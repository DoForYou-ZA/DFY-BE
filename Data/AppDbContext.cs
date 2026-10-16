using Microsoft.EntityFrameworkCore;
using DoForYou.API.Models;

namespace DoForYou.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users { get; set; }
    public DbSet<Models.Task> Tasks { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Payment> Payments { get; set; }
    public DbSet<PaymentAttempt> PaymentAttempts { get; set; } = null!;
    public DbSet<Rating> Ratings { get; set; }
    public DbSet<TaskMessage> TaskMessages { get; set; }
    public DbSet<TaskProgressUpdate> TaskProgressUpdates { get; set; }
    public DbSet<Dispute> Disputes { get; set; }
    public DbSet<WalletTransaction> WalletTransactions { get; set; }
    public DbSet<BusinessRule> BusinessRules { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<BankAccount> BankAccounts { get; set; }
    public DbSet<WithdrawalRequest> WithdrawalRequests { get; set; }
    public DbSet<Payout> Payouts { get; set; } = null!;
    public DbSet<Refund> Refunds { get; set; } = null!;
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<SupportTicket> SupportTickets { get; set; }
    public DbSet<Testimonial> Testimonials { get; set; }

    public override int SaveChanges()
    {
        NormalizeUserEmails();
        NormalizeUtcDates();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        NormalizeUserEmails();
        NormalizeUtcDates();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void NormalizeUserEmails()
    {
        foreach (var entry in ChangeTracker.Entries<User>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            entry.Entity.Email = entry.Entity.Email.Trim();
            entry.Entity.NormalizedEmail = entry.Entity.Email.ToLowerInvariant();
        }
    }

    private void NormalizeUtcDates()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            foreach (var prop in entry.Properties)
            {
                if (prop.CurrentValue is DateTime dt && dt.Kind == DateTimeKind.Unspecified)
                    prop.CurrentValue = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<User>()
            .HasIndex(user => user.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("IX_Users_NormalizedEmail");
        modelBuilder.Entity<User>()
            .HasIndex(user => user.GoogleSubject)
            .IsUnique()
            .HasDatabaseName("IX_Users_GoogleSubject");

        modelBuilder.Entity<Models.Task>()
            .HasQueryFilter(t => !t.IsDeleted);
        modelBuilder.Entity<Models.Task>()
            .HasOne(t => t.CreatedByUser)
            .WithMany()
            .HasForeignKey(t => t.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Models.Task>()
            .HasOne(t => t.AcceptedByUser)
            .WithMany()
            .HasForeignKey(t => t.AcceptedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Testimonial>().Property(t => t.Name).HasMaxLength(100);
        modelBuilder.Entity<Testimonial>().Property(t => t.Role).HasMaxLength(80);
        modelBuilder.Entity<Testimonial>().Property(t => t.Text).HasMaxLength(1200);
        modelBuilder.Entity<Testimonial>().Property(t => t.IsPublished).HasDefaultValue(false);
        modelBuilder.Entity<Testimonial>().Property(t => t.SortOrder).HasDefaultValue(0);
        modelBuilder.Entity<Testimonial>().HasIndex(t => new { t.IsPublished, t.SortOrder });

        modelBuilder.Entity<PaymentAttempt>()
            .HasIndex(p => p.Reference)
            .IsUnique();

        modelBuilder.Entity<PaymentAttempt>()
            .HasIndex(p => p.TransactionId)
            .IsUnique()
            .HasFilter("\"TransactionId\" IS NOT NULL");

        modelBuilder.Entity<PaymentAttempt>()
            .HasOne(p => p.Task)
            .WithMany()
            .HasForeignKey(p => p.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasIndex(p => p.MerchantReference)
            .IsUnique();

        modelBuilder.Entity<Refund>()
            .HasIndex(r => r.TaskId)
            .IsUnique();

        modelBuilder.Entity<Refund>()
            .HasIndex(r => r.RefundId)
            .IsUnique();

        modelBuilder.Entity<Refund>()
            .HasOne(r => r.Task)
            .WithMany()
            .HasForeignKey(r => r.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasIndex(p => p.ProviderReference)
            .IsUnique()
            .HasFilter("\"ProviderReference\" IS NOT NULL");

        modelBuilder.Entity<Payout>()
            .HasOne(p => p.Task)
            .WithMany()
            .HasForeignKey(p => p.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasOne(p => p.Runner)
            .WithMany()
            .HasForeignKey(p => p.RunnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Payout>()
            .HasOne(p => p.BankAccount)
            .WithMany()
            .HasForeignKey(p => p.BankAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}