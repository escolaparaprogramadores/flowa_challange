using System.Data;
using Dapper;
using Flowa.Shared;
using Npgsql;

namespace OrderAccumulator.Persistence;

// A lista de ordens da tela e o "Deletar tudo" (GET e DELETE /api/orders).
public sealed class OrderHistoryRepository(NpgsqlDataSource orderDatabaseDataSource)
{
    public const int OrdersPerPage = 10;

    private const string CountStoredOrdersSql = "SELECT count(*) FROM orders";

    // Usa o índice orders_received_at_id_idx do Schema.sql; o id desempata ordens do mesmo instante.
    private const string SelectStoredOrderPageSql = """
        SELECT received_at AS ReceivedAt, accepted AS Accepted, symbol AS Symbol, side AS Side,
               quantity AS Quantity, price AS Price, order_id AS OrderId, cl_ord_id AS ClOrdId
        FROM orders
        ORDER BY received_at DESC, id DESC
        LIMIT @OrdersPerPage OFFSET @SkippedOrders
        """;

    // Zera em vez de apagar: sem a linha do símbolo o processamento de ordem lança exceção
    // (PostgresOrderProcessor.TryMoveExposureAsync).
    private const string ZeroSymbolExposuresSql = "UPDATE exposures SET exposure = 0 WHERE symbol = ANY(@Symbols)";

    private const string DeleteAllStoredOrdersSql = "DELETE FROM orders";

    // Total e página saem da mesma foto do banco, para o total não contar uma ordem que a página não viu.
    public async Task<StoredOrderPage> ReadStoredOrderPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseConnection = await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);
        await using var orderPageTransaction = await orderDatabaseConnection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var totalStoredOrders = await orderDatabaseConnection.ExecuteScalarAsync<long>(new CommandDefinition(
            CountStoredOrdersSql, transaction: orderPageTransaction, cancellationToken: cancellationToken));
        var storedOrdersOfPage = await orderDatabaseConnection.QueryAsync<StoredOrderListRow>(new CommandDefinition(
            SelectStoredOrderPageSql,
            new { OrdersPerPage, SkippedOrders = (pageNumber - 1) * OrdersPerPage },
            orderPageTransaction,
            cancellationToken: cancellationToken));

        await orderPageTransaction.CommitAsync(cancellationToken);
        return new StoredOrderPage(totalStoredOrders, storedOrdersOfPage.ToList());
    }

    // Tudo ou nada: primeiro zera a exposição dos três símbolos, depois apaga as ordens.
    public async Task DeleteAllOrdersAndZeroExposuresAsync(CancellationToken cancellationToken = default)
    {
        await using var orderDatabaseConnection = await orderDatabaseDataSource.OpenConnectionAsync(cancellationToken);
        await using var deleteAllOrdersTransaction = await orderDatabaseConnection.BeginTransactionAsync(cancellationToken);

        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            ZeroSymbolExposuresSql, new { Symbols = OrderRules.AllowedOrderSymbols.ToArray() }, deleteAllOrdersTransaction, cancellationToken: cancellationToken));
        await orderDatabaseConnection.ExecuteAsync(new CommandDefinition(
            DeleteAllStoredOrdersSql, transaction: deleteAllOrdersTransaction, cancellationToken: cancellationToken));

        await deleteAllOrdersTransaction.CommitAsync(cancellationToken);
    }
}

public sealed record StoredOrderPage(long TotalStoredOrders, IReadOnlyList<StoredOrderListRow> StoredOrders);

public sealed record StoredOrderListRow(
    DateTime ReceivedAt,
    bool Accepted,
    string? Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string OrderId,
    string ClOrdId);
