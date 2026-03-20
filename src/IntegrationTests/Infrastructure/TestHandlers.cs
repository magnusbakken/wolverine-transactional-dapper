using Contracts;
using Dapper;
using Npgsql;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace IntegrationTests.Infrastructure;

// ============================================================================
// EF CORE HANDLERS (mirror EfCoreDemo/Handlers/)
// ============================================================================

/// <summary>
/// EF Core handler used in integration tests. Identical pattern to the production
/// handler in EfCoreDemo — fully automatic transaction via WolverineFx middleware.
/// The middleware (enabled via AutoApplyTransactions) wraps this method in a
/// transaction that atomically covers SaveChangesAsync() + outbox write.
/// </summary>
public class EfCoreCreateOrderHandler
{
    // No async needed — WolverineFx middleware handles I/O around this method.
    public static OrderCreated Handle(CreateOrder command, TestDbContext db)
    {
        if (command.Amount < 0)
            throw new ArgumentException($"Amount must be non-negative, got {command.Amount}");

        db.Orders.Add(new TestOrder
        {
            Id = command.OrderId,
            CustomerName = command.CustomerName,
            Amount = command.Amount,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        });

        // Returning OrderCreated cascades it through the outbox atomically.
        // No explicit SaveChangesAsync() or transaction management.
        return new OrderCreated(command.OrderId, command.CustomerName, command.Amount);
    }
}

/// <summary>Updates order status to "Confirmed" to prove the round-trip works.</summary>
public class EfCoreOrderCreatedHandler
{
    public static async Task Handle(OrderCreated @event, TestDbContext db)
    {
        var order = await db.Orders.FindAsync(@event.OrderId);
        if (order is not null)
            order.Status = "Confirmed";
    }
}

// ============================================================================
// DAPPER HANDLERS (mirror DapperDemo/Handlers/)
// ============================================================================

/// <summary>
/// Dapper handler used in integration tests. Identical pattern to the production
/// handler in DapperDemo — explicit transaction, explicit outbox enrollment.
/// Every step that EF Core middleware does automatically must be done manually.
/// </summary>
public class DapperCreateOrderHandler
{
    public static async Task Handle(
        CreateOrder command,
        IMessageContext context,
        NpgsqlDataSource dataSource)
    {
        // Cast to the concrete MessageContext to access EnlistInOutboxAsync.
        // In handler execution, IMessageContext always resolves to MessageContext.
        var messageContext = (MessageContext)context;

        // Get Wolverine's backing message database from the message context.
        // This avoids needing to register IMessageDatabase explicitly in DI
        // (it is accessible via the runtime context instead).
        if (!MessageDatabaseExtensions.TryFindMessageDatabase(messageContext, out var messageDb))
            throw new InvalidOperationException("No IMessageDatabase found. Ensure PersistMessagesWithPostgresql() is configured.");

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Enlist Wolverine's outbox in our transaction.
        var envelopeTransaction = new DatabaseEnvelopeTransaction(messageDb!, tx);
        await messageContext.EnlistInOutboxAsync(envelopeTransaction);

        if (command.Amount < 0)
            throw new ArgumentException($"Amount must be non-negative, got {command.Amount}");

        await conn.ExecuteAsync(
            """
            INSERT INTO dapper_demo.orders (id, customer_name, amount, status, created_at)
            VALUES (@Id, @CustomerName, @Amount, @Status, @CreatedAt)
            """,
            new { Id = command.OrderId, command.CustomerName, command.Amount, Status = "Pending", CreatedAt = DateTime.UtcNow },
            tx);

        // PublishAsync writes the envelope to the outbox table via our tx.
        await context.PublishAsync(new OrderCreated(command.OrderId, command.CustomerName, command.Amount));

        // Atomic commit: both application data and outbox entry committed together.
        await tx.CommitAsync();
    }
}

/// <summary>Updates order status to "Confirmed" to prove the round-trip works.</summary>
public class DapperOrderCreatedHandler
{
    public static async Task Handle(OrderCreated @event, NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE dapper_demo.orders SET status = 'Confirmed' WHERE id = @Id",
            new { Id = @event.OrderId });
    }
}
