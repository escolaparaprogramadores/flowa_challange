using Dapper;
using Flowa.Shared;
using Npgsql;
using OrderAccumulator.Exposure;

namespace OrderAccumulator.Persistence;

public sealed class PostgresOrderProcessor(NpgsqlDataSource dataSource) : IOrderProcessor
{
    // O limite é garantido aqui, no banco, e não em memória: o UPDATE só move a exposição se o
    // novo valor couber. Duas ordens no mesmo símbolo ao mesmo tempo disputam o lock da linha, e a
    // segunda reavalia a condição sobre o valor que a primeira acabou de gravar.
    private const string MoveExposureSql = """
        UPDATE exposures
        SET exposure = exposure + @Delta
        WHERE symbol = @Symbol AND abs(exposure + @Delta) <= @Limit
        """;

    private const string SymbolExistsSql = "SELECT count(*) FROM exposures WHERE symbol = @Symbol";

    // Se o ClOrdID já existe, não grava nada: quem chama desfaz a transação e lê a resposta original.
    private const string InsertOrderSql = """
        INSERT INTO orders (cl_ord_id, order_id, exec_id, symbol, side, quantity, price, accepted, reject_reason)
        VALUES (@ClOrdId, @OrderId, @ExecId, @Symbol, @Side, @Quantity, @Price, @Accepted, @RejectReason)
        ON CONFLICT (cl_ord_id) DO NOTHING
        """;

    private const string SelectOrderSql = """
        SELECT cl_ord_id AS ClOrdId, order_id AS OrderId, exec_id AS ExecId, symbol AS Symbol, side AS Side,
               quantity AS Quantity, price AS Price, accepted AS Accepted, reject_reason AS RejectReason
        FROM orders
        WHERE cl_ord_id = @ClOrdId
        """;

    public async Task<OrderOutcome> ProcessIncomingOrderAsync(IncomingOrder incomingOrder, CancellationToken cancellationToken = default)
    {
        // Sem ClOrdID não há como reconhecer a repetição: ordens diferentes colidiriam na mesma chave.
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingOrder.ClOrdId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Repetição já gravada volta direto, sem disputar o lock da linha do símbolo com ordens novas.
        var storedOutcome = await TryReadStoredOrderOutcomeAsync(connection, incomingOrder.ClOrdId, cancellationToken);
        if (storedOutcome is not null)
            return storedOutcome;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var orderValidation = OrderValidator.ValidateOrderFromFix(
            incomingOrder.Symbol, incomingOrder.Side, incomingOrder.Quantity, incomingOrder.Price);
        var (accepted, rejectReason) = orderValidation.IsOrderValid
            ? await TryMoveExposureAsync(connection, transaction, orderValidation.ValidatedOrder!, cancellationToken)
            : (false, string.Join(' ', orderValidation.OrderFieldErrors.Select(fieldError => fieldError.OrderFieldErrorMessage)));

        var orderOutcome = new OrderOutcome(
            incomingOrder.ClOrdId, NewOrderOrExecId(), NewOrderOrExecId(),
            incomingOrder.Symbol, incomingOrder.Side, incomingOrder.Quantity, incomingOrder.Price,
            accepted, rejectReason, IsRepeat: false);

        var insertedOrders = await connection.ExecuteAsync(new CommandDefinition(
            InsertOrderSql, ToOrderInsertParameters(orderOutcome), transaction, cancellationToken: cancellationToken));

        if (insertedOrders == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return orderOutcome;
        }

        // A mesma ordem chegou em paralelo e a outra gravou primeiro: o rollback desfaz o que esta
        // tentativa mexeu na exposição.
        await transaction.RollbackAsync(cancellationToken);
        return await TryReadStoredOrderOutcomeAsync(connection, incomingOrder.ClOrdId, cancellationToken)
            ?? throw new InvalidOperationException($"A ordem {incomingOrder.ClOrdId} colidiu na chave, mas não foi encontrada.");
    }

    private static async Task<(bool Accepted, string? RejectReason)> TryMoveExposureAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ValidOrder validOrder, CancellationToken cancellationToken)
    {
        var exposureMoveParameters = new
        {
            Symbol = validOrder.OrderSymbol,
            Delta = ExposureLimit.OrderExposureDelta(validOrder.OrderSide, validOrder.OrderQuantity, validOrder.OrderPrice),
            Limit = ExposureLimit.PerSymbol
        };

        var movedExposureRows = await connection.ExecuteAsync(new CommandDefinition(
            MoveExposureSql, exposureMoveParameters, transaction, cancellationToken: cancellationToken));
        if (movedExposureRows == 1)
            return (true, null);

        var exposureRowsOfSymbol = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            SymbolExistsSql, exposureMoveParameters, transaction, cancellationToken: cancellationToken));
        if (exposureRowsOfSymbol == 0)
            throw new InvalidOperationException(
                $"O símbolo {validOrder.OrderSymbol} não tem linha de exposição. A migração do banco não foi aplicada.");

        return (false, ExposureLimit.RejectionText(validOrder.OrderSymbol));
    }

    private static async Task<OrderOutcome?> TryReadStoredOrderOutcomeAsync(
        NpgsqlConnection connection, string clOrdId, CancellationToken cancellationToken)
    {
        var storedOrder = await connection.QuerySingleOrDefaultAsync<StoredOrderRow>(new CommandDefinition(
            SelectOrderSql, new { ClOrdId = clOrdId }, cancellationToken: cancellationToken));
        if (storedOrder is null)
            return null;

        return new OrderOutcome(
            storedOrder.ClOrdId, storedOrder.OrderId, storedOrder.ExecId, storedOrder.Symbol, storedOrder.Side[0],
            storedOrder.Quantity, storedOrder.Price, storedOrder.Accepted, storedOrder.RejectReason, IsRepeat: true);
    }

    private static object ToOrderInsertParameters(OrderOutcome orderOutcome) => new
    {
        orderOutcome.ClOrdId,
        orderOutcome.OrderId,
        orderOutcome.ExecId,
        orderOutcome.Symbol,
        Side = orderOutcome.Side.ToString(),
        orderOutcome.Quantity,
        orderOutcome.Price,
        orderOutcome.Accepted,
        orderOutcome.RejectReason
    };

    // OrderID (tag 37) e ExecID (tag 17) nascem aqui, como UUID sem traços.
    private static string NewOrderOrExecId() => Guid.NewGuid().ToString("N");

    private sealed record StoredOrderRow(
        string ClOrdId,
        string OrderId,
        string ExecId,
        string? Symbol,
        string Side,
        decimal Quantity,
        decimal Price,
        bool Accepted,
        string? RejectReason);
}
