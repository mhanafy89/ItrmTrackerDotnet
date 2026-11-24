namespace ItrmTracker.Application.Common;

/// <summary>
/// A business-rule failure with an HTTP-style status code and one or more user-facing messages.
/// Controllers translate it into model-state errors (MVC) or a <c>{ errors: [...] }</c> body (API),
/// which mirrors the error contract of the original Node.js routes.
/// </summary>
public class ServiceException : Exception
{
    public ServiceException(int statusCode, IEnumerable<string> errors)
        : base(errors.FirstOrDefault() ?? "The operation failed.")
    {
        StatusCode = statusCode;
        Errors = errors.ToList();
    }

    public ServiceException(int statusCode, params string[] errors) : this(statusCode, (IEnumerable<string>)errors) { }

    public int StatusCode { get; }
    public IReadOnlyList<string> Errors { get; }

    public static ServiceException NotFound(string message = "Not found.") => new(404, message);
    public static ServiceException BadRequest(params string[] errors) => new(400, errors);
    public static ServiceException Conflict(params string[] errors) => new(409, errors);
}

/// <summary>Result of an operation that can partially succeed (bulk update, import commit, ...).</summary>
public sealed record OperationResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static OperationResult Ok() => new(true, Array.Empty<string>());
    public static OperationResult Fail(params string[] errors) => new(false, errors);
}
