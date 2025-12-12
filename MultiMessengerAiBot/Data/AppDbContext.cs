using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MultiMessengerAiBot.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<RequestLog> RequestLogs { get; set; } // для аналитики

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().HasKey(u => u.TelegramId);
        }
    }

    public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            optionsBuilder.UseSqlite("Data Source=bot.db"); // Тот же connection string

            return new AppDbContext(optionsBuilder.Options);
        }
    }

    public class User
    {
        public long TelegramId { get; set; } // primary key
        public int Credits { get; set; } = 0; // кредиты на генерации
        public string? ReferralCode { get; set; } // уникальный код
        public long? ReferredBy { get; set; } // кто пригласил
        public string? PhoneNumber { get; set; } // для локализации
        public string Currency { get; set; } = "RUB"; // по умолчанию RUB
    }

    public class RequestLog // для аналитики и анти-абьюз
    {
        public int Id { get; set; }
        public long UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Action { get; set; } = string.Empty; // "generate", "payment", "abuse"
        public bool Success { get; set; }
    }
}
