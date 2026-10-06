using Base.OrderAccumulator.Commons.Responses;

namespace Base.OrderAccumulator.Application.ErrorHandling;

public static class UseCaseFailureDataMessageMapper
{
    public const string UnexpectedErrorMessage = "Aconteceu um erro inesperado. Informe o traceId ao suporte.";
    public const string UnexpectedErrorCode = "internal-error";

    public static DataMessage<TResponseData> MapUseCaseFailureToDataMessage<TResponseData>(Exception useCaseFailure) =>
        DataMessage<TResponseData>.CreateUnexpectedFailureMessage(useCaseFailure, UnexpectedErrorMessage, UnexpectedErrorCode);
}
