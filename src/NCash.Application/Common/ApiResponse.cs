namespace NCash.Application.Common;

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
    public ApiError? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse<T> Ok(T data, string message = "Operation succeeded") => new()
    {
        Success = true,
        Message = message,
        Data = data
    };

    public static ApiResponse<T> Fail(string code, string message, int statusCode = 400) => new()
    {
        Success = false,
        Error = new ApiError { Code = code, Message = message, StatusCode = statusCode }
    };
}

public class ApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ApiError? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static ApiResponse Ok(string message = "Operation succeeded") => new()
    {
        Success = true,
        Message = message
    };

    public static ApiResponse Fail(string code, string message, int statusCode = 400) => new()
    {
        Success = false,
        Error = new ApiError { Code = code, Message = message, StatusCode = statusCode }
    };
}

public class ApiError
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public Dictionary<string, string[]>? ValidationErrors { get; set; }
}
