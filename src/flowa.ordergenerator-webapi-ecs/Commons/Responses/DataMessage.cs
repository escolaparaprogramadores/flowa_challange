using System.Text.Json.Serialization;

namespace Flowa.OrderGenerator.Commons.Responses;

public sealed class DataMessage<T>
{
    public bool Success { get; private set; }
    public ResultStatus Status { get; private set; }
    public string Message { get; private set; }
    public T? Data { get; private set; }
    public IReadOnlyCollection<string> Errors { get; private set; }
    public string? ErrorCode { get; private set; }
    [JsonIgnore]
    public Exception? Failure { get; private set; }
    [JsonIgnore]
    public string? FailureTraceId { get; private set; }

    private DataMessage(bool success, ResultStatus status, string message, T? data, IReadOnlyCollection<string> errors, string? errorCode,
        Exception? failure, string? failureTraceId)
    {
        Success = success;
        Status = status;
        Message = message;
        Data = data;
        Errors = errors;
        ErrorCode = errorCode;
        Failure = failure;
        FailureTraceId = failureTraceId;
    }

    public static DataMessage<T> CreateSuccessMessage(T data, string message, ResultStatus status = ResultStatus.Ok) =>
        new(true, status, message, data, Array.Empty<string>(), null, null, null);

    public static DataMessage<T> CreateErrorMessage(string message, ResultStatus status = ResultStatus.InvalidInput, IReadOnlyCollection<string>? errors = null, string? errorCode = null) =>
        new(false, status, message, default, errors ?? Array.Empty<string>(), errorCode, null, null);

    public static DataMessage<T> CreateFailureMessage(Exception failure, string message, ResultStatus status, string errorCode, string? failureTraceId = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(false, status, message, default, Array.Empty<string>(), errorCode, failure, failureTraceId);
    }
}
