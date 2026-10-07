using Flowa.OrderGenerator.Application.ErrorHandling;
using Flowa.OrderGenerator.Application.Orders.Commands;
using Flowa.OrderGenerator.Application.Orders.Interfaces;
using Flowa.OrderGenerator.Application.Orders.Responses;
using Flowa.Commons.Logging;
using Flowa.Commons.Observability;
using Flowa.Commons.Responses;
using Flowa.OrderGenerator.Domain.Orders.ValueObjects;

namespace Flowa.OrderGenerator.Application.Orders.UseCases;

public sealed class SendOrderUseCase
{
    public const string OperationName = "orders.send-order";
    public const string AcceptedOrderResult = "accepted";
    public const string RejectedOrderResult = "rejected";
    public const string InvalidOrderMessage = "A ordem tem campos inválidos.";
    public const string InvalidOrderErrorCode = "invalid-order";

    private readonly IOrderAccumulatorPort _orderAccumulatorPort;
    private readonly IOperationMonitoring _operationMonitoring;
    private readonly IApplicationLogger<SendOrderUseCase> _logger;

    public SendOrderUseCase(IOrderAccumulatorPort orderAccumulatorPort, IOperationMonitoring operationMonitoring, IApplicationLogger<SendOrderUseCase> logger)
    {
        _orderAccumulatorPort = orderAccumulatorPort ?? throw new ArgumentNullException(nameof(orderAccumulatorPort));
        _operationMonitoring = operationMonitoring ?? throw new ArgumentNullException(nameof(operationMonitoring));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataMessage<SentOrderResponse>> SendOrderAsync(SendOrderCommand sendOrderCommand)
    {
        ArgumentNullException.ThrowIfNull(sendOrderCommand);

        using var orderSending = _operationMonitoring.StartOperationMonitoring(OperationName);
        try
        {
            var orderFormatValidation = OrderToSend.ValidateOrderFormat(
                sendOrderCommand.Symbol, sendOrderCommand.Side, sendOrderCommand.Quantity, sendOrderCommand.Price);
            if (orderFormatValidation.OrderToSend is not { } orderToSend)
            {
                orderSending.RecordOperationResult(OperationResults.InvalidInput);
                return DataMessage<SentOrderResponse>.CreateErrorMessage(
                    InvalidOrderMessage, ResultStatus.InvalidInput, orderFormatValidation.ListOrderFieldFormatMessages(), InvalidOrderErrorCode);
            }

            var sentOrderResult = await _orderAccumulatorPort.SendOrderAsync(orderToSend);
            sentOrderResult.ConfirmOrderWasAnswered();

            if (sentOrderResult.IsAccepted)
            {
                orderSending.RecordOperationResult(AcceptedOrderResult);
                _logger.LogInformation("Order accepted by the OrderAccumulator.", new
                {
                    sentOrderResult.ClOrdId,
                    sentOrderResult.OrderId,
                    orderToSend.Symbol,
                    Side = orderToSend.DescribeOrderSideCode()
                });
            }
            else
                orderSending.RecordOperationResult(RejectedOrderResult);

            return DataMessage<SentOrderResponse>.CreateSuccessMessage(
                SentOrderResponse.MapFromSentOrder(sentOrderResult, orderToSend), sentOrderResult.DescribeOrderAnswer());
        }
        catch (Exception orderSendingFailure)
        {
            orderSending.RecordOperationResult(OperationResults.Failed);
            return UseCaseFailureDataMessageMapper.MapFailureToDataMessage<SentOrderResponse>(orderSendingFailure);
        }
    }
}
