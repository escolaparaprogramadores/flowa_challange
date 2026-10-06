using Flowa.OrderGenerator.Commons.Http;
using Flowa.OrderGenerator.Commons.Responses;
using Flowa.OrderGenerator.Domain.Orders.Exceptions;

namespace Flowa.OrderGenerator.Application.ErrorHandling;

public static class UseCaseFailureDataMessageMapper
{
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";
    public const string UnexpectedErrorCode = "internal-error";
    public const string OrderAccumulatorUnavailableMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";
    public const string OrderAccumulatorUnavailableErrorCode = "order-accumulator-unavailable";

    public static bool WasCancelledByTheCaller(Exception useCaseFailure, CancellationToken callerCancellation) =>
        useCaseFailure is OperationCanceledException && callerCancellation.IsCancellationRequested;

    public static DataMessage<TData> MapFailureToDataMessage<TData>(Exception useCaseFailure)
    {
        ArgumentNullException.ThrowIfNull(useCaseFailure);

        return useCaseFailure switch
        {
            OrderNotAnsweredException orderNotAnswered => DataMessage<TData>.CreateFailureMessage(
                orderNotAnswered, OrderAccumulatorUnavailableMessage, ResultStatus.ServiceUnavailable, orderNotAnswered.ErrorCode, orderNotAnswered.ClOrdId),
            OrderFailureException orderFailure => DataMessage<TData>.CreateFailureMessage(
                orderFailure, UnexpectedErrorMessage, ResultStatus.InternalError, orderFailure.ErrorCode, orderFailure.ClOrdId),
            _ when useCaseFailure.IndicatesUnavailableHttpApi() => DataMessage<TData>.CreateFailureMessage(
                useCaseFailure, OrderAccumulatorUnavailableMessage, ResultStatus.ServiceUnavailable, OrderAccumulatorUnavailableErrorCode),
            _ => DataMessage<TData>.CreateFailureMessage(useCaseFailure, UnexpectedErrorMessage, ResultStatus.InternalError, UnexpectedErrorCode)
        };
    }
}
