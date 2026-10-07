using System.Diagnostics.Metrics;
using Flowa.Commons.Database;
using Flowa.Commons.Observability;
using Flowa.OrderAccumulator.Infrastructure.DependencyInjection;
using Dapper;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flowa.OrderAccumulator.Tests;

// The app starts against its own empty database: the migration has to run at startup. It starts as a worker, without HTTP.
[Collection(OrderAccumulatorPostgresCollection.Name)]
public sealed class OrderAccumulatorStartupTests(OrderAccumulatorPostgresFixture orderAccumulatorDatabase) : IAsyncLifetime
{
    private string emptyOrderAccumulatorDatabaseConnectionString = null!;

    public async Task InitializeAsync()
    {
        var emptyOrderAccumulatorDatabaseName = "startup_" + Guid.NewGuid().ToString("N");
        await using (var postgresAdminConnection = await orderAccumulatorDatabase.OrderDatabaseDataSource.OpenConnectionAsync())
            await postgresAdminConnection.ExecuteAsync($"CREATE DATABASE {emptyOrderAccumulatorDatabaseName}");

        emptyOrderAccumulatorDatabaseConnectionString = new NpgsqlConnectionStringBuilder(orderAccumulatorDatabase.OrderDatabaseConnectionString)
        {
            Database = emptyOrderAccumulatorDatabaseName
        }.ConnectionString;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // CA-9: no web server is registered, so nothing answers /health, /version, /api/exposures or /api/orders.
    [Fact]
    public async Task Worker_starts_without_any_http_server()
    {
        // Arrange
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(emptyOrderAccumulatorDatabaseConnectionString).StartWithFixAcceptorAsync();

        // Act
        var registeredHttpServer = orderAccumulatorTestApp.Services.GetService<IServer>();

        // Assert
        Assert.Null(registeredHttpServer);
    }

    // The Commons is shared by the apps, so the meter and the duration metric come from the app: the Program of the
    // OrderAccumulator has to register the use case measurement with its own names, the ones the dashboard reads.
    [Fact]
    public async Task Program_registers_the_use_case_measurement_with_the_order_accumulator_meter_and_metric()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(emptyOrderAccumulatorDatabaseConnectionString).StartWithFixAcceptorAsync();
        var orderAccumulatorMeterFactory = orderAccumulatorTestApp.Services.GetRequiredService<IMeterFactory>();
        var recordedUseCaseDurations = new List<(string MeterName, string MetricName, string? MetricUnit)>();
        using var useCaseDurationListener = new MeterListener
        {
            InstrumentPublished = (publishedInstrument, listener) =>
            {
                if (publishedInstrument.Meter.Scope == orderAccumulatorMeterFactory)
                    listener.EnableMeasurementEvents(publishedInstrument);
            }
        };
        useCaseDurationListener.SetMeasurementEventCallback<double>((publishedInstrument, _, _, _) =>
            recordedUseCaseDurations.Add((publishedInstrument.Meter.Name, publishedInstrument.Name, publishedInstrument.Unit)));
        useCaseDurationListener.Start();

        using (orderAccumulatorTestApp.Services.GetRequiredService<IOperationMonitoring>().StartOperationMonitoring("orders.decide-incoming-order"))
        {
        }

        Assert.Equal(("Flowa.OrderAccumulator", "orderaccumulator.usecase.duration", "s"), Assert.Single(recordedUseCaseDurations));
    }

    [Fact]
    public async Task Startup_creates_the_tables_and_one_zeroed_row_per_symbol()
    {
        await using var orderAccumulatorTestApp = await new OrderAccumulatorFixTestHost(emptyOrderAccumulatorDatabaseConnectionString).StartWithFixAcceptorAsync();

        await using var orderDatabaseConnection = new NpgsqlConnection(emptyOrderAccumulatorDatabaseConnectionString);
        var symbolExposureRows = (await orderDatabaseConnection.QueryAsync<(string Symbol, decimal Exposure)>(
            "SELECT symbol, exposure FROM exposures ORDER BY symbol")).ToList();
        var storedOrderCount = await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM orders");

        Assert.Equal([("PETR4", 0m), ("VALE3", 0m), ("VIIA4", 0m)], symbolExposureRows);
        Assert.Equal(0, storedOrderCount);
    }

    [Fact]
    public async Task Many_instances_applying_the_schema_at_once_on_an_empty_database_all_succeed()
    {
        await using var emptyOrderAccumulatorDatabase = NpgsqlDataSource.Create(emptyOrderAccumulatorDatabaseConnectionString);
        await using var emptyOrderAccumulatorConnectionSource = new PostgresConnectionSource(emptyOrderAccumulatorDatabaseConnectionString);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => emptyOrderAccumulatorConnectionSource.ApplyOrderAccumulatorSchemaAsync())));

        await using var orderDatabaseConnection = await emptyOrderAccumulatorDatabase.OpenConnectionAsync();
        Assert.Equal(3, await orderDatabaseConnection.ExecuteScalarAsync<long>("SELECT count(*) FROM exposures"));
    }
}
