using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MultiMessengerAiBot.Data;
using System.Text.Json;
using Telegram.Bot;

namespace MultiMessengerAiBot.Services;

public class DonatePayPollingService : BackgroundService
{
    private readonly ILogger<DonatePayPollingService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _cfg;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private long _lastId = 0;

    public DonatePayPollingService(ILogger<DonatePayPollingService> logger, IConfiguration cfg, IServiceProvider services)
    {
        _logger = logger;
        _cfg = cfg;
        _services = services;
        _apiKey = cfg["DonatePay:ApiKey"] ?? throw new InvalidOperationException("DonatePay ApiKey missing");
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "MultiMessengerAiBot/1.0");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://donatepay.ru/api/v1/transactions?access_token={_apiKey}";
                var response = await _http.GetAsync(url, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("DonatePay: TooManyRequests — ждём 60 сек");
                    await Task.Delay(60000, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("DonatePay HTTP ошибка: {Status}", response.StatusCode);
                    await Task.Delay(30000, ct);
                    continue;
                }

                var jsonText = await response.Content.ReadAsStringAsync(ct);
                var json = JsonDocument.Parse(jsonText).RootElement;

                if (json.GetProperty("status").GetString() != "success")
                {
                    _logger.LogWarning("DonatePay API: {Message}", json.GetProperty("message").GetString() ?? "unknown error");
                    await Task.Delay(30000, ct);
                    continue;
                }

                var transactions = json.GetProperty("data").EnumerateArray();

                foreach (var tx in transactions)
                {
                    var id = tx.GetProperty("id").GetInt64();
                    if (id <= _lastId) continue;
                    _lastId = id;

                    var status = tx.GetProperty("status").GetString();
                    if (status != "user") continue; // success донаты имеют "user"

                    // custom — это наш Telegram ID
                    var custom = tx.GetProperty("vars").GetProperty("name").GetString();
                    if (string.IsNullOrEmpty(custom) || !long.TryParse(custom, out var userId)) continue;

                    var amount = decimal.Parse(tx.GetProperty("sum").GetString()!);
                    var username = tx.GetProperty("vars").GetProperty("name").GetString() ?? "Аноним";

                    await using var scope = _services.CreateAsyncScope();
                    await using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var bot = scope.ServiceProvider.GetRequiredService<TelegramBotClient>();

                    var user = await db.Users.FindAsync(userId);
                    if (user == null) continue;

                    var credits = amount switch
                    {
                        >= 500 => 10,
                        >= 300 => 5,
                        >= 100 => 2,
                        _ => 1
                    };

                    user.Credits += credits;
                    await db.SaveChangesAsync();

                    _logger.LogInformation("DonatePay: {Username} ({UserId}) +{Credits} генераций за {Amount} руб", username, userId, credits, amount);

                    // Уведомление пользователю
                    await bot.SendMessage(userId,
                        $"❤️ Спасибо за поддержку, {username}!\nТебе добавлено <b>{credits} генераций</b>\nТеперь у тебя: {user.Credits}",
                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: ct);

                    // Уведомление админу
                    var adminIdStr = _cfg["AdminTelegramId"];
                    if (long.TryParse(adminIdStr, out var adminId) && adminId != 0)
                    {
                        await bot.SendMessage(adminId,
                            $"💸 Новый донат!\nОт: {username} (ID: {userId})\nСумма: {amount} руб\nДобавлено: {credits} генераций",
                            cancellationToken: ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка polling DonatePay");
            }

            await Task.Delay(35000, ct); // 35 сек — чтобы не попасть в TooManyRequests
        }
    }
}