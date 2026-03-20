using Contracts;
using EfCoreDemo;
using EfCoreDemo.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PostgreSQL")
    ?? throw new InvalidOperationException("PostgreSQL connection string not found.");

var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
var rabbitPort = int.Parse(builder.Configuration["RabbitMQ:Port"] ?? "5672");
var rabbitUser = builder.Configuration["RabbitMQ:Username"] ?? "guest";
var rabbitPass = builder.Configuration["RabbitMQ:Password"] ?? "guest";

// Register AppDbContext with Wolverine integration.
//
// This does two things beyond a normal AddDbContext call:
//   1. Tells Wolverine about this DbContext type so the transactional middleware
//      can automatically be applied to any handler that takes an AppDbContext param.
//   2. Calls MapWolverineEnvelopeStorage() in OnModelCreating — this maps the
//      Wolverine inbox/outbox tables into EF Core's model, enabling EF Core to
//      batch-write outgoing envelopes in the SAME SaveChangesAsync() call as
//      application data. One round-trip instead of two.
//
// The "efcore" schema is where Wolverine will create its own tables
// (incoming_envelopes, outgoing_envelopes, dead_letters, etc.).
builder.Services.AddDbContextWithWolverineIntegration<AppDbContext>(
    opts => opts.UseNpgsql(connectionString),
    "efcore");

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "EfCoreDemo";

    // Use the same PostgreSQL database for Wolverine's durable message store.
    // Wolverine creates its own tables in the "efcore" schema.
    // Because AppDbContext maps those tables via MapWolverineEnvelopeStorage(),
    // outbox writes happen through EF Core — same connection, same transaction.
    opts.PersistMessagesWithPostgresql(connectionString, "efcore");

    // Enable the EF Core transactional middleware globally.
    // Any handler that declares an AppDbContext (or any DbContext) parameter
    // will automatically be wrapped in a transaction that covers:
    //   1. AppDbContext.SaveChangesAsync()
    //   2. Flushing outgoing messages to the outbox table
    // Both succeed or both fail — no [Transactional] attribute needed per-handler.
    opts.UseEntityFrameworkCoreTransactions();

    // Auto-apply transactional middleware to ALL handlers that declare a DbContext
    // parameter — no per-handler attribute needed.
    opts.Policies.AutoApplyTransactions();

    // Make all incoming message queues use durable inbox:
    // messages are stored in PostgreSQL BEFORE being delivered to the handler.
    // If the app crashes mid-handler, the message is retried on restart.
    opts.Policies.UseDurableInboxOnAllListeners();

    // Make all outgoing send operations use durable outbox:
    // messages are stored in PostgreSQL BEFORE being forwarded to RabbitMQ.
    // If the relay crashes before forwarding, it picks up on restart.
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = rabbitHost;
        rabbit.Port = rabbitPort;
        rabbit.UserName = rabbitUser;
        rabbit.Password = rabbitPass;
    })
    // Create exchanges and queues on RabbitMQ at startup if they don't exist.
    .AutoProvision();

    // Listen for incoming CreateOrder commands on a dedicated durable queue.
    opts.ListenToRabbitQueue("efcore-create-orders");

    // Route outgoing CreateOrder commands to the queue.
    // Normally this wouldn't be in the same app — but here both publisher (HTTP
    // endpoint) and consumer (handler) live together to keep the demo self-contained.
    opts.PublishMessage<CreateOrder>()
        .ToRabbitQueue("efcore-create-orders");

    // Listen for OrderCreated events (to show the full round-trip).
    opts.ListenToRabbitQueue("efcore-order-created");

    // Route outgoing OrderCreated events to the queue.
    opts.PublishMessage<OrderCreated>()
        .ToRabbitQueue("efcore-order-created");
});

var app = builder.Build();

// Run EF Core migrations and Wolverine schema setup at startup.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // EnsureCreated creates application tables + Wolverine envelope tables
    // (because MapWolverineEnvelopeStorage() is in the model).
    await db.Database.EnsureCreatedAsync();
}

// POST /orders — trigger the inbox/outbox demo
// Request body: { "customerName": "Alice", "amount": 42.00 }
// Use a negative amount to see transaction rollback in action.
//
// Flow:
//   POST /orders
//   → bus.PublishAsync(CreateOrder) → durable outbox → RabbitMQ efcore-create-orders queue
//   → durable inbox → CreateOrderHandler (EF Core auto-transaction):
//       BEGIN TRANSACTION
//         INSERT INTO efcore_demo.orders
//         INSERT INTO efcore.outgoing_envelopes  ← outbox entry for OrderCreated
//       COMMIT
//   → relay sends OrderCreated to RabbitMQ efcore-order-created queue
//   → durable inbox → OrderCreatedHandler verifies DB record exists
app.MapPost("/orders", async (
    [FromBody] CreateOrderRequest request,
    [FromServices] IMessageBus bus) =>
{
    var orderId = Guid.NewGuid();
    await bus.PublishAsync(new CreateOrder(orderId, request.CustomerName, request.Amount));
    return Results.Accepted($"/orders/{orderId}", new { orderId });
});

// GET /orders — list all persisted orders (confirms DB writes succeeded)
app.MapGet("/orders", async ([FromServices] AppDbContext db) =>
    await db.Orders.OrderByDescending(o => o.CreatedAt).ToListAsync());

// GET /orders/{id} — check a specific order's status
app.MapGet("/orders/{id:guid}", async (Guid id, [FromServices] AppDbContext db) =>
    await db.Orders.FindAsync(id) is Order order ? Results.Ok(order) : Results.NotFound());

app.Run();

// ReSharper disable once ClassNeverInstantiated.Global
public record CreateOrderRequest(string CustomerName, decimal Amount);
