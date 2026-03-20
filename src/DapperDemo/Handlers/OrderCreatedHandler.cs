using Contracts;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Logging;

namespace DapperDemo.Handlers;

/// <summary>
/// Handles the OrderCreated event. Verifies that the order record is in the
/// database, proving that the outbox message was only delivered AFTER the
/// creating transaction committed — the core guarantee of the outbox pattern.
/// </summary>
public class OrderCreatedHandler
{
    public static async Task Handle(
        OrderCreated @event,
        NpgsqlDataSource dataSource,
        ILogger<OrderCreatedHandler> logger)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        var order = await conn.QuerySingleOrDefaultAsync(
            "SELECT id, customer_name, amount, status FROM dapper_demo.orders WHERE id = @Id",
            new { Id = @event.OrderId });

        if (order is null)
        {
            logger.LogError(
                "[Dapper] BUG: Received OrderCreated for {OrderId} but order does NOT exist in DB! " +
                "This would indicate a broken outbox — the message was sent before the commit.",
                @event.OrderId);
            return;
        }

        // Update the status to Confirmed using a separate transaction.
        await using var updateTx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "UPDATE dapper_demo.orders SET status = 'Confirmed' WHERE id = @Id",
            new { Id = @event.OrderId },
            updateTx);
        await updateTx.CommitAsync();

        logger.LogInformation(
            "[Dapper] Order {OrderId} confirmed for {CustomerName} ({Amount:C}). " +
            "DB record verified — transactional outbox is working correctly.",
            @event.OrderId, @event.CustomerName, @event.Amount);
    }
}
