using Contracts;
using Dapper;
using Npgsql;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace DapperDemo.Handlers;

/// <summary>
/// Handles the CreateOrder command using Dapper with Wolverine's outbox pattern.
///
/// KEY FINDING: The outbox pattern DOES work with Dapper and raw ADO.NET.
/// The mechanism is DatabaseEnvelopeTransaction from Wolverine.RDBMS, which
/// bridges a raw DbTransaction (NpgsqlTransaction) into Wolverine's outbox
/// infrastructure. However, unlike the EF Core handler, NONE of this is
/// automatic — every step must be written explicitly.
///
/// WHAT WOLVERINE DOES AUTOMATICALLY WITH EF CORE (zero boilerplate):
///   1. Opens connection and begins a transaction on the DbContext
///   2. Runs the handler
///   3. Calls SaveChangesAsync() — persists application data
///   4. Writes outgoing messages to outbox table (same transaction)
///   5. Commits the transaction
///
/// WHAT YOU MUST DO MANUALLY WITH DAPPER (~15 extra lines):
///   1. Open a NpgsqlConnection
///   2. Begin a NpgsqlTransaction
///   3. Cast IMessageContext to MessageContext (concrete type)
///   4. Get IMessageDatabase via MessageDatabaseExtensions.TryFindMessageDatabase
///   5. Create DatabaseEnvelopeTransaction and call EnlistInOutboxAsync()
///   6. Execute Dapper queries with the transaction parameter
///   7. Call context.PublishAsync() explicitly (cannot use return-value cascade)
///   8. Call tx.CommitAsync()
///
/// RESULT: Same transactional guarantee — business data and outbox message
/// are committed atomically or rolled back together — but with explicit code.
/// </summary>
public class CreateOrderHandler
{
    public static async Task Handle(
        CreateOrder command,
        IMessageContext context,
        NpgsqlDataSource dataSource,
        ILogger<CreateOrderHandler> logger)
    {
        // Step 1: Cast to the concrete MessageContext to access outbox APIs.
        // In handler execution, IMessageContext always resolves to MessageContext.
        // Note: EnlistInOutboxAsync is NOT on the IMessageContext interface;
        // it's only on the concrete MessageContext class.
        var messageContext = (MessageContext)context;

        // Step 2: Get Wolverine's backing message database from the runtime context.
        // TryFindMessageDatabase accesses the IMessageDatabase instance from the
        // WolverineFx runtime — no need to register it in DI separately.
        if (!MessageDatabaseExtensions.TryFindMessageDatabase(messageContext, out var messageDb))
            throw new InvalidOperationException(
                "No IMessageDatabase found. Ensure PersistMessagesWithPostgresql() is configured.");

        // Step 3: Open a connection and start a transaction.
        // With EF Core, Wolverine does this behind the scenes via generated code.
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Step 4: Enlist Wolverine's outbox in our transaction.
        // DatabaseEnvelopeTransaction wraps our NpgsqlTransaction and implements
        // IEnvelopeTransaction. When context.PublishAsync() is called below,
        // it writes the envelope to the outbox table via OUR transaction — not a
        // separate connection — so the envelope write is part of the same atomic
        // commit as our application data writes.
        var envelopeTransaction = new DatabaseEnvelopeTransaction(messageDb!, tx);
        await messageContext.EnlistInOutboxAsync(envelopeTransaction);

        // Step 5: Validate AFTER enlisting so that a validation failure correctly
        // rolls back. The tx rolls back on DisposeAsync() if CommitAsync() was
        // never called — identical to the EF Core behavior.
        if (command.Amount < 0)
        {
            logger.LogWarning(
                "[Dapper] Rejecting order {OrderId}: amount {Amount} is negative. " +
                "Transaction will roll back — no DB row and no outbox entry will be persisted.",
                command.OrderId, command.Amount);
            // await using tx → DisposeAsync() → automatic rollback.
            throw new ArgumentException(
                $"Order amount must be non-negative, got {command.Amount}",
                nameof(command));
        }

        // Step 6: Write business data using Dapper within the transaction.
        await conn.ExecuteAsync(
            """
            INSERT INTO dapper_demo.orders (id, customer_name, amount, status, created_at)
            VALUES (@Id, @CustomerName, @Amount, @Status, @CreatedAt)
            """,
            new
            {
                Id = command.OrderId,
                command.CustomerName,
                command.Amount,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow,
            },
            tx);   // ← Must pass the transaction to Dapper; EF Core does this automatically.

        logger.LogInformation(
            "[Dapper] Persisting order {OrderId} for {CustomerName} ({Amount:C})",
            command.OrderId, command.CustomerName, command.Amount);

        // Step 7: Publish the outgoing message.
        // Because we enlisted via EnlistInOutboxAsync(), this call writes the
        // OrderCreated envelope to Wolverine's outbox table USING our tx —
        // same connection, same NpgsqlTransaction.
        await context.PublishAsync(new OrderCreated(command.OrderId, command.CustomerName, command.Amount));

        // Step 8: Commit — both the business data and the outbox entry are
        // committed atomically. If this throws, both roll back.
        await tx.CommitAsync();

        logger.LogInformation(
            "[Dapper] Order {OrderId} committed. Outbox entry will be relayed to RabbitMQ.",
            command.OrderId);
    }
}
