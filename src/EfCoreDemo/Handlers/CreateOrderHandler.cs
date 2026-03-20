using Contracts;
using EfCoreDemo.Domain;
using Microsoft.Extensions.Logging;

namespace EfCoreDemo.Handlers;

/// <summary>
/// Handles the CreateOrder command using EF Core with Wolverine's automatic
/// transactional middleware.
///
/// HOW THE AUTOMATIC TRANSACTION WORKS:
/// When UseEntityFrameworkCoreTransactions() is configured, Wolverine's code
/// generation detects that this handler has an AppDbContext parameter and
/// automatically wraps execution in a transaction:
///
///   1. Begin transaction on AppDbContext
///   2. Invoke this handler method
///   3. Call AppDbContext.SaveChangesAsync()
///   4. Flush outgoing messages to the outbox table (same transaction)
///   5. Commit the transaction
///
/// If the handler throws, all of the above is rolled back — no DB record
/// is persisted and no outbox message is written.
///
/// CONTRAST WITH DAPPER: In the DapperDemo, steps 1-5 require 15+ lines
/// of explicit code. Here they require ZERO extra lines.
/// </summary>
public class CreateOrderHandler
{
    // NOTE: No [Transactional] attribute needed — AutoApplyTransactions() in
    // Program.cs detects the AppDbContext parameter and applies the middleware
    // automatically to any handler that has a DbContext parameter.
    // Wolverine handlers can be plain synchronous methods — no async needed here
    // because we perform no I/O directly; the transactional middleware (generated
    // at startup) handles SaveChangesAsync() and outbox flushing asynchronously
    // before and after this method is called.
    public static OrderCreated Handle(
        CreateOrder command,
        AppDbContext db,
        ILogger<CreateOrderHandler> logger)
    {
        // This validation runs INSIDE the generated transaction. If it throws,
        // the entire transaction — including any outbox messages — is rolled back.
        if (command.Amount < 0)
        {
            logger.LogWarning(
                "[EF Core] Rejecting order {OrderId}: amount {Amount} is negative",
                command.OrderId, command.Amount);
            throw new ArgumentException(
                $"Order amount must be non-negative, got {command.Amount}",
                nameof(command));
        }

        var order = new Order
        {
            Id = command.OrderId,
            CustomerName = command.CustomerName,
            Amount = command.Amount,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
        };

        db.Orders.Add(order);

        // DO NOT call db.SaveChangesAsync() here — the transactional middleware
        // does it automatically after this method returns, within the transaction.

        logger.LogInformation(
            "[EF Core] Persisting order {OrderId} for {CustomerName} ({Amount:C})",
            command.OrderId, command.CustomerName, command.Amount);

        // Returning a message from a Wolverine handler automatically routes it
        // through the outbox. The message is written to the outbox table as part
        // of the same transaction that saves the Order entity. If the transaction
        // commits, the relay agent will forward it to RabbitMQ.
        return new OrderCreated(command.OrderId, command.CustomerName, command.Amount);
    }
}
