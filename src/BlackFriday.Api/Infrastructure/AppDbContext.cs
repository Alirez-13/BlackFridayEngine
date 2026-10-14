using BlackFriday.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BlackFriday.Api.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<SaleEvent> SaleEvents => Set<SaleEvent>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();
    public DbSet<DeadLetterJob> DeadLetterJobs => Set<DeadLetterJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SaleEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.SkuId });
            e.ToTable(t => t.HasCheckConstraint("CK_SaleEvent_RemainingStock_NonNegative", "RemainingStock >= 0"));
        });

        modelBuilder.Entity<Reservation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SaleEventId);
        });

        modelBuilder.Entity<OrderRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ReservationId).IsUnique();
        });

        modelBuilder.Entity<DeadLetterJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ReservationId);
        });
    }
}