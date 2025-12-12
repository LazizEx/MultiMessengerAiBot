using MultiMessengerAiBot.Services;
using MultiMessengerAiBot.Workers;
using System;
using Telegram.Bot;
using Microsoft.EntityFrameworkCore;
using MultiMessengerAiBot.Data;
using System.Text;
using SQLitePCL;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация (secrets.json + appsettings.json)
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>();

Batteries.Init();
//var cfg = builder.Configuration;
//string tgToken = cfg["BotTokens:Telegram"] ?? throw new InvalidOperationException("TelegramBotToken is not configured.");
//string vkToken = cfg["BotTokens:Vk"] ?? throw new InvalidOperationException("TelegramBotToken is not configured.");
//string viberToken = cfg["BotTokens:Viber"] ?? throw new InvalidOperationException("TelegramBotToken is not configured.");
//string orKey = cfg["OpenRouter:Key"] ?? throw new InvalidOperationException("TelegramBotToken is not configured.");
//string host = cfg["HostUrl"] ?? throw new InvalidOperationException("TelegramBotToken is not configured.");

// Add services to the container.

//---------------------------------------------------------------------
// HttpClient для OpenRouter
builder.Services.AddHttpClient("OpenRouter", client =>
{
    client.BaseAddress = new Uri("https://openrouter.ai");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["OpenRouter:Key"]}");
    client.DefaultRequestHeaders.Add("HTTP-Referer", builder.Configuration["HostUrl"] ?? "http://localhost:5000");
    client.DefaultRequestHeaders.Add("X-Title", "MultiMessengerAiBot");
});

// Сервисы
builder.Services.AddSingleton<IBotService, OpenRouterImageService>();

builder.Services.AddSingleton<TelegramBotClient>(sp =>
{
    var token = builder.Configuration["BotTokens:Telegram"] ?? throw new InvalidOperationException("Missing token");
    return new TelegramBotClient(token);  // v22+ поддерживает async по умолчанию
});

// В builder.Services
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=bot.db")); // файл БД в корне проекта

// Telegram
//builder.Services.AddHostedService<TelegramBotWorker>();
// В Program.cs — добавь эту строку:
builder.Services.AddSingleton<TelegramBotWorker>();


builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();



// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles(); // ← делает wwwroot доступным
app.MapFallbackToFile("ProgressBar.gif"); // автоматически отдаёт из wwwroot

// Получаем экземпляр worker через DI
var telegramWorker = app.Services.GetRequiredService<TelegramBotWorker>();

// Теперь вызываем НЕстатический метод
telegramWorker.MapTelegramWebhook(app);

//app.UseHttpsRedirection();

//app.UseAuthorization();

app.MapControllers();

// ←←← Главная магия — регистрируем webhook-эндпоинт
//var botWorker = app.Services.GetRequiredService<TelegramBotWorker>();
//botWorker.MapTelegramWebhook(app);

// Webhook для YooMoney уведомлений
app.MapPost("/yoomoney/notify", async (HttpRequest request, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    var form = await request.ReadFormAsync();
    var notificationSecret = cfg["YooMoney:NotificationSecret"] ?? "";

    // Параметры из уведомления
    var sha1Hash = form["sha1_hash"].ToString();
    var operationId = form["operation_id"].ToString();
    var amount = form["amount"].ToString();
    var withdrawAmount = form["withdraw_amount"].ToString();
    var label = form["label"].ToString(); // наш payload: pack5_123456789

    // Проверка подписи (обязательно!)
    //var stringToHash = $"{form["notification_type"]}&{form["operation_id"]}&{amount}&{form["currency"]}&{form["datetime"]}&{form["sender"]}&{form["codepro"]}&{notificationSecret}&{label}";
    
    // Формируем строку для проверки подписи (строго в этом порядке!)
    var stringToHash = string.Join("&", new[]
    {
        form["notification_type"].ToString(),
        form["operation_id"].ToString(),
        form["amount"].ToString(),
        form["currency"].ToString(),
        form["datetime"].ToString(),
        form["sender"].ToString(),
        form["codepro"].ToString(),
        notificationSecret,
        label
    });
    //var computedHash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(stringToHash)).Select(b => b.ToString("x2")).Aggregate((a, b) => a + b);
    var computedHash = BitConverter.ToString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(stringToHash))).Replace("-", "").ToLower();

    // Проверки безопасности
    //if (computedHash != sha1Hash || form["codepro"] == "true" || string.IsNullOrEmpty(label))
    //{
    //    logger.LogWarning("Invalid YooMoney notification");
    //    return Results.BadRequest();
    //}
    
    if (string.IsNullOrEmpty(label) || computedHash != sha1Hash || form["codepro"] == "true" || form["test_notification"] == "true")
    {
        logger.LogWarning("YooMoney: подозрительное или тестовое уведомление");
        return Results.BadRequest();
    }

    // Обработка успешного платежа
    if (!string.IsNullOrEmpty(label) && decimal.Parse(withdrawAmount) > 0)
    {
        var parts = label.Split('_');
        if (parts.Length >= 2 && parts[0] == "pack5")
        {
            var userId = long.Parse(parts[1]);
            var user = await db.Users.FindAsync(userId);
            if (user != null)
            {
                user.Credits += 5; // или другое количество
                await db.SaveChangesAsync();

                db.RequestLogs.Add(new RequestLog
                {
                    UserId = userId,
                    Timestamp = DateTime.UtcNow,
                    Action = "payment",
                    Success = true
                });
                await db.SaveChangesAsync();
            }
        }
    }

    return Results.Ok("notification accepted");
}).WithName("YooMoneyNotify");

// Простой health-check
app.MapGet("/", () => "MultiMessenger AI Bot is running! Nano banana ready.");

app.Run();
