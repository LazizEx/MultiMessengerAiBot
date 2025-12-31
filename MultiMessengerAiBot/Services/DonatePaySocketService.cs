using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MultiMessengerAiBot.Data;
using Microsoft.EntityFrameworkCore;
using SocketIOClient;
using System.Text.Json;

namespace MultiMessengerAiBot.Services;

public class DonatePaySocketService : BackgroundService
{
    private readonly ILogger<DonatePaySocketService> _logger;
    private readonly IServiceProvider _services;
    private readonly string _token; // API ключ из DonatePay

    public DonatePaySocketService(ILogger<DonatePaySocketService> logger, IConfiguration cfg, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
        _token = cfg["DonatePay:ApiKey"] ?? throw new InvalidOperationException("DonatePay ApiKey missing");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var client = new SocketIOClient.SocketIO("wss://centrifugo.donatepay.ru/connection/websocket", new SocketIOOptions
        {
            Query = new Dictionary<string, string>
            {
                { "token", _token }
            },
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            ReconnectionDelay = 5000
        });

        client.OnConnected += async (sender, e) =>
        {
            _logger.LogInformation("DonatePay Socket подключён");
        };

        client.OnDisconnected += (sender, e) =>
        {
            _logger.LogWarning("DonatePay Socket отключён: {Reason}", e);
        };

        client.OnReconnectFailed += (sender, e) =>
        {
            _logger.LogError("DonatePay Socket: переподключение не удалось");
        };

        // Основное событие — новый донат
        client.On("donation", async response =>
        {
            try
            {
                var jsonStr = response.GetValue<string>();
                var json = JsonDocument.Parse(jsonStr).RootElement;

                var status = json.GetProperty("status").GetString();
                if (status != "success") return;

                var custom = json.GetProperty("custom").GetString();
                if (!long.TryParse(custom, out var userId)) return;

                var amount = json.GetProperty("amount").GetDecimal();

                await using var scope = _services.CreateAsyncScope();
                await using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var user = await db.Users.FindAsync(userId);
                if (user == null) return;

                var credits = amount switch
                {
                    >= 500 => 10,
                    >= 300 => 5,
                    >= 100 => 2,
                    _ => 1
                };

                user.Credits += credits;
                await db.SaveChangesAsync();

                _logger.LogInformation("DonatePay: пользователю {UserId} добавлено {Credits} генераций за {Amount} руб", userId, credits, amount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки доната DonatePay");
            }
        });

        await client.ConnectAsync(ct);

        // Держим сервис живым
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
        }
    }
}