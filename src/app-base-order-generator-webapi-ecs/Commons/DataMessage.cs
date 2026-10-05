namespace Base.OrderGenerator.Commons;

// The one return of every use case (backend-datamessage.md). Over HTTP only the success leaves in this
// shape; an error leaves as problem+json, built by the Entrypoint from Message, Errors and ErrorCode.
public sealed class DataMessage<T>
{
    public bool Success { get; private set; }
    public ResultStatus Status { get; private set; }
    public string Message { get; private set; }
    public T? Data { get; private set; }
    public IReadOnlyCollection<string> Errors { get; private set; }
    public string? ErrorCode { get; private set; }

    private DataMessage(bool success, ResultStatus status, string message, T? data, IReadOnlyCollection<string> errors, string? errorCode)
    {
        Success = success;
        Status = status;
        Message = message;
        Data = data;
        Errors = errors;
        ErrorCode = errorCode;
    }

    public static DataMessage<T> CreateSuccessMessage(T data, string message, ResultStatus status = ResultStatus.Ok) =>
        new(true, status, message, data, Array.Empty<string>(), null);

    public static DataMessage<T> CreateErrorMessage(string message, ResultStatus status = ResultStatus.InvalidInput, IReadOnlyCollection<string>? errors = null, string? errorCode = null) =>
        new(false, status, message, default, errors ?? Array.Empty<string>(), errorCode);
}
