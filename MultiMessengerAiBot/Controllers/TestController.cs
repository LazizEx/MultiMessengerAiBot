using Microsoft.AspNetCore.Mvc;
using MultiMessengerAiBot.Services;

namespace MultiMessengerAiBot.Controllers
{
    [ApiController]
    [Route("test")]
    public class TestController : ControllerBase
    {
        private readonly IBotService _gen;

        public TestController(IBotService gen)   // инжектируем наш сервис
        {
            _gen = gen;
        }

        // GET https://localhost:7123/test/draw?text=кот в космосе
        [HttpGet("draw")]
        public async Task<IActionResult> Draw([FromQuery] string text)
        {
            string url = await _gen.GetImageUrlAsync(text);
            return Ok(new { imageUrl = url });   // просто возвращаем JSON с ссылкой
        }
    }
}
