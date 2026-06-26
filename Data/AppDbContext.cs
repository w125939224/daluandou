using daluandou.Models;
using daluandou.Pages;
using Microsoft.EntityFrameworkCore;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace daluandou.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<ChatRoom> ChatRooms { get; set; }
        public DbSet<GameCard> GameCards { get; set; }
        public DbSet<GameCells> GameCells { get; set; }
        public DbSet<GameRooms> GameRooms { get; set; }
        public DbSet<GamePlayer> GamePlayers { get; set; }
        public DbSet<GameLog> GameLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasCharSet(CharSet.Utf8Mb4, DelegationModes.ApplyToColumns);

            modelBuilder.Entity<User>().ToTable("Users");

            modelBuilder.Entity<ChatMessage>(entity =>
            {
                entity.ToTable("ChatMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).UseIdentityColumn();
            });

            modelBuilder.Entity<ChatRoom>(entity =>
            {
                entity.ToTable("ChatRooms");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).UseIdentityColumn();
            });
        }
        public override int SaveChanges()
        {
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}