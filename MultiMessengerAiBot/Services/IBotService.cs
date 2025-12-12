namespace MultiMessengerAiBot.Services
{
    public interface IBotService
    {
        Task<string?> GetImageUrlAsync(string prompt, string model = "pro", CancellationToken ct = default);

        // OpenRouterImageService.cs — временная заглушка
        public Task<string?> GenerateFromImageAsync(string imageUrl, string prompt, CancellationToken ct = default)
        {
            // Пока просто возвращаем оригинал (потом подключишь Flux img2img, Ideogram, Replicate и т.д.)
            return Task.FromResult<string?>(imageUrl);
        }
    }
}
