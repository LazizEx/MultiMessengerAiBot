using MultiMessengerAiBot.Services;
using MultiMessengerAiBot.Workers;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

// Конфигурация (secrets.json + appsettings.json)
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>();

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

// Простой health-check
app.MapGet("/", () => "MultiMessenger AI Bot is running! Nano banana ready.");

app.Run();
