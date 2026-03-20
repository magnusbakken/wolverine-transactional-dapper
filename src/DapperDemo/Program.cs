using Contracts;
using Dapper;
using DapperDemo.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException("PostgreSQL connection string not found.");

var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
var rabbitPort = int.Parse(builder.Configuration["RabbitMQ:Port"] ?? "5672");
var rabbitUser = builder.Configuration["RabbitMQ:Username"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMQ:Password"] ?? "guest";

// Register NpgsqlDataSource as a singleton — Dapper handlers inject it directly.
// This is the Npgsql-idiomatic way to manage connection pooling.
// With EF Core, you would register a DbContext instead.
builder.Services.AddNpgsqlDataSource(connectionString);

// Register the database initializer that creates the dapper_demo schema and tables.
// With EF Core, this would be handled by EF Core migrations or EnsureCreated().
builder.Services.AddHostedService<DatabaseInitializer>();

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "DapperDemo";

    // Use PostgreSQL for Wolverine's durable message store (inbox + outbox tables).
    // The "dapper" schema separates DapperDemo's Wolverine tables from EfCoreDemo's.
    //
    // NOTE: With Dapper, there is NO automatic integration between the outbox and
    // application code. Unlike with EF Core (where WolverineFx generates code that
    // writes outbox entries via the DbContext), with Dapper you must explicitly:
    //   1. Create a DatabaseEnvelopeTransaction from the IMessageDatabase
    //   2. Call context.EnlistInOutboxAsync(envelopeTransaction)
    //   3. Manage the transaction lifecycle manually
    //   See CreateOrderHandler.cs for the full pattern.
    opts.PersistMessagesWithPostgresql(connectionString, "dapper");

    // Durable inbox: incoming messages are stored in PostgreSQL before handler runs.
    opts.Policies.UseDurableInboxOnAllListeners();

    // Durable outbox: outgoing messages are stored in PostgreSQL before RabbitMQ send.
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = rabbitHost;
        rabbit.Port = rabbitPort;
        rabbit.UserName = rabbitUser;
        rabbit.Password = rabbitPass;
    })
    .AutoProvision();

    // Listen for incoming CreateOrder commands.
    opts.ListenToRabbitQueue("dapper-create-orders");

    // Route outgoing CreateOrder commands to the queue.
    opts.PublishMessage<CreateOrder>()
        .ToRabbitQueue("dapper-create-orders");

    // Listen for OrderCreated events (full round-trip demo).
    opts.ListenToRabbitQueue("dapper-order-created");

    // Route outgoing OrderCreated events to the queue.
    opts.PublishMessage<OrderCreated>()
        .ToRabbitQueue("dapper-order-created");
});

var app = builder.Build();

// POST /orders — trigger the inbox/outbox demo
// Request body: { "customerName": "Alice", "amount": 42.00 }
// Use a negative amount to see transaction rollback in action.
//
// Flow:
//   POST /orders
//   → bus.PublishAsync(CreateOrder) → durable outbox → RabbitMQ dapper-create-orders queue
//   → durable inbox → CreateOrderHandler:
//       BEGIN TRANSACTION (manual)
//         INSERT INTO dapper_demo.orders  (via Dapper + tx)
//         INSERT INTO dapper.outgoing_envelopes  (via DatabaseEnvelopeTransaction + same tx)
//       COMMIT
//   → relay sends OrderCreated to RabbitMQ dapper-order-created queue
//   → durable inbox → OrderCreatedHandler verifies DB record exists
app.MapPost("/orders", async (
    [FromBody] CreateOrderRequest request,
    [FromServices] IMessageBus bus) =>
{
    var orderId = Guid.NewGuid();
    await bus.PublishAsync(new CreateOrder(orderId, request.CustomerName, request.Amount));
    return Results.Accepted($"/orders/{orderId}", new { orderId });
});

// GET /orders — list all persisted orders
app.MapGet("/orders", async ([FromServices] NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var orders = await conn.QueryAsync(
        "SELECT id, customer_name, amount, status, created_at FROM dapper_demo.orders ORDER BY created_at DESC");
    return Results.Ok(orders);
});

// GET /orders/{id} — check a specific order
app.MapGet("/orders/{id:guid}", async (Guid id, [FromServices] NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var order = await conn.QuerySingleOrDefaultAsync(
        "SELECT id, customer_name, amount, status, created_at FROM dapper_demo.orders WHERE id = @Id",
        new { Id = id });
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.Run();

// ReSharper disable once ClassNeverInstantiated.Global
public record CreateOrderRequest(string CustomerName, decimal Amount);
