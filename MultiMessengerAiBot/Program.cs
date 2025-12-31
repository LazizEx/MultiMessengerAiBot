using Microsoft.EntityFrameworkCore;
using MultiMessengerAiBot.Data;
using MultiMessengerAiBot.Services;
using MultiMessengerAiBot.Workers;
using SQLitePCL;
using System.Text;
using System.Text.Json;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

// Инициализация SQLite (обязательно до UseSqlite!)
Batteries.Init();

// База данных
//var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "/SQLite/bot.db";  // fallback на локальный для dev
//builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath};"));

var environment = builder.Environment;

var dbPath = environment.IsDevelopment()
    ? builder.Configuration["Database:Path"]  // берёт из appsettings.Development.json
    : Environment.GetEnvironmentVariable("DB_PATH") ?? "/data/bot.db";  // на Render

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath};"));


// Конфигурация
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>();

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
    new TelegramBotClient(builder.Configuration["BotTokens:Telegram"]!));

//builder.Services.AddHostedService<DonatePaySocketService>();
//builder.Services.AddHostedService<DonatePayPollingService>();

// КЛЮЧЕВАЯ СТРОКА — запускает BackgroundService и ExecuteAsync()
builder.Services.AddHostedService<TelegramBotWorker>();
builder.Services.AddSingleton<TelegramBotWorker>(); // оставляем для ручного вызова MapTelegramWebhook

//builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();  // создаст БД и применит все миграции, если БД нет
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.MapFallbackToFile("ProgressBar.gif");
app.MapControllers();

// ВАЖНО: вызываем после сборки приложения
var telegramWorker = app.Services.GetRequiredService<TelegramBotWorker>();
telegramWorker.MapTelegramWebhook(app);

// YooMoney webhook
app.MapPost("/yoomoney/notify", async (HttpRequest request, AppDbContext db, IConfiguration cfg, ILogger<Program> logger) =>
{
    var form = await request.ReadFormAsync();
    var secret = cfg["YooMoney:NotificationSecret"] ?? "";

    var label = form["label"].ToString();

    // Проверка подписи (обязательно!)
    var stringToHash = string.Join("&", new[]
    {
        form["notification_type"].ToString(),
        form["operation_id"].ToString(),
        form["amount"].ToString(),
        form["currency"].ToString(),
        form["datetime"].ToString(),
        form["sender"].ToString(),
        form["codepro"].ToString(),
        secret,
        label
    });

    var computedHash = BitConverter.ToString(
        System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(stringToHash))
    ).Replace("-", "").ToLower();

    // Проверки безопасности
    if (string.IsNullOrEmpty(label) ||
        computedHash != form["sha1_hash"].ToString().ToLower() ||
        form["codepro"] == "true" ||
        form["test_notification"] == "true")
    {
        logger.LogWarning("YooMoney: неверная подпись или тестовое уведомление");
        return Results.BadRequest();
    }

    // Проверяем, что оплата реальная
    if (decimal.TryParse(form["withdraw_amount"], out var withdraw) && withdraw > 0)
    {
        if (label.StartsWith("pack_"))
        {
            var parts = label.Split('_');
            if (parts.Length == 3 && int.TryParse(parts[1], out var credits) && long.TryParse(parts[2], out var userId))
            {
                var user = await db.Users.FindAsync(userId);
                if (user != null)
                {
                    user.Credits += credits;
                    await db.SaveChangesAsync();

                    db.RequestLogs.Add(new RequestLog
                    {
                        UserId = userId,
                        Timestamp = DateTime.UtcNow,
                        Action = "payment_yoomoney",
                        Success = true
                    });
                    await db.SaveChangesAsync();

                    logger.LogInformation("YooMoney: пользователю {UserId} добавлено {Credits} генераций", userId, credits);
                }
            }
        }
    }

    return Results.Ok("ok");
}).WithName("YooMoneyNotify");

// donatepay.ru webhook


app.MapGet("/", () => "MultiMessenger AI Bot is running! Nano banana ready.");

var diskPath = "/SQLite/output.json";
var seedPath = "output.json"; // путь в проекте (файл должен быть в git)

if (!File.Exists(diskPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
    File.Copy(seedPath, diskPath);
    Console.WriteLine("output.json скопирован на persistent disk");
}

app.Run();