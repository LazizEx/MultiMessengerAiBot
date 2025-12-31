// Workers/TelegramBotWorker.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MultiMessengerAiBot.Data;
using MultiMessengerAiBot.Services;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using VkNet.Model;
//using Telegram.Bot.Types.InputFiles;

namespace MultiMessengerAiBot.Workers;

public class TelegramBotWorker : BackgroundService
{
    private readonly TelegramBotClient _bot;
    private readonly IBotService _imageService;
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly string _hostUrl;
    private readonly IConfiguration _cfg;
    private readonly string _token;
    private readonly string _walletNumber;
    //private readonly AppDbContext _db;
    private readonly IServiceProvider _services;           // ← вместо AppDbContext

    // Защита от спама: максимум 1 запрос каждые 8 секунд на пользователя
    private readonly Dictionary<long, DateTime> _lastRequest = new();
    private static readonly ConcurrentDictionary<long, string> UserPhotoContext = new(); // Хранит последнее фото пользователя (chatId → fileId)

    public TelegramBotWorker(IConfiguration cfg, IBotService imageService, IServiceProvider services, ILogger<TelegramBotWorker> logger)
    {
        _token = cfg["BotTokens:Telegram"] ?? throw new InvalidOperationException("Telegram token missing");
        _bot = new TelegramBotClient(_token);
        _imageService = imageService;
        _logger = logger;
        _cfg = cfg;
        _hostUrl = cfg["HostUrl"]!.TrimEnd('/');
        _walletNumber = cfg["YooMoney:WalletNumber"] ?? throw new InvalidOperationException("Missing wallet");
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var webhookUrl = $"{_hostUrl}/telegram/webhook";

        try
        {
            await _bot.DeleteWebhook(dropPendingUpdates: true, ct);
            
            var info = await _bot.GetWebhookInfo(ct);
            if (info.Url != webhookUrl)
            {
                await _bot.SetWebhook(
                    url: webhookUrl, 
                    allowedUpdates: new[] 
                    { 
                        Telegram.Bot.Types.Enums.UpdateType.Message,
                        Telegram.Bot.Types.Enums.UpdateType.CallbackQuery,
                        Telegram.Bot.Types.Enums.UpdateType.InlineQuery,
                    }, 
                    cancellationToken: ct);

                _logger.LogInformation("Webhook установлен: {Url}", webhookUrl);
            }
            else
            {
                _logger.LogInformation("Webhook уже актуален: {Url}", info.Url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось установить webhook");
        }
    }


    public void MapTelegramWebhook(IEndpointRouteBuilder app)
    {
        app.MapPost("/telegram/webhook", async (HttpRequest request, CancellationToken ct) =>
        {
            var json = await new StreamReader(request.Body).ReadToEndAsync(ct);
            var update = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (update == null) return Results.Ok();
            
            // Создаём scope для всего обработчика
            await using var scope = _services.CreateAsyncScope();
            await using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();


            // === ОБЫЧНЫЕ СООБЩЕНИЯ ===
            if (update.Message is { } message && message.From is { } from)
            {
                var chatId = update.Message.Chat.Id;

                // === 1. ПОЛУЧЕНИЕ КОНТАКТА (для локализации) ===
                if (message.Contact is { } contact)
                {
                    var user = await db.Users.FindAsync(chatId) ?? new Data.User { TelegramId = chatId };
                    user.PhoneNumber = contact.PhoneNumber;
                    user.Currency = (contact.PhoneNumber.StartsWith("+998") || contact.PhoneNumber.StartsWith("998")) ? "UZS" : "RUB";
                    if (user.TelegramId == 0) db.Users.Add(user);
                    await db.SaveChangesAsync(ct);
                    await _bot.SendMessage(chatId, $"Регион определён: {user.Currency}", cancellationToken: ct);
                    return Results.Ok();
                }

                // === 2. ПОЛЬЗОВАТЕЛЬ ОТПРАВИЛ ФОТО (img2img) ===
                if (message.Photo is { Length: > 0 } photoArray)
                {
                    // Берём самое качественное фото
                    var fileId = photoArray[^1].FileId;
                    // Сохраняем file_id в "контекст" пользователя (простой in-memory словарь)
                    UserPhotoContext[chatId] = fileId;
                    await _bot.SendMessage(chatId, "Фото получил! Теперь отправь текст (промпт), что сделать с этим изображением:", cancellationToken: ct);
                    return Results.Ok();
                }

                // === 3. ПОЛЬЗОВАТЕЛЬ ОТПРАВИЛ ТЕКСТ, а у нас есть его фото ===
                if (message.Text is not { Length: > 0 } text) return Results.Ok();
                var prompt = text.Trim();

                // /start и реферальная система
                if (prompt.Equals("/start", StringComparison.OrdinalIgnoreCase) || prompt.StartsWith("/start ref_"))
                {
                    var user = await db.Users.FindAsync(chatId);

                    // Если пользователя ещё нет — создаём с базовыми данными
                    if (user == null)
                    {
                        user = new Data.User
                        {
                            TelegramId = chatId,
                            Credits = 1,
                            FirstName = message.From?.FirstName,
                            LastName = message.From?.LastName,
                            Username = message.From?.Username
                        };
                        db.Users.Add(user);
                    }

                    // ОБРАБАТЫВАЕМ РЕФЕРАЛКУ СНАЧАЛА
                    bool isNewReferral = false;
                    long? referrerId = null;
                    if (prompt.StartsWith("/start ref_") && user.ReferredBy == null)
                    {
                        var refCode = prompt["/start ref_".Length..].Trim();
                        var referrer = await db.Users.FirstOrDefaultAsync(u => u.ReferralCode == refCode, ct);
                        if (referrer != null && referrer.TelegramId != chatId)
                        {
                            referrerId = referrer.TelegramId;
                            user.ReferredBy = referrerId;

                            // Начисляем бонусы ТОЛЬКО при новом приглашении
                            user.Credits += 2;
                            referrer.Credits += 2;
                            isNewReferral = true;
                            await db.SaveChangesAsync(ct); // Сохраняем сразу, чтобы избежать гонок
                        }
                    }

                    // Генерируем реф-код ТОЛЬКО если его ещё нет
                    if (string.IsNullOrEmpty(user.ReferralCode))
                    {
                        user.ReferralCode = Guid.NewGuid().ToString("N")[..8].ToUpper();
                        await db.SaveChangesAsync(ct);
                    }

                    // Сохраняем ВСЁ ОДНИМ SaveChanges (важно!)
                    //await db.SaveChangesAsync(ct);

                    // === ЕСЛИ ТЕЛЕФОН ЕЩЁ НЕТ — ЗАПРАШИВАЕМ ===
                    if (string.IsNullOrEmpty(user.PhoneNumber))
                    {
                        var requestPhoneKeyboard = new ReplyKeyboardMarkup(new[]
                        {
                            new KeyboardButton("📱 Поделиться номером") { RequestContact = true }
                        })
                        {
                            ResizeKeyboard = true,
                            OneTimeKeyboard = true
                        };

                        //await _bot.SendMessage(chatId,
                        //    $"Привет, {user.FirstName ?? "друг"}!\n\n" +
                        //    $"Чтобы продолжить, поделись номером телефона — это нужно для оплаты и безопасности.\n\n" +
                        //    $"У тебя {user.Credits} генераций\n\n" +
                        //    $"Твоя реферальная ссылка: https://t.me/{(await _bot.GetMe(ct)).Username}?start=ref_{user.ReferralCode}\n\n" +
                        //    $"/buy — купить генерации\n/balance — проверить остаток",
                        //    replyMarkup: requestPhoneKeyboard,  cancellationToken: ct);
                        var welcomeText = new StringBuilder();
                        welcomeText.AppendLine($"Привет, {user.FirstName ?? "друг"}!");
                        welcomeText.AppendLine();
                        welcomeText.AppendLine($"У тебя {user.Credits} генераций");

                        if (isNewReferral)
                        {
                            welcomeText.AppendLine("🎉 Вы зарегистрированы по реферальной ссылке!");
                            welcomeText.AppendLine("Вы и ваш друг получили +2 генерации!");
                            welcomeText.AppendLine();
                        }

                        welcomeText.AppendLine($"Твоя реферальная ссылка:");
                        welcomeText.AppendLine($"https://t.me/{(await _bot.GetMe(ct)).Username}?start=ref_{user.ReferralCode}");
                        welcomeText.AppendLine();
                        welcomeText.AppendLine("/buy — купить генерации");
                        welcomeText.AppendLine("/balance — проверить остаток");

                        await _bot.SendMessage(chatId, welcomeText.ToString(), replyMarkup: requestPhoneKeyboard, cancellationToken: ct);
                        return Results.Ok();
                    }

                    // Уведомляем реферера ТОЛЬКО если это новый реферал
                    if (isNewReferral && referrerId.HasValue)
                    {
                        try
                        {
                            await _bot.SendMessage(referrerId.Value,
                                "🎉 По твоей реферальной ссылке пришёл новый пользователь!\nВы оба получили +2 генерации!",
                                cancellationToken: ct);
                        }
                        catch
                        {
                            // Если реферер заблокировал бота — игнорируем
                        }
                    }

                    //var botName = (await _bot.GetMe(ct)).Username;
                    //var refLink = $"https://t.me/{botName}?start=ref_{user.ReferralCode}";

                    //await _bot.SendMessage(chatId,
                    //    $"С возвращением, {user.FirstName ?? "друг"}!\n\n" +
                    //    $"У тебя {user.Credits} генераций\n\n" +
                    //    $"Реферальная ссылка:\n{refLink}\nПригласи друга — оба получите +2 кредита!\n\n" +
                    //    $"/buy — купить генерации\n/balance — проверить остаток",
                    //    cancellationToken: ct);

                    //// Убираем клавиатуру после первого сообщения
                    //await _bot.SendMessage(chatId, ".", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    //return Results.Ok();
                    
                    // Обычное приветствие для вернувшихся
                    var botName = (await _bot.GetMe(ct)).Username;
                    var refLink = $"https://t.me/{botName}?start=ref_{user.ReferralCode}";

                    await _bot.SendMessage(chatId,
                        $"С возвращением, {user.FirstName ?? "друг"}!\n\n" +
                        $"У тебя {user.Credits} генераций\n\n" +
                        (isNewReferral ? "🎉 Спасибо за регистрацию по приглашению!\n\n" : "") +
                        $"Твоя реферальная ссылка:\n{refLink}\n" +
                        $"Пригласи друга — оба получите +2 кредита!\n\n" +
                        $"/buy — купить генерации\n/balance — проверить остаток",
                        cancellationToken: ct);

                    // Убираем клавиатуру
                    //await _bot.SendMessage(chatId, ".", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                    return Results.Ok();
                }

                // /balance
                if (prompt.Equals("/balance", StringComparison.OrdinalIgnoreCase))
                {
                    var user = await db.Users.FindAsync(chatId) ?? new Data.User { TelegramId = chatId, Credits = 1 };
                    if (user.TelegramId == 0) db.Users.Add(user);
                    await db.SaveChangesAsync(ct);
                    await _bot.SendMessage(chatId, $"У тебя {user.Credits} генераций", cancellationToken: ct);
                    return Results.Ok();
                }

                //// /buy — оплата через личный YooMoney кошелёк
                //if (prompt.Equals("/buy", StringComparison.OrdinalIgnoreCase))
                //{
                //    var user = await db.Users.FindAsync(chatId) ?? new Data.User { TelegramId = chatId, Credits = 1 };
                //    if (user.TelegramId == 0) db.Users.Add(user);
                //    await db.SaveChangesAsync(ct);

                //    var label = $"pack5_{chatId}";
                //    var paymentUrl = $"https://yoomoney.ru/quickpay/confirm.xml" +
                //                     $"?receiver={_walletNumber}" +
                //                     $"&quickpay-form=shop" +
                //                     $"&targets=Покупка+5+генераций+AI-изображений" +
                //                     $"&paymentType=PC" +
                //                     $"&sum=500" +
                //                     $"&label={label}" +
                //                     $"&successURL=https://t.me/{(await _bot.GetMe(ct)).Username}";

                //    var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl("Оплатить 500 ₽ → 5 генераций", paymentUrl));

                //    await _bot.SendMessage(chatId,
                //        $"У тебя {user.Credits} генераций\n\nКупи 5 за 500 ₽:",
                //        replyMarkup: keyboard, cancellationToken: ct);
                //    return Results.Ok();
                //}

                // /buy — открываем Mini App
                if (prompt.Equals("/buy", StringComparison.OrdinalIgnoreCase))
                {
                    var user = await db.Users.FindAsync(chatId);
                    if (user == null)
                    {
                        user = new Data.User { TelegramId = chatId, Credits = 1 };
                        db.Users.Add(user);
                        await db.SaveChangesAsync(ct);
                    }

                    // Формируем ссылку на DonatePay с передачей Telegram ID в параметре custom
                    var donatePayUrl = $"https://donatepay.ru/don/1450922?custom={chatId}";

                    var keyboard = new InlineKeyboardMarkup(new[]
                    {
                    InlineKeyboardButton.WithWebApp("💳 Докупить генерации", new WebAppInfo() { Url = $"{_hostUrl}/buy.html" }),
                    // Вторую кнопку добавим позже
                    //InlineKeyboardButton.WithUrl("💰 Поддержать бота любой суммой", donatePayUrl),
                    });

                    await _bot.SendMessage(chatId,
                        $"У тебя {user.Credits} генераций\n\nВыбери способ оплаты:",
                        replyMarkup: keyboard, cancellationToken: ct);

                    return Results.Ok();
                }

                if (prompt.StartsWith("/"))
                {
                    return Results.Ok();
                }

                // === 4. ГЕНЕРАЦИЯ ИЗОБРАЖЕНИЯ (txt2img или img2img) ===
                var currentUser = await db.Users.FindAsync(chatId) ?? new Data.User { TelegramId = chatId, Credits = 1 };
                if (currentUser.TelegramId == 0) { db.Users.Add(currentUser); await db.SaveChangesAsync(ct); }

                // Антиспам
                if (_lastRequest.TryGetValue(chatId, out var last) && (DateTime.UtcNow - last).TotalSeconds < 8)
                {
                    await _bot.SendMessage(chatId, "Подожди 8 секунд", cancellationToken: ct);
                    return Results.Ok();
                }
                _lastRequest[chatId] = DateTime.UtcNow;

                // Проверка кредитов
                if (currentUser.Credits <= 0)
                {
                    await _bot.SendMessage(chatId, "Генерации закончились\nПополни баланс: /buy", cancellationToken: ct);
                    return Results.Ok();
                }

                // Анти-абьюз: не больше 4 запросов за 5 минут
                var recent = await db.RequestLogs.CountAsync(l =>
                    l.UserId == chatId && l.Timestamp > DateTime.UtcNow.AddMinutes(-5), ct);
                if (recent > 4)
                {
                    await _bot.SendMessage(chatId, "Слишком много запросов. Подожди пару минут", cancellationToken: ct);
                    return Results.Ok();
                }

                // Прогресс-GIF
                await _bot.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: ct);
                var loadingMsg = await _bot.SendDocument(chatId,
                    document: InputFile.FromUri($"{_hostUrl}/ProgressBar.gif"),
                    caption: "Генерирую…", cancellationToken: ct);

                string? resultImageUrl = null;
                bool success = false;

                try
                {
                    // img2img — если есть сохранённое фото
                    if (UserPhotoContext.TryRemove(chatId, out var fileId))
                    {
                        var file = await _bot.GetFile(fileId, ct);
                        var photoUrl = $"https://api.telegram.org/file/bot{_token}/{file.FilePath}";
                        resultImageUrl = await _imageService.GenerateFromImageAsync(photoUrl, prompt, "Nano_Banana", ct);
                    }
                    // txt2img
                    else
                    {
                        resultImageUrl = await _imageService.GetImageUrlAsync(prompt, "Nano_Banana", ct);
                    }

                    // Списываем кредит
                    if (!string.IsNullOrEmpty(resultImageUrl))
                    {
                        currentUser.Credits--;
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка генерации");
                    db.RequestLogs.Add(new RequestLog { UserId = chatId, Timestamp = DateTime.UtcNow, Action = "generate", Success = false });
                    await db.SaveChangesAsync(ct);
                }
                // Лог
                db.RequestLogs.Add(new RequestLog { UserId = chatId, Timestamp = DateTime.UtcNow, Action = "generate", Success = success });
                await db.SaveChangesAsync(ct);

                // Удаляем GIF
                try { await _bot.DeleteMessage(chatId, loadingMsg.MessageId, ct); } catch { }

                // Отправляем результат
                if (!string.IsNullOrEmpty(resultImageUrl))
                {
                    InputFile photo = resultImageUrl.StartsWith("data:image")
                        ? InputFile.FromStream(StreamFromBase64(resultImageUrl), "result.png")
                        : InputFile.FromUri(resultImageUrl);

                    //var model = prompt.StartsWith("/pro ") ? "pro" : prompt.StartsWith("/flex ") ? "flex" : "pro";
                    //var cleanPrompt = prompt.Replace("/pro ", "").Replace("/flex ", "").Trim();

                    //var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Ещё одну!", $"again:fdsfsd"));

                    var caption = prompt.Length <= 200 ? prompt : "Готово!";
                    await _bot.SendPhoto(
                        chatId, 
                        photo, 
                        caption: caption, 
                        //replyMarkup: keyboard,
                        cancellationToken: ct);
                }
                else
                {
                    await _bot.SendMessage(chatId, "Не удалось сгенерировать изображение\nПопробуй позже или напиши /buy", cancellationToken: ct);
                }
            }

            // === КНОПКА "Ещё одну!" (CallbackQuery) ===
            else if (update.CallbackQuery is { Data: { } callbackData } callback)
            {
                if (callbackData.StartsWith("again:"))
                {
                    var parts = callbackData["again:".Length..].Split(':');
                    var repeatPrompt = parts[0];
                    var repeatModel = parts.Length > 1 ? parts[1] : "pro";

                    await _bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
                    await _bot.SendChatAction(callback.Message!.Chat.Id, ChatAction.UploadPhoto, cancellationToken: ct);

                    var image = await _imageService.GetImageUrlAsync(repeatPrompt, repeatModel, ct);
                    if (image?.StartsWith("data:image") == true)
                    {
                        var base64 = image.Split(',')[1];
                        var bytes = Convert.FromBase64String(base64);
                        await using var stream = new MemoryStream(bytes) { Position = 0 };

                        var keyboard = new InlineKeyboardMarkup(
                            InlineKeyboardButton.WithCallbackData("Ещё одну!", $"again:{repeatPrompt}:{repeatModel}"));

                        await _bot.SendPhoto(
                            chatId: callback.Message!.Chat.Id,
                            photo: InputFile.FromStream(stream, "nano.png"),
                            caption: repeatPrompt.Length <= 100 ? repeatPrompt : "Ещё одна версия",
                            replyMarkup: keyboard,
                            cancellationToken: ct);
                    }
                }
            }

            return Results.Ok();
        })
        .WithName("TelegramWebhook")
        .ExcludeFromDescription(); // ←←← ЧТОБЫ SWAGGER НЕ РУГАЛСЯ
    }

    private async Task<Data.User> GetOrCreateUser(long chatId)
    {
        await using var scope = _services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.FindAsync(chatId);
        if (user == null)
        {
            user = new Data.User { TelegramId = chatId, Credits = 1 };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        return user;
    }

    private async Task _botPhoto(long chatId, InputFile photo, string caption, CancellationToken ct)
    {
        var shortCaption = caption.Length <= 100 ? caption : "Готово!";
        // var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Ещё одну!", $"again:{caption}"));
        await _bot.SendPhoto(chatId, photo, caption: shortCaption, /*replyMarkup: keyboard,*/ cancellationToken: ct);
    }

    // Добавь два helper-метода в класс:
    private static MemoryStream StreamFromBase64(string dataUri)
    {
        var base64 = dataUri.Split(',')[1];
        var bytes = Convert.FromBase64String(base64);
        return new MemoryStream(bytes) { Position = 0 };
    }

    private async Task SendPhotoWithCaption(long chatId, InputFile photo, string prompt, CancellationToken ct)
    {
        var caption = prompt.Length <= 100 ? prompt : "Готово!";
        await _bot.SendPhoto(chatId, photo, caption: caption, cancellationToken: ct);
    }
}