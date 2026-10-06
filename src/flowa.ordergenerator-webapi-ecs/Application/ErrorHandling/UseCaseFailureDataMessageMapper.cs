using Flowa.Commons.Responses;
using Flowa.OrderGenerator.Domain.Orders.Exceptions;

namespace Flowa.OrderGenerator.Application.ErrorHandling;

public static class UseCaseFailureDataMessageMapper
{
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";
    public const string UnexpectedErrorCode = "internal-error";
    public const string OrderAccumulatorUnavailableMessage = "Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.";

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
            _ => DataMessage<TData>.CreateFailureMessage(useCaseFailure, UnexpectedErrorMessage, ResultStatus.InternalError, UnexpectedErrorCode)
        };
    }
}
