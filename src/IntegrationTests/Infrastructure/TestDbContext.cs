using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace IntegrationTests.Infrastructure;

/// <summary>
/// EF Core DbContext for integration tests. Mirrors the AppDbContext in EfCoreDemo
/// but lives in the test project to avoid a project reference to a Web SDK project.
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestOrder> Orders => Set<TestOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestOrder>(entity =>
        {
            entity.ToTable("orders", "efcore_demo");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.CustomerName).HasMaxLength(200).IsRequired();
            entity.Property(o => o.Amount).HasPrecision(18, 2);
            entity.Property(o => o.Status).HasMaxLength(50).IsRequired();
        });

        // NOTE: We do NOT call modelBuilder.MapWolverineEnvelopeStorage() here.
        // When AddDbContextWithWolverineIntegration<T>() is used (see tests),
        // Wolverine automatically customizes the model via WolverineModelCustomizer
        // and calls MapWolverineEnvelopeStorage() itself. Calling it again here
        // would cause a duplicate 'WolverineEnabled' annotation exception.
        //
        // In AppDbContext (EfCoreDemo), the call IS present because it registers
        // the DbContext with AddDbContext() (not AddDbContextWithWolverineIntegration()).
        // The WolverineEntityCoreExtensions.MapWolverineEnvelopeStorage extension
        // method shows up only once per model registration path.
    }
}

public class TestOrder
{
    public Guid Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }
}
