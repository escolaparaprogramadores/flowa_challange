using System.Text.Json.Serialization;

namespace Flowa.OrderAccumulator.Commons.Responses;

public sealed class DataMessage<T>
{
    public bool Success { get; private set; }
    public ResultStatus Status { get; private set; }
    public string Message { get; private set; }
    public T? Data { get; private set; }
    public IReadOnlyCollection<string> Errors { get; private set; }
    public string? ErrorCode { get; private set; }

    [JsonIgnore]
    public Exception? UnexpectedFailure { get; private set; }

    private DataMessage(
        bool success, ResultStatus status, string message, T? data, IReadOnlyCollection<string> errors, string? errorCode, Exception? unexpectedFailure)
    {
        Success = success;
        Status = status;
        Message = message;
        Data = data;
        Errors = errors;
        ErrorCode = errorCode;
        UnexpectedFailure = unexpectedFailure;
    }

    public static DataMessage<T> CreateSuccessMessage(T responseData, string message, ResultStatus status = ResultStatus.Ok) =>
        new(true, status, message, responseData, Array.Empty<string>(), null, null);

    public static DataMessage<T> CreateErrorMessage(string message, ResultStatus status = ResultStatus.InvalidInput, IReadOnlyCollection<string>? errors = null, string? errorCode = null) =>
        new(false, status, message, default, errors ?? Array.Empty<string>(), errorCode, null);

    public static DataMessage<T> CreateUnexpectedFailureMessage(Exception unexpectedFailure, string message, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(unexpectedFailure);
        return new(false, ResultStatus.InternalError, message, default, Array.Empty<string>(), errorCode, unexpectedFailure);
    }
}
