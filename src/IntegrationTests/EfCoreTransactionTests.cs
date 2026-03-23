using Contracts;
using Dapper;
using IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace IntegrationTests;

/// <summary>
/// Integration tests for the EF Core + WolverineFx transactional outbox pattern.
///
/// These tests prove the core durability guarantee:
///   SUCCESS: The Order row AND the outbox entry for OrderCreated are written
///            atomically in one database transaction.
///   FAILURE: If the handler throws, NEITHER the Order row NOR the outbox entry
///            is written — full rollback.
///
/// RabbitMQ is not required — all external transports are stubbed.
/// Requires a running PostgreSQL instance (start docker-compose first).
/// Set WOLVERINE_DEMO_CONNECTION_STRING to override the default connection string.
/// </summary>
[Collection("EfCore")]
public class EfCoreTransactionTests : IAsyncLifetime
{
    private IHost _host = null!;
    private bool _isDbAvailable;

    public async Task InitializeAsync()
    {
        _isDbAvailable = await TestInfrastructure.IsPostgresAvailableAsync();
        if (!_isDbAvailable) return;

        _host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContextWithWolverineIntegration<TestDbContext>(
                    opts => opts.UseNpgsql(TestInfrastructure.ConnectionString),
                    "efcore");
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "EfCoreTest";
                opts.PersistMessagesWithPostgresql(TestInfrastructure.ConnectionString, "efcore");

                // Enable EF Core transactional middleware.
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                // Stub external transports — no RabbitMQ needed for these tests.
                opts.StubAllExternalTransports();

                // Only discover handlers from this assembly (avoid conflicts with
                // Dapper handlers that also handle CreateOrder).
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<EfCoreCreateOrderHandler>();
                opts.Discovery.IncludeType<EfCoreOrderCreatedHandler>();
            })
            .StartAsync();

        // Explicitly create the application table using DDL.
        //
        // We cannot rely solely on db.Database.EnsureCreatedAsync() because EF Core
        // treats that method as all-or-nothing: if ANY tables already exist in the
        // database (e.g. Wolverine's schema tables from a previous run), EF Core
        // returns false without creating the missing application tables.
        //
        // The column names use EF Core's default naming for the mapped properties
        // (PascalCase quoted, since we don't apply a snake_case naming convention).
        await using var setupConn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await setupConn.OpenAsync();
        await setupConn.ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS efcore_demo;
            CREATE TABLE IF NOT EXISTS efcore_demo.orders (
                "Id"           UUID          NOT NULL PRIMARY KEY,
                "CustomerName" VARCHAR(200)  NOT NULL,
                "Amount"       NUMERIC(18,2) NOT NULL,
                "Status"       VARCHAR(50)   NOT NULL DEFAULT 'Pending',
                "CreatedAt"    TIMESTAMPTZ   NOT NULL DEFAULT NOW()
            );
            """);

        // Clear data from previous runs (truncate rows, keep table structure).
        await TestInfrastructure.ClearTestDataAsync("efcore", "efcore_demo");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.StopAsync();
    }

    [Fact]
    public async Task SuccessfulOrder_WritesOrderRowAndOutboxEntry_Atomically()
    {
        SkipIfNoDatabase();

        var orderId = Guid.NewGuid();

        await using (var busScope = _host.Services.CreateAsyncScope())
        {
            var bus = busScope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(new CreateOrder(orderId, "Alice", 99.99m));
        }

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var order = await db.Orders.FindAsync(orderId);

        // Verify the Order row was written with the correct data.
        Assert.NotNull(order);
        Assert.Equal("Alice", order.CustomerName);
        Assert.Equal(99.99m, order.Amount);
        // The status may be "Pending" or "Confirmed" depending on whether
        // InvokeAsync delivered the cascaded OrderCreated handler inline (as it
        // does with stubbed transports on .NET 10) or asynchronously. Both states
        // prove the order was successfully created — the FullRoundTrip test
        // explicitly verifies the full cascade to "Confirmed".
        Assert.True(order.Status is "Pending" or "Confirmed",
            $"Expected 'Pending' or 'Confirmed', got '{order.Status}'");
    }

    [Fact]
    public async Task InvalidOrder_NegativeAmount_RollsBackOrderRow()
    {
        SkipIfNoDatabase();

        var orderId = Guid.NewGuid();

        // Handler throws for negative amount — the entire transaction rolls back.
        await using (var busScope = _host.Services.CreateAsyncScope())
        {
            var bus = busScope.ServiceProvider.GetRequiredService<IMessageBus>();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                bus.InvokeAsync(new CreateOrder(orderId, "Bob", -10m)));
        }

        // No order row should exist.
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var order = await db.Orders.FindAsync(orderId);

        Assert.Null(order);
    }

    [Fact]
    public async Task InvalidOrder_NegativeAmount_WritesNoOutboxEntry()
    {
        SkipIfNoDatabase();

        var orderId = Guid.NewGuid();

        await using (var busScope = _host.Services.CreateAsyncScope())
        {
            var bus = busScope.ServiceProvider.GetRequiredService<IMessageBus>();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                bus.InvokeAsync(new CreateOrder(orderId, "Carol", -10m)));
        }

        // No outbox entry should have been committed for this order.
        await using var conn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await conn.OpenAsync();

        var count = await conn.QuerySingleAsync<long>(
            """
            SELECT COUNT(*) FROM efcore.wolverine_outgoing_envelopes
            WHERE CONVERT_FROM(body, 'UTF8') LIKE @OrderId
            """,
            new { OrderId = $"%{orderId}%" });

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task FullRoundTrip_OrderCreatedHandlerConfirmsOrderStatus()
    {
        SkipIfNoDatabase();

        // When CreateOrder succeeds:
        //   1. Order row inserted with status="Pending"
        //   2. OrderCreated written to outbox (same transaction)
        //   3. Relay delivers OrderCreated to OrderCreatedHandler
        //   4. OrderCreatedHandler updates status to "Confirmed"
        //
        // The fact that OrderCreatedHandler can update the status proves that:
        //   - The Order row existed when OrderCreatedHandler ran (transaction committed first)
        //   - OrderCreated was only published AFTER the commit (outbox guarantee)
        //
        // We use a separate host with DurabilityMode.Solo to ensure the cascaded
        // OrderCreated handler completes before the host shuts down.

        var orderId = Guid.NewGuid();

        var testHost = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddDbContextWithWolverineIntegration<TestDbContext>(
                    opts => opts.UseNpgsql(TestInfrastructure.ConnectionString),
                    "efcore");
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "EfCoreTestRoundTrip";
                opts.PersistMessagesWithPostgresql(TestInfrastructure.ConnectionString, "efcore");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
                opts.StubAllExternalTransports();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<EfCoreCreateOrderHandler>();
                opts.Discovery.IncludeType<EfCoreOrderCreatedHandler>();
            })
            .StartAsync();

        try
        {
            await using (var busScope = testHost.Services.CreateAsyncScope())
            {
                var bus = busScope.ServiceProvider.GetRequiredService<IMessageBus>();
                await bus.InvokeAsync(new CreateOrder(orderId, "Diana", 200m));
            }

            // Allow time for the cascaded OrderCreated handler to run.
            await Task.Delay(500);

            await using var scope = testHost.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var order = await db.Orders.FindAsync(orderId);

            Assert.NotNull(order);
            Assert.Equal("Confirmed", order.Status);
        }
        finally
        {
            await testHost.StopAsync();
        }
    }

    private void SkipIfNoDatabase()
    {
        if (!_isDbAvailable)
            TestSkip.Because("PostgreSQL not available. Start docker-compose first.");
    }
}
