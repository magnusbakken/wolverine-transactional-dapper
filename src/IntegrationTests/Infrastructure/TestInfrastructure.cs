using Dapper;
using Npgsql;

namespace IntegrationTests.Infrastructure;

/// <summary>
/// Shared infrastructure for integration tests.
/// Reads PostgreSQL connection string from the environment variable
/// WOLVERINE_DEMO_CONNECTION_STRING, falling back to the docker-compose defaults.
/// </summary>
public static class TestInfrastructure
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("WOLVERINE_DEMO_CONNECTION_STRING")
        ?? "Host=localhost;Port=5432;Database=wolverine_demo;Username=wolverine;Password=wolverine";

    /// <summary>
    /// Returns true if a real PostgreSQL connection is available.
    /// </summary>
    public static async Task<bool> IsPostgresAvailableAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Clears test data between test runs.
    /// Uses TRUNCATE rather than DROP+CREATE so that EF Core's EnsureCreatedAsync
    /// can create the tables on first run without having to re-create them on each test.
    /// </summary>
    public static async Task ClearTestDataAsync(string wolverineSchema, string appSchema)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // Ensure the application schema exists (first-run idempotency).
        await conn.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS {appSchema}");

        // Clear the orders table if it exists (don't drop it — let EnsureCreatedAsync create it).
        var ordersTableExists = await conn.QuerySingleAsync<int>(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = @Schema AND table_name = 'orders'
            """,
            new { Schema = appSchema });

        if (ordersTableExists > 0)
            await conn.ExecuteAsync($"TRUNCATE {appSchema}.orders");

        // Clear Wolverine outbox/inbox tables if they exist.
        var wolverineTableExists = await conn.QuerySingleAsync<int>(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = @Schema AND table_name = 'wolverine_outgoing_envelopes'
            """,
            new { Schema = wolverineSchema });

        if (wolverineTableExists > 0)
        {
            await conn.ExecuteAsync($"DELETE FROM {wolverineSchema}.wolverine_outgoing_envelopes");
            await conn.ExecuteAsync($"DELETE FROM {wolverineSchema}.wolverine_incoming_envelopes");
        }
    }
}
