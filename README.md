# wolverine-transactional-dapper

Demonstrates the transactional inbox/outbox pattern with [WolverineFx](https://wolverinefx.io),
comparing the **EF Core automatic middleware** approach against **Dapper with manual
outbox enrollment**.

---

## Key Findings

> **The outbox pattern works with both EF Core and Dapper.** The transactional
> *guarantee* is identical. The difference is in how much code you must write.

| | EF Core | Dapper |
|---|---|---|
| Transaction opened | Automatic (middleware) | Manual |
| `SaveChanges` / Dapper queries | Automatic / N/A | Manual (must pass `tx`) |
| Outbox enlisted in transaction | Automatic | Manual (`DatabaseEnvelopeTransaction`) |
| Message published to outbox | Via return value | `context.PublishAsync()` |
| Transaction committed | Automatic | Manual |
| Extra lines of boilerplate | 0 | ~15 |
| Transactional guarantee | ✅ Atomic | ✅ Atomic |

The popular assumption — _"Wolverine's outbox only works automatically with EF Core or
Marten"_ — is **partially correct**: the **automatic middleware** that does everything
with zero handler code does require EF Core (or Marten). But the underlying outbox
infrastructure (`DatabaseEnvelopeTransaction` from `WolverineFx.RDBMS`) can be used
directly with any `NpgsqlTransaction`, enabling the same atomicity guarantee with Dapper.

---

## Repository Structure

```
WolverineDemo.sln
docker-compose.yml          ← PostgreSQL + RabbitMQ infrastructure
src/
  Contracts/                ← Shared message types (CreateOrder, OrderCreated)
  EfCoreDemo/               ← ASP.NET Core app: automatic EF Core outbox middleware
  DapperDemo/               ← ASP.NET Core app: manual Dapper outbox enrollment
  IntegrationTests/         ← xUnit tests proving transactional correctness
```

---

## Infrastructure

Start the PostgreSQL and RabbitMQ services using Docker Compose:

```bash
docker-compose up -d
```

| Service | Port | Credentials |
|---|---|---|
| PostgreSQL | 5432 | wolverine / wolverine / wolverine_demo |
| RabbitMQ | 5672 | guest / guest |
| RabbitMQ Management | 15672 | guest / guest |

---

## Running the Demos

Both demos expose the same HTTP API on different ports:

```bash
# Terminal 1 — EF Core demo (port 5001)
cd src/EfCoreDemo
dotnet run --urls http://localhost:5001

# Terminal 2 — Dapper demo (port 5002)
cd src/DapperDemo
dotnet run --urls http://localhost:5002
```

### Create a successful order

```bash
# EF Core demo
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -d '{"customerName": "Alice", "amount": 99.99}'

# Dapper demo
curl -X POST http://localhost:5002/orders \
  -H "Content-Type: application/json" \
  -d '{"customerName": "Bob", "amount": 149.50}'
```

### Trigger a failure (negative amount → transaction rollback)

```bash
curl -X POST http://localhost:5001/orders \
  -H "Content-Type: application/json" \
  -d '{"customerName": "Failure", "amount": -10}'
```

In the logs you will see the handler reject the order. In the database you will see
**no order row** and **no outbox entry** — the transaction rolled back atomically.

### Verify the database

```bash
# EF Core orders
curl http://localhost:5001/orders

# Dapper orders
curl http://localhost:5002/orders
```

---

## How It Works

### Full Message Flow (same for both demos)

```
POST /orders
  → bus.PublishAsync(CreateOrder)
  → [durable outbox] persisted to PostgreSQL
  → [relay agent] forwarded to RabbitMQ
  → [durable inbox] persisted before handler runs
  → CreateOrderHandler
        BEGIN TRANSACTION
          INSERT INTO orders ...
          INSERT INTO outgoing_envelopes ...  ← same transaction
        COMMIT
  → [relay agent] forwards committed OrderCreated to RabbitMQ
  → OrderCreatedHandler verifies DB record, updates status to "Confirmed"
```

### Durable Endpoints

Both apps configure WolverineFx with durable endpoints:

```csharp
opts.Policies.UseDurableInboxOnAllListeners();     // Store incoming before processing
opts.Policies.UseDurableOutboxOnAllSendingEndpoints(); // Store outgoing before sending
```

**Durable inbox**: When a `CreateOrder` arrives from RabbitMQ, it is first written to
`{schema}.wolverine_incoming_envelopes` in PostgreSQL. Only then is the handler invoked.
If the app crashes mid-handler, the message is replayed on restart.

**Durable outbox**: Outgoing `OrderCreated` messages are written to
`{schema}.wolverine_outgoing_envelopes` _within the same transaction_ as the business
data. A background relay agent reads committed entries and forwards them to RabbitMQ.
If the relay crashes after commit but before the RabbitMQ send, it picks up on restart.

---

## EF Core Demo — Automatic Transaction Middleware

### Setup (`Program.cs`)

```csharp
builder.Services.AddDbContextWithWolverineIntegration<AppDbContext>(
    opts => opts.UseNpgsql(connectionString),
    "efcore");  // Wolverine schema name

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString, "efcore");
    opts.UseEntityFrameworkCoreTransactions();   // Enable EF Core middleware
    opts.Policies.AutoApplyTransactions();       // Auto-detect DbContext in handlers
    // ...
});
```

`AddDbContextWithWolverineIntegration` registers a `WolverineModelCustomizer` that
maps Wolverine's envelope tables into the `AppDbContext` EF Core model. This enables
**command batching**: the `SaveChangesAsync()` call writes the Order row and the outbox
entry in a single round-trip.

### Handler (`CreateOrderHandler.cs`)

```csharp
public class CreateOrderHandler
{
    // Zero boilerplate — the middleware wraps this in a transaction automatically.
    public static OrderCreated Handle(CreateOrder command, AppDbContext db)
    {
        if (command.Amount < 0)
            throw new ArgumentException("...");  // Rolls back everything

        db.Orders.Add(new Order { ... });

        // Returning a message routes it through the outbox atomically.
        // No SaveChangesAsync(), no transaction management needed.
        return new OrderCreated(command.OrderId, ...);
    }
}
```

WolverineFx generates code at startup (discoverable via `dotnet run -- codegen preview`)
that wraps the handler:

```csharp
// Generated code (simplified):
var dbContext = serviceProvider.GetRequiredService<AppDbContext>();
await using var tx = await dbContext.Database.BeginTransactionAsync();
try {
    var result = EfCoreCreateOrderHandler.Handle(command, dbContext);
    await dbContext.SaveChangesAsync();
    await outbox.StoreOutgoingEnvelopeAsync(result, tx);  // same tx
    await tx.CommitAsync();
} catch {
    await tx.RollbackAsync();
    throw;
}
```

---

## Dapper Demo — Manual Outbox Enrollment

### Setup (`Program.cs`)

```csharp
builder.Services.AddNpgsqlDataSource(connectionString);

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString, "dapper");
    // Note: No UseEntityFrameworkCoreTransactions() — not applicable for Dapper.
    // Note: No AutoApplyTransactions() — there is no DbContext to detect.
    // ...
});
```

### Handler (`CreateOrderHandler.cs`)

```csharp
public class CreateOrderHandler
{
    public static async Task Handle(
        CreateOrder command,
        IMessageContext context,
        NpgsqlDataSource dataSource)
    {
        // Step 1: Cast to concrete type to access outbox APIs.
        // EnlistInOutboxAsync is NOT on IMessageContext — only on MessageContext.
        var messageContext = (MessageContext)context;

        // Step 2: Get Wolverine's message database from the runtime context.
        if (!MessageDatabaseExtensions.TryFindMessageDatabase(messageContext, out var messageDb))
            throw new InvalidOperationException("...");

        // Step 3: Open connection and start transaction.
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Step 4: Enlist the outbox in our transaction.
        // DatabaseEnvelopeTransaction wraps our NpgsqlTransaction. When
        // context.PublishAsync() is called, it writes the envelope to the outbox
        // table using OUR transaction — same connection, same commit.
        var envelopeTransaction = new DatabaseEnvelopeTransaction(messageDb, tx);
        await messageContext.EnlistInOutboxAsync(envelopeTransaction);

        if (command.Amount < 0)
            throw new ArgumentException("...");  // tx rolls back on DisposeAsync()

        // Step 5: Write business data via Dapper — must pass tx explicitly.
        await conn.ExecuteAsync("INSERT INTO orders ...", params, tx);

        // Step 6: Publish event — writes to outbox table via our tx.
        await context.PublishAsync(new OrderCreated(command.OrderId, ...));

        // Step 7: Commit both business data and outbox entry atomically.
        await tx.CommitAsync();
    }
}
```

---

## Technical Details: How DatabaseEnvelopeTransaction Works

`DatabaseEnvelopeTransaction` is the bridge between a raw `DbTransaction` and
Wolverine's `IEnvelopeTransaction` interface:

```csharp
// From Wolverine.RDBMS:
public class DatabaseEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly IMessageDatabase _database;
    private readonly DbTransaction _tx;

    public Task PersistOutgoingAsync(Envelope[] envelopes)
        => _database.StoreOutgoingAsync(_tx, envelopes);  // uses OUR transaction

    public Task PersistIncomingAsync(Envelope envelope)
        => _database.StoreIncomingAsync(_tx, envelope);   // uses OUR transaction
}
```

When `context.PublishAsync(message)` is called with an active `DatabaseEnvelopeTransaction`,
Wolverine writes the serialized envelope to `{schema}.wolverine_outgoing_envelopes` using
the `NpgsqlTransaction` you provided. The row is uncommitted until you call
`tx.CommitAsync()` — at which point both your business data row and the outbox entry
are committed in the same PostgreSQL transaction.

---

## Evaluation: Should You Use Wolverine + Dapper + PostgreSQL?

### Pros
- **Same durability guarantee** as EF Core (atomic business data + outbox entry)
- **Full control** over SQL — use any Dapper query, stored procedure, or raw ADO.NET
- **No ORM overhead** — ideal for read-heavy or performance-critical paths
- **Incrementally adoptable** — existing Dapper codebase can add Wolverine for messaging

### Cons
- **~15 lines of boilerplate per handler** vs. zero with EF Core
- **Cast required**: `(MessageContext)context` — coupling to an internal concrete type
- **No automatic retry safety**: you must not re-run the DB insert if the outbox
  enlistment fails; careful sequencing is required (enlist first, then insert)
- **No cascade pattern**: cannot return a message from a handler to publish it;
  must call `context.PublishAsync()` explicitly

### Recommendation

**Use Wolverine + Dapper + PostgreSQL when:**
- Your team is committed to Dapper and raw SQL
- You need fine-grained control over query execution
- You are willing to write (and review) the transaction boilerplate consistently

**Use Wolverine + EF Core when:**
- You want zero-boilerplate transaction handling
- Domain complexity benefits from an ORM's change-tracking and migrations
- You want the EF Core cascade pattern (return messages from handlers)

**Hybrid approach**: Use EF Core for writes (in handlers where the outbox pattern
matters) and Dapper for reads (queries). This is a legitimate pattern — you can inject
both `AppDbContext` (for writes + outbox) and `NpgsqlDataSource` (for fast reads)
into the same handler.

---

## Running the Integration Tests

The tests use a real PostgreSQL instance and stub out RabbitMQ (no broker needed):

```bash
# Start PostgreSQL (required)
docker-compose up -d postgres

# Run all tests
dotnet test src/IntegrationTests

# Or with a custom connection string
WOLVERINE_DEMO_CONNECTION_STRING="Host=myserver;Database=mydb;Username=user;Password=pass" \
  dotnet test src/IntegrationTests
```

### What the Tests Prove

| Test | What it proves |
|---|---|
| `SuccessfulOrder_WritesOrderRowAndOutboxEntry_Atomically` | Both DB row and outbox entry are committed together |
| `InvalidOrder_NegativeAmount_RollsBackOrderRow` | Failed handler → no DB row |
| `InvalidOrder_NegativeAmount_WritesNoOutboxEntry` | Failed handler → no outbox entry |
| `FullRoundTrip_OrderCreatedHandlerConfirmsOrderStatus` | OrderCreated only delivered after DB commit |
| `Documentation_UnsafeNonOutboxApproachRisk` | Documents the inconsistency risk without the outbox |

The same four core tests run for both EF Core and Dapper, proving that both approaches
achieve the same transactional guarantee.

---

## NuGet Packages Used

| Package | Purpose |
|---|---|
| `WolverineFx` | Core message bus and handler runtime |
| `WolverineFx.Postgresql` | PostgreSQL durable message store (inbox/outbox tables) |
| `WolverineFx.RabbitMQ` | RabbitMQ transport |
| `WolverineFx.EntityFrameworkCore` | EF Core transactional middleware |
| `WolverineFx.RDBMS` | `DatabaseEnvelopeTransaction` for raw ADO.NET outbox enrollment |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL EF Core provider |
| `Dapper` | Lightweight ORM for SQL queries |
| `Npgsql` | PostgreSQL .NET driver |
