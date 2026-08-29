namespace NCash.Domain.Common;

public class Result<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public int StatusCode { get; }

    protected Result(bool isSuccess, T? value, string? errorCode, string? errorMessage, int statusCode = 200)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        StatusCode = statusCode;
    }

    public static Result<T> Success(T value, int statusCode = 200) =>
        new(true, value, null, null, statusCode);

    public static Result<T> Failure(string errorCode, string errorMessage, int statusCode = 400) =>
        new(false, default, errorCode, errorMessage, statusCode);
}

public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public int StatusCode { get; }

    protected Result(bool isSuccess, string? errorCode, string? errorMessage, int statusCode = 200)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        StatusCode = statusCode;
    }

    public static Result Success(int statusCode = 200) =>
        new(true, null, null, statusCode);

    public static Result Failure(string errorCode, string errorMessage, int statusCode = 400) =>
        new(false, errorCode, errorMessage, statusCode);
}
