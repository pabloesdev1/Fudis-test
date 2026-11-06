using Fudis.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Api.Data;

public class FudisDbContext : DbContext
{
    public FudisDbContext(DbContextOptions<FudisDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<DriverEarning> DriverEarnings => Set<DriverEarning>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.DeliveryFee).HasPrecision(18, 2);
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.Property(o => o.RowVersion).IsRowVersion();
        });

        b.Entity<DriverEarning>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => x.OrderId).IsUnique();          // una sola ganancia por pedido
        });

        b.Entity<PaymentTransaction>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.TransactionId).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.TransactionId).IsUnique();    // idempotencia
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(100);
            e.HasIndex(x => x.ProcessedAt);
        });
    }
}