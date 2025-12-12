// Workers/TelegramBotWorker.cs
using Microsoft.AspNetCore.Mvc;
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
//using Telegram.Bot.Types.InputFiles;

namespace MultiMessengerAiBot.Workers;

public class TelegramBotWorker : BackgroundService
{
    private readonly TelegramBotClient _bot;
    private readonly IBotService _imageService;
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly string _hostUrl;
    private static readonly ConcurrentDictionary<long, string> UserPhotoContext = new(); // Хранит последнее фото пользователя (chatId → fileId)
    private readonly string _token;
    private readonly string _walletNumber;
    private readonly AppDbContext _db;

    // Защита от спама: максимум 1 запрос каждые 8 секунд на пользователя
    private readonly Dictionary<long, DateTime> _lastRequest = new();

    public TelegramBotWorker(IConfiguration cfg, IBotService imageService, ILogger<TelegramBotWorker> logger, AppDbContext db)
    {
        _token = cfg["BotTokens:Telegram"] ?? throw new InvalidOperationException("Telegram token missing");
        _bot = new TelegramBotClient(_token);
        _imageService = imageService;
        _logger = logger;
        _hostUrl = cfg["HostUrl"]!.TrimEnd('/');
        _walletNumber = cfg["YooMoney:WalletNumber"] ?? throw new InvalidOperationException("Missing wallet");
        _db = db;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var webhookUrl = $"{_hostUrl}/telegram/webhook";

        try
        {
            var info = await _bot.GetWebhookInfo(ct);
            if (info.Url != webhookUrl)
            {
                await _bot.SetWebhook(url: webhookUrl, allowedUpdates: new[] { UpdateType.Message }, cancellationToken: ct);

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
        _ = app.MapPost("/telegram/webhook", async (HttpRequest request, CancellationToken ct) =>
        {
            var json = await new StreamReader(request.Body).ReadToEndAsync(ct);
            var update = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (update == null) return Results.Ok();


            // === ОБЫЧНЫЕ СООБЩЕНИЯ ===
            if (update.Message is { } message && message.From is { } from)
            {
                var chatId = update.Message.Chat.Id;

                // === 1. ПОЛУЧЕНИЕ КОНТАКТА (для локализации) ===
                if (message.Contact is { } contact)
                {
                    var user = await GetOrCreateUser(chatId);
                    user.PhoneNumber = contact.PhoneNumber;
                    user.Currency = contact.PhoneNumber.StartsWith("+998") ? "UZS" : "RUB";
                    await _db.SaveChangesAsync(ct);
                    await _bot.SendMessage(chatId, $"Регион определён: {user.Currency}", ct);
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

                if (message.Text is { Length: > 0 } promptText && UserPhotoContext.TryGetValue(chatId, out var savedFileId))
                {

                    var user = await GetOrCreateUser(chatId);

                    if (user.Credits <= 0)
                    {
                        await _bot.SendMessage(chatId, "Генерации закончились 😔\nКупи пакет: /buy", ct);
                        return Results.Ok();
                    }

                    user.Credits--;
                    await _db.SaveChangesAsync(ct);

                    // Логируем использование
                    _db.RequestLogs.Add(new RequestLog
                    {
                        UserId = chatId,
                        Timestamp = DateTime.UtcNow,
                        Action = "generate",
                        Success = true
                    });
                    await _db.SaveChangesAsync(ct);

                    var prompt = promptText.Trim();

                    // Антиспам и бесплатный лимит — оставляем как у тебя было
                    if (_lastRequest.TryGetValue(chatId, out var last) &&
                        (DateTime.UtcNow - last).TotalSeconds < 8)
                    {
                        await _bot.SendMessage(chatId, "Подожди 8 секунд", cancellationToken: ct);
                        return Results.Ok();
                    }
                    _lastRequest[chatId] = DateTime.UtcNow;

                    await _bot.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: ct);
                    var loading = await _bot.SendDocument(chatId,
                        InputFile.FromUri($"{_hostUrl}/ProgressBar.gif"),
                        caption: "Обрабатываю твоё фото…", cancellationToken: ct);

                    // Скачиваем фото из Telegram
                    var file = await _bot.GetFile(savedFileId, ct);
                    var photoUrl = $"https://api.telegram.org/file/bot{_token}/{file.FilePath}";

                    // Удаляем из контекста — фото использовано один раз
                    UserPhotoContext.Remove(chatId, out var v);

                    // ←←← ВЫЗЫВАЕМ СЕРВИС (пока заглушка, потом подключишь настоящий img2img)
                    var resultImageUrl = await _imageService.GenerateFromImageAsync(photoUrl, prompt, ct);

                    await _bot.DeleteMessage(chatId, loading.MessageId, ct);

                    if (!string.IsNullOrEmpty(resultImageUrl))
                    {
                        InputFile resultPhoto = resultImageUrl.StartsWith("data:image")
                            ? InputFile.FromStream(StreamFromBase64(resultImageUrl), "resultImageUrl")

                            : InputFile.FromUri(resultImageUrl);

                        await _bot.SendPhoto(chatId, resultPhoto, caption: prompt, cancellationToken: ct);
                    }
                    else
                    {
                        await _bot.SendMessage(chatId, "Не получилось обработать фото", cancellationToken: ct);
                    }

                    return Results.Ok();
                }


                // === 3. ОБЫЧНЫЙ ТЕКСТ БЕЗ ФОТО (старое поведение) ===
                if (message.Text is { Length: > 0 } text)
                {
                    var prompt = text.Trim();

                    // /start
                    if (prompt.Equals("/start", StringComparison.OrdinalIgnoreCase))
                    {
                        await _bot.SendMessage(chatId: chatId, text: """
                            Привет! Пиши любое описание — нарисую за 5 секунд

                            • nano banana
                            • кот в космосе
                            • киберпанк-логотип

                            /pro  — максимальное качество
                            /flex — быстрее и дешевле
                            """,
                            cancellationToken: ct);
                        return Results.Ok();
                    }

                    if (prompt is "/pro" or "/flex")
                    {
                        await _bot.SendMessage(chatId: chatId, text: $"Режим: {prompt[1..].ToUpper()}", cancellationToken: ct);
                        return Results.Ok();
                    }

                    if (prompt.Equals("/buy", StringComparison.OrdinalIgnoreCase))
                    {
                        var user = await GetOrCreateUser(chatId);

                        var label = $"pack5_{chatId}"; // уникальный payload
                        var quickpayUrl = $"https://yoomoney.ru/quickpay/confirm.xml" +
                                          $"?receiver={_walletNumber}" +
                                          $"&quickpay-form=shop" +
                                          $"&targets=Покупка+5+генераций+изображений" +
                                          $"&paymentType=PC" +          // PC = карта, AC = карта, SB = быстрый платёж
                                          $"&sum=500" +                 // 500 RUB
                                          $"&label={label}" +
                                          $"&successURL=https://t.me/{(await _bot.GetMe(ct)).Username}";

                        var keyboard = new InlineKeyboardMarkup(
                            InlineKeyboardButton.WithUrl("💳 Оплатить 500 ₽ за 5 генераций", quickpayUrl));

                        await _bot.SendMessage(chatId,
                            $"У тебя {user.Credits} генераций.\nКупи пакет из 5 за 500 ₽:",
                            replyMarkup: keyboard, ct);

                        return Results.Ok();
                    }

                    // Антиспам
                    if (_lastRequest.TryGetValue(chatId, out var last) && (DateTime.UtcNow - last).TotalSeconds < 8)
                    {
                        await _bot.SendMessage(chatId: chatId, text: "Подожди 8 секунд", cancellationToken: ct);
                        return Results.Ok();
                    }
                    _lastRequest[chatId] = DateTime.UtcNow;

                    // Прогресс-бар
                    await _bot.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: ct);

                    // ←←← ВОЛШЕБНЫЙ GIF-ПРОГРЕСС ←←←
                    var loadingMessage = await _bot.SendDocument(
                        chatId: chatId,
                        document: InputFile.FromUri($"{_hostUrl}/ProgressBar.gif"),
                        caption: "Генерирую твою картинку…",
                        cancellationToken: ct);


                    var model = prompt.StartsWith("/pro ") ? "pro" : prompt.StartsWith("/flex ") ? "flex" : "pro";
                    var cleanPrompt = prompt.Replace("/pro ", "").Replace("/flex ", "").Trim();

                    var imageDataUri = await _imageService.GetImageUrlAsync(cleanPrompt, model, ct);

                    // Удаляем GIF
                    try
                    {
                        await _bot.DeleteMessage(chatId: chatId, messageId: loadingMessage.MessageId, cancellationToken: ct);
                    }
                    catch { }
                    await _bot.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: ct);

                    if (!string.IsNullOrEmpty(imageDataUri))
                    {
                        try
                        {
                            try
                            {
                                InputFile photo = imageDataUri.StartsWith("data:image")
                                    ? InputFile.FromStream(StreamFromBase64(imageDataUri), "image.png")
                                    : InputFile.FromUri(imageDataUri);

                                await SendPhotoWithCaption(chatId, photo, cleanPrompt, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to send image");
                                await _bot.SendMessage(chatId, "Ошибка при отправке картинки", cancellationToken: ct);
                            }


                            //if (imageDataUri?.StartsWith("data:image") == true)
                            //{
                            //    var base64 = imageDataUri.Split(',')[1];
                            //    var bytes = Convert.FromBase64String(base64);
                            //    await using var stream = new MemoryStream(bytes) { Position = 0 };

                            //    //var keyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Ещё одну!", $"again:{cleanPrompt}:{model}"));

                            //    await _botPhoto(
                            //        chatId: chatId,
                            //        photo: InputFile.FromStream(stream, "nano.png"),
                            //        caption: cleanPrompt.Length <= 100 ? cleanPrompt : "Готово!",
                            //        //replyMarkup: keyboard,
                            //        ct);
                            //}
                            //else if (Uri.TryCreate(imageDataUri, UriKind.Absolute, out _))
                            //{
                            //    // Обычная ссылка — шлём напрямую
                            //    await _botPhoto(chatId, InputFile.FromUri(imageDataUri), caption: cleanPrompt.Length <= 100 ? cleanPrompt : "Готово!", ct);
                            //}
                            //else
                            //{
                            //    await _bot.SendMessage(chatId: chatId, text: "Неподдерживаемый формат изображения)", cancellationToken: ct);
                            //}
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Ошибка при отправке изображения");
                            await _bot.SendMessage(chatId, "Ой, что-то пошло не так при отправке картинки", cancellationToken: ct);
                        }

                    }
                    else
                    {
                        await _bot.SendMessage(chatId: chatId, text: "Не удалось сгенерировать изображение (проверь баланс или попробуй позже)", cancellationToken: ct);
                    }
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