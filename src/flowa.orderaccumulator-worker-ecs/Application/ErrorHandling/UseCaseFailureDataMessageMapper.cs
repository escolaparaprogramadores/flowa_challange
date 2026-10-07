using Flowa.Commons.Responses;

namespace Flowa.OrderAccumulator.Application.ErrorHandling;

public static class UseCaseFailureDataMessageMapper
{
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";
    public const string UnexpectedErrorCode = "internal-error";

    public static DataMessage<TResponseData> MapUseCaseFailureToDataMessage<TResponseData>(Exception useCaseFailure) =>
        DataMessage<TResponseData>.CreateFailureMessage(useCaseFailure, UnexpectedErrorMessage, ResultStatus.InternalError, UnexpectedErrorCode);
}
