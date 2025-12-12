namespace MultiMessengerAiBot.Services
{
    public interface IBotService
    {
        Task<string?> GetImageUrlAsync(string prompt, string model = "pro", CancellationToken ct = default);
    }
}
