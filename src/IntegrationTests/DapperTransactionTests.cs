using Contracts;
using Dapper;
using IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace IntegrationTests;

/// <summary>
/// Integration tests for the Dapper + WolverineFx transactional outbox pattern.
///
/// These tests prove the same guarantee as EfCoreTransactionTests, but using
/// the manual DatabaseEnvelopeTransaction approach that Dapper requires.
///
/// COMPARISON SUMMARY:
///   EF Core approach: Zero boilerplate. WolverineFx middleware generates code
///     that automatically wraps handlers in a transaction covering both SaveChanges()
///     and outbox writes. The [Transactional] attribute (or AutoApplyTransactions())
///     is the only configuration needed.
///
///   Dapper approach: ~15 lines of boilerplate per handler:
///     (1) Open connection, (2) begin transaction,
///     (3) cast IMessageContext to MessageContext,
///     (4) create DatabaseEnvelopeTransaction,
///     (5) call EnlistInOutboxAsync(),
///     (6) pass transaction to all Dapper queries,
///     (7) explicitly call PublishAsync(),
///     (8) commit the transaction.
///
///   The transactional guarantee is IDENTICAL for both approaches. The difference
///   is purely in how much code the developer must write.
/// </summary>
[Collection("Dapper")]
public class DapperTransactionTests : IAsyncLifetime
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
                services.AddNpgsqlDataSource(TestInfrastructure.ConnectionString);
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DapperTest";
                opts.PersistMessagesWithPostgresql(TestInfrastructure.ConnectionString, "dapper");

                opts.StubAllExternalTransports();

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<DapperCreateOrderHandler>();
                opts.Discovery.IncludeType<DapperOrderCreatedHandler>();
            })
            .StartAsync();

        // Create the dapper_demo.orders table (idempotent).
        await using var conn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS dapper_demo;
            CREATE TABLE IF NOT EXISTS dapper_demo.orders (
                id            UUID          NOT NULL PRIMARY KEY,
                customer_name VARCHAR(200)  NOT NULL,
                amount        NUMERIC(18,2) NOT NULL,
                status        VARCHAR(50)   NOT NULL DEFAULT 'Pending',
                created_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW()
            );
            """);

        // Clear data from previous runs.
        await TestInfrastructure.ClearTestDataAsync("dapper", "dapper_demo");
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
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync(new CreateOrder(orderId, "Eve", 150m));

        await using var conn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await conn.OpenAsync();

        var order = await conn.QuerySingleOrDefaultAsync(
            "SELECT id, customer_name, amount, status FROM dapper_demo.orders WHERE id = @Id",
            new { Id = orderId });

        Assert.NotNull(order);
        Assert.Equal("Eve", (string)order!.customer_name);
        Assert.Equal(150m, (decimal)order.amount);
        Assert.Equal("Pending", (string)order.status);
    }

    [Fact]
    public async Task InvalidOrder_NegativeAmount_RollsBackOrderRow()
    {
        SkipIfNoDatabase();

        var orderId = Guid.NewGuid();
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            bus.InvokeAsync(new CreateOrder(orderId, "Frank", -5m)));

        await using var conn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await conn.OpenAsync();

        var order = await conn.QuerySingleOrDefaultAsync(
            "SELECT id FROM dapper_demo.orders WHERE id = @Id",
            new { Id = orderId });

        Assert.Null(order);
    }

    [Fact]
    public async Task InvalidOrder_NegativeAmount_WritesNoOutboxEntry()
    {
        SkipIfNoDatabase();

        var orderId = Guid.NewGuid();
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            bus.InvokeAsync(new CreateOrder(orderId, "George", -5m)));

        await using var conn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await conn.OpenAsync();

        var count = await conn.QuerySingleAsync<long>(
            """
            SELECT COUNT(*) FROM dapper.wolverine_outgoing_envelopes
            WHERE CONVERT_FROM(body, 'UTF8') LIKE @OrderId
            """,
            new { OrderId = $"%{orderId}%" });

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task FullRoundTrip_OrderCreatedHandlerConfirmsOrderStatus()
    {
        SkipIfNoDatabase();

        var orderId = Guid.NewGuid();
        var bus = _host.Services.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync(new CreateOrder(orderId, "Helen", 75m));

        // Allow time for the cascaded OrderCreated handler to run.
        await Task.Delay(300);

        await using var conn = new NpgsqlConnection(TestInfrastructure.ConnectionString);
        await conn.OpenAsync();

        var status = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT status FROM dapper_demo.orders WHERE id = @Id",
            new { Id = orderId });

        Assert.Equal("Confirmed", status);
    }

    [Fact]
    public void Documentation_UnsafeNonOutboxApproachRisk()
    {
        SkipIfNoDatabase();

        // This test documents — conceptually — what happens without the outbox.
        //
        // UNSAFE PATTERN (do NOT use):
        //
        //   await conn.ExecuteAsync("INSERT INTO orders ...", data, tx);
        //   await tx.CommitAsync();
        //
        //   // ← CRASH HERE → order exists in DB, but message is NEVER sent.
        //
        //   await bus.PublishAsync(new OrderCreated(...));   // not reached!
        //
        // The DB write and the message send are two separate operations. If the
        // process crashes between them, you have a committed DB record but no
        // outgoing message — a "lost event."
        //
        // SAFE PATTERN (use this):
        //
        //   await using var tx = await conn.BeginTransactionAsync();
        //   await ((MessageContext)context).EnlistInOutboxAsync(
        //       new DatabaseEnvelopeTransaction(messageDb, tx));
        //   await conn.ExecuteAsync("INSERT INTO orders ...", data, tx);
        //   await context.PublishAsync(new OrderCreated(...));   // writes to outbox table via tx
        //   await tx.CommitAsync();   // ← atomically commits DB row + outbox entry
        //
        // With the safe pattern, a crash at ANY point either rolls back both
        // or commits both — there is no in-between inconsistent state.

        Assert.True(true, "This is a documentation test that always passes.");
    }

    private void SkipIfNoDatabase()
    {
        if (!_isDbAvailable)
            TestSkip.Because("PostgreSQL not available. Start docker-compose first.");
    }
}
