using System.Text;
using System.Text.Json;

namespace MultiMessengerAiBot.Services
{
    public class OpenRouterImageService : IBotService
    {
        private readonly HttpClient _http;
        private readonly ILogger<OpenRouterImageService> _logger;

        // ← ИСПРАВЛЕНО! Это актуальное имя
        //private const string DefaultModel = "black-forest-labs/flux.2-pro"; // ← РАБОТАЕТ

        // Альтернативы (раскомментируй нужную):
        //private const string DefaultModel = "black-forest-labs/flux.2-flex";
        //private const string DefaultModel = "recursal/eagle-7b";
        //private const string DefaultModel = "recursal/rwkv-5-3b-ai-town";
        //private const string DefaultModel = "google/gemini-3-pro-image-preview";

        private static readonly Dictionary<string, string> Models = new()
        {
            ["pro"] = "black-forest-labs/flux.2-pro",
            ["flex"] = "black-forest-labs/flux.2-flex"
        };



        public OpenRouterImageService(IHttpClientFactory factory, ILogger<OpenRouterImageService> logger)
        {
            _http = factory.CreateClient("OpenRouter");
            _logger = logger;
        }

        public async Task<string?> GetImageUrlAsync(string prompt, string model = "pro", CancellationToken ct = default)
        {
            var modelId = Models.GetValueOrDefault(model, Models["pro"]);

            var request = new
            {
                model = modelId,
                messages = new[]
                {
                new { role = "user", content = prompt } // ← просто промпт, без лишнего текста
                },
                // Эти параметры критичны для получения base64-изображения:
                modalities = new[] { "image", "text" },  // ← ВОЛШЕБСТВО: включает image output
                max_tokens = 1,
                temperature = 0.8,
                response_format = "url", // или "b64_json" — если хочешь base64
               
                // ←←← НОВАЯ ЧАСТЬ: размер изображения
                // Вариант 1: высокое качество (около 1.5–2 MP)
                //width = 1024,
                //height = 1792,   // 1024×1792 ≈ 9:16 — идеально для современных смартфонов

                // Вариант 2: чуть меньше (быстрее и дешевле)
                width = 768,
                height = 1360,   // 768×1360

                // Вариант 3: если хочешь горизонтальный — поменяй местами
                // width = 1792,
                // height = 1024,

            };

            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

            try
            {
                //var response = await _http.PostAsync("/api/v1/chat/completions", content, ct);

                //if (!response.IsSuccessStatusCode)
                //{
                //    var error = await response.Content.ReadAsStringAsync(ct);
                //    _logger.LogWarning("OpenRouter error {Status}: {Error}", response.StatusCode, error);
                //    return null;
                //}

                //using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                using var doc = ReadFile();

                var root = doc.RootElement;

                // Вариант 1: новое API (2025) — images[] → image_url.url
                if (root.TryGetProperty("choices", out var choicesEl) &&
                    choicesEl.GetArrayLength() > 0)
                {
                    var message = choicesEl[0].GetProperty("message");

                    // Новый формат: images[]
                    if (message.TryGetProperty("images", out var imagesEl) && imagesEl.GetArrayLength() > 0)
                    {
                        var imageUrl = imagesEl[0]
                            .GetProperty("image_url")
                            .GetProperty("url")
                            .GetString();

                        // Если base64 — возвращаем data URI
                        if (imageUrl?.StartsWith("data:image") == true)
                            return imageUrl;

                        // Если внешний URL — возвращаем как есть (Telegram обработает)
                        if (!string.IsNullOrEmpty(imageUrl))
                            return imageUrl;

                        //if (!string.IsNullOrEmpty(imageUrl) && imageUrl.StartsWith("data:image"))
                        //    return imageUrl;
                    }

                    // Старый формат (ещё встречается на некоторых моделях): content = data:image/...
                    if (message.TryGetProperty("content", out var contentEl))
                    {
                        var contentStr = contentEl.GetString();
                        if (!string.IsNullOrEmpty(contentStr) && contentStr.StartsWith("data:image"))
                            return contentStr;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenRouter exception");
                return null;
            }
        }

        public async Task<string?> GenerateFromImageAsync(string imageUrl, string prompt, CancellationToken ct = default)
        {
        //    var request = new
        //    {
        //        model = "black-forest-labs/flux.2-pro",           // поддерживает img2img
        //                                                          // model = "ideogram/ideogram-2.0"                // альтернатива, тоже отлично работает
        //        messages = new[]
        //        {
        //    new
        //    {
        //        role = "user",
        //        content = new object[]
        //        {
        //            new { type = "text", text = prompt },
        //            new { type = "image_url", image_url = new { url = imageUrl } }
        //        }
        //    }
        //},
        //        max_tokens = 1,
        //        modalities = new[] { "image", "text" }
        //    };

        //    var content = new StringContent(
        //        JsonSerializer.Serialize(request),
        //        Encoding.UTF8,
        //        "application/json");

        //    var response = await _http.PostAsync("/api/v1/chat/completions", content, ct);

        //    if (!response.IsSuccessStatusCode)
        //        return null;
            
        //    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct));
            using var doc = ReadFile();

            var root = doc.RootElement;

            // Новый формат 2025 года
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");

                if (message.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                {
                    var url = images[0].GetProperty("image_url").GetProperty("url").GetString();
                    if (!string.IsNullOrEmpty(url))
                        return url;

                    if (images[0].TryGetProperty("b64_json", out var b64El))
                    {
                        var b64 = b64El.GetString();
                        if (!string.IsNullOrEmpty(b64))
                            return $"data:image/png;base64,{b64}";
                    }
                    return null;
                }

                // Старый fallback
                var contentStr = message.GetProperty("content").GetString();
                if (contentStr?.StartsWith("data:image") == true)
                    return contentStr;
            }

            return null;
        }

        private static JsonDocument? ReadFile()
        {
            // На Render.com используем путь на примонтированном диске
            // Если переменная окружения не задана — fallback на локальный путь (удобно для разработки)
            string filePath = Environment.GetEnvironmentVariable("OUTPUT_JSON_PATH") ?? "/SQLite/output.json";
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"Ошибка: Файл не найден по пути '{filePath}'");
                    return null;
                }

                string jsonString = File.ReadAllText(filePath);
                return JsonDocument.Parse(jsonString);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Ошибка парсинга JSON: {ex.Message}");
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Ошибка доступа к файлу: {ex.Message}");
                return null;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Ошибка ввода-вывода при чтении файла: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Неожиданная ошибка: {ex.Message}");
                return null;
            }
        }
    }
}
