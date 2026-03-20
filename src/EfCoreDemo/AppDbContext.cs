using EfCoreDemo.Domain;
using Microsoft.EntityFrameworkCore;

namespace EfCoreDemo;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("orders", "efcore_demo");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.CustomerName).HasMaxLength(200).IsRequired();
            entity.Property(o => o.Amount).HasPrecision(18, 2);
            entity.Property(o => o.Status).HasMaxLength(50).IsRequired();
        });

        // NOTE: MapWolverineEnvelopeStorage() is NOT called here explicitly.
        // When AddDbContextWithWolverineIntegration<AppDbContext>() is used in
        // Program.cs, Wolverine automatically registers a WolverineModelCustomizer
        // that calls MapWolverineEnvelopeStorage() during model building. Calling
        // it again here would cause a duplicate 'WolverineEnabled' annotation error.
        //
        // The envelope tables (wolverine_incoming_envelopes, wolverine_outgoing_envelopes,
        // etc.) are added to this DbContext's model automatically, enabling EF Core
        // command batching: outbox writes happen in the SAME round-trip as SaveChangesAsync().
    }
}
