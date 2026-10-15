using Kubico.Domain.Calendar;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Domain.Rewards;
using Kubico.Domain.Shopping;
using Kubico.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Kubico.Infrastructure.Persistence;

public class KubicoDbContext(DbContextOptions<KubicoDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Household> Households => Set<Household>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();
    public DbSet<Chore> Chores => Set<Chore>();
    public DbSet<ChoreCompletion> ChoreCompletions => Set<ChoreCompletion>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<Reward> Rewards => Set<Reward>();
    public DbSet<RewardRedemption> RewardRedemptions => Set<RewardRedemption>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite (usado nos testes) não ordena nem compara DateTimeOffset; guardamos como número.
        if (Database.IsSqlite())
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(256);
            e.Property(u => u.DisplayName).HasMaxLength(100);
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.Device).HasMaxLength(200);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.FamilyId);
            e.HasIndex(t => t.UserId);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Household>(e =>
        {
            e.Property(h => h.Name).HasMaxLength(100);
            e.Property(h => h.TimeZoneId).HasMaxLength(64).HasDefaultValue(Household.DefaultTimeZoneId);
        });

        modelBuilder.Entity<Membership>(e =>
        {
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Color).HasMaxLength(7);
            e.Property(m => m.CalendarFeedToken).HasMaxLength(64);
            e.HasIndex(m => new { m.HouseholdId, m.UserId }).IsUnique();
            e.HasIndex(m => m.CalendarFeedToken).IsUnique();
            e.HasOne(m => m.Household).WithMany(h => h.Members).HasForeignKey(m => m.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany(u => u.Memberships).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Invite>(e =>
        {
            e.Property(i => i.Code).HasMaxLength(16);
            e.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(i => i.Code).IsUnique();
            e.HasOne(i => i.Household).WithMany(h => h.Invites).HasForeignKey(i => i.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShoppingList>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(100);
            e.HasIndex(l => l.HouseholdId);
            e.HasOne(l => l.Household).WithMany().HasForeignKey(l => l.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShoppingItem>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(200);
            e.Property(i => i.Quantity).HasMaxLength(50);
            e.Property(i => i.Category).HasMaxLength(50);
            e.HasIndex(i => i.ListId);
            e.HasOne(i => i.List).WithMany(l => l.Items).HasForeignKey(i => i.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chore>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Notes).HasMaxLength(1000);
            e.Property(c => c.Recurrence).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(c => new { c.HouseholdId, c.DueDate });
            e.HasOne(c => c.Household).WithMany().HasForeignKey(c => c.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChoreCompletion>(e =>
        {
            e.HasIndex(c => new { c.ChoreId, c.CompletedAt });
            e.HasIndex(c => c.UserId);
            e.HasOne(c => c.Chore).WithMany(c => c.Completions).HasForeignKey(c => c.ChoreId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarEvent>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(200);
            e.Property(c => c.Notes).HasMaxLength(1000);
            e.Property(c => c.Location).HasMaxLength(200);
            e.Property(c => c.Recurrence).HasMaxLength(200);
            e.HasIndex(c => new { c.HouseholdId, c.StartsAt });
            e.HasIndex(c => new { c.HouseholdId, c.StartDate });
            e.HasIndex(c => new { c.SeriesId, c.OriginalDate });
            e.HasOne(c => c.Household).WithMany().HasForeignKey(c => c.HouseholdId).OnDelete(DeleteBehavior.Cascade);

            // Apagar uma série apaga as suas exceções.
            e.HasOne<CalendarEvent>().WithMany().HasForeignKey(c => c.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Reward>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(100);
            e.Property(r => r.Description).HasMaxLength(500);
            e.HasIndex(r => r.HouseholdId);
            e.HasOne(r => r.Household).WithMany().HasForeignKey(r => r.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RewardRedemption>(e =>
        {
            e.Property(r => r.RewardTitle).HasMaxLength(100);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(r => new { r.HouseholdId, r.RequestedAt });
            e.HasIndex(r => new { r.HouseholdId, r.UserId });
            e.HasOne<Household>().WithMany().HasForeignKey(r => r.HouseholdId).OnDelete(DeleteBehavior.Cascade);

            // O histórico fica, mesmo que a recompensa seja apagada.
            e.HasOne<Reward>().WithMany().HasForeignKey(r => r.RewardId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.Property(n => n.Title).HasMaxLength(200);
            e.Property(n => n.Body).HasMaxLength(500);
            e.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(n => new { n.UserId, n.CreatedAt });
            e.HasIndex(n => n.PushedAt);
            e.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Household>().WithMany().HasForeignKey(n => n.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationPreferences>(e =>
        {
            e.HasKey(p => p.UserId);
            e.Property(p => p.Delivery).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.TimeZoneId).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PushDevice>(e =>
        {
            e.Property(d => d.Token).HasMaxLength(512);
            e.Property(d => d.Platform).HasMaxLength(20);
            e.HasIndex(d => d.Token).IsUnique();
            e.HasIndex(d => d.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
