using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MultiMessengerAiBot.Data;
using System.Text;

namespace MultiMessengerAiBot.Controllers;

public class AdminController : Controller
{
    private readonly AppDbContext _db;
    private const string AdminUsername = "admin";                 // ← поменяй на свой
    private const string AdminPassword = "admins1";   // ← поменяй на надёжный!

    public AdminController(AppDbContext db)
    {
        _db = db;
    }

    // Защита: проверяем Basic Auth
    private bool CheckAuth()
    {
        var authHeader = Request.Headers["Authorization"].FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Basic "))
        {
            var encoded = authHeader["Basic ".Length..];
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parts = decoded.Split(':');
            if (parts.Length == 2 && parts[0] == AdminUsername && parts[1] == AdminPassword)
                return true;
        }
        return false;
    }

    [HttpGet("/admin")]
    public async Task<IActionResult> Index()
    {
        if (!CheckAuth())
        {
            Response.Headers["WWW-Authenticate"] = "Basic realm=\"Admin Panel\"";
            return Unauthorized();
        }

        // Шаг 1: Загружаем всех пользователей (простой запрос)
        var users = await _db.Users
            .Select(u => new
            {
                u.TelegramId,
                FullName = (u.FirstName ?? "") + " " + (u.LastName ?? ""),
                u.Username,
                u.PhoneNumber,
                u.Credits
            })
            .ToListAsync();

        // Шаг 2: Считаем количество генераций для всех пользователей одним запросом
        var generationCounts = await _db.RequestLogs
            .Where(l => l.Action == "generate" && l.Success)
            .GroupBy(l => l.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Count = g.Count()
            })
            .ToDictionaryAsync(g => g.UserId, g => g.Count);

        // Шаг 3: Объединяем данные в памяти
        var result = users.Select(u => new
        {
            u.TelegramId,
            FullName = string.IsNullOrWhiteSpace(u.FullName.Trim()) ? null : u.FullName.Trim(),
            Username = string.IsNullOrEmpty(u.Username) ? null : u.Username,
            PhoneNumber = string.IsNullOrEmpty(u.PhoneNumber) ? null : u.PhoneNumber,
            u.Credits,
            GenerationsUsed = generationCounts.TryGetValue(u.TelegramId, out var count) ? count : 0
        })
        .OrderByDescending(u => u.Credits)
        .ThenByDescending(u => u.GenerationsUsed)
        .ToList();

        // Статистика
        ViewBag.TotalUsers = result.Count;
        ViewBag.TotalCredits = result.Sum(u => u.Credits);
        ViewBag.TotalPayments = await _db.RequestLogs.CountAsync(l => l.Action.Contains("payment") && l.Success);
        ViewBag.TotalGenerations = result.Sum(u => u.GenerationsUsed);

        return View(result);
    }

    //[HttpGet("/admin")]
    //public IActionResult Index()
    //{
    //    if (!CheckAuth())
    //    {
    //        Response.Headers["WWW-Authenticate"] = "Basic realm=\"Admin Panel\"";
    //        return Unauthorized();
    //    }

    //    ViewBag.TotalUsers = _db.Users.Count();
    //    ViewBag.TotalCredits = _db.Users.Sum(u => u.Credits);
    //    ViewBag.TotalPayments = _db.RequestLogs.Count(l => l.Action.Contains("payment") && l.Success);
    //    ViewBag.TotalGenerations = _db.RequestLogs.Count(l => l.Action == "generate" && l.Success);

    //    var recentLogs = _db.RequestLogs
    //        .OrderByDescending(l => l.Timestamp)
    //        .Take(50)
    //        .ToList();

    //    return View(recentLogs);
    //}
}