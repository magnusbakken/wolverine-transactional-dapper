using Contracts;
using EfCoreDemo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EfCoreDemo.Handlers;

/// <summary>
/// Handles the OrderCreated event. This handler demonstrates that the outbox
/// guarantee holds end-to-end: this message is ONLY received when the creating
/// transaction fully committed (both the Order row and the outbox entry).
/// </summary>
public class OrderCreatedHandler
{
    public static async Task Handle(
        OrderCreated @event,
        AppDbContext db,
        ILogger<OrderCreatedHandler> logger)
    {
        // Verify the order actually exists in the database — proof that the
        // outbox message was delivered only after the transaction committed.
        var order = await db.Orders.FindAsync(@event.OrderId);
        if (order is null)
        {
            logger.LogError(
                "[EF Core] BUG: Received OrderCreated for {OrderId} but order does NOT exist in DB!",
                @event.OrderId);
            return;
        }

        order.Status = "Confirmed";

        logger.LogInformation(
            "[EF Core] Order {OrderId} confirmed for {CustomerName} ({Amount:C}). " +
            "DB record verified — transactional outbox is working correctly.",
            @event.OrderId, @event.CustomerName, @event.Amount);
    }
}
