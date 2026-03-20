namespace Contracts;

/// <summary>
/// Command to create a new order. Published by an HTTP endpoint and consumed by
/// both the EfCoreDemo and DapperDemo apps (via separate queues).
/// </summary>
/// <param name="OrderId">Unique identifier for the order.</param>
/// <param name="CustomerName">Name of the customer placing the order.</param>
/// <param name="Amount">Order amount. Use a negative value to trigger a failure scenario.</param>
public record CreateOrder(Guid OrderId, string CustomerName, decimal Amount);

/// <summary>
/// Event published after an order has been successfully created and persisted.
/// Only published when the database transaction commits successfully.
/// </summary>
public record OrderCreated(Guid OrderId, string CustomerName, decimal Amount);
