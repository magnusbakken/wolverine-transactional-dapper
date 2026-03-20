using Dapper;
using Npgsql;

namespace DapperDemo.Infrastructure;

/// <summary>
/// Creates the database schema and tables for the DapperDemo on startup.
/// With Dapper there is no ORM migration framework — we manage schema manually.
/// </summary>
public class DatabaseInitializer(NpgsqlDataSource dataSource, ILogger<DatabaseInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[Dapper] Initializing database schema...");

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS dapper_demo;

            CREATE TABLE IF NOT EXISTS dapper_demo.orders (
                id           UUID         NOT NULL PRIMARY KEY,
                customer_name VARCHAR(200) NOT NULL,
                amount       NUMERIC(18,2) NOT NULL,
                status       VARCHAR(50)  NOT NULL DEFAULT 'Pending',
                created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );
            """);

        logger.LogInformation("[Dapper] Schema initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
