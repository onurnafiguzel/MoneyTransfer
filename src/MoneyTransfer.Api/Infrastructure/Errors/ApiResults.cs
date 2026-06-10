namespace MoneyTransfer.Api.Infrastructure.Errors;

/// <summary>
/// Helpers that produce RFC 9457 ProblemDetails responses carrying a machine-readable "code" extension,
/// so clients can branch on a stable code instead of parsing prose.
/// </summary>
public static class ApiResults
{
    public static IResult Problem(int status, string code, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: status,
            title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult BadRequest(string code, string detail) =>
        Problem(StatusCodes.Status400BadRequest, code, detail);

    public static IResult UnprocessableEntity(string code, string detail) =>
        Problem(StatusCodes.Status422UnprocessableEntity, code, detail);

    public static IResult NotFound(string code, string detail) =>
        Problem(StatusCodes.Status404NotFound, code, detail);

    public static IResult Conflict(string code, string detail) =>
        Problem(StatusCodes.Status409Conflict, code, detail);

    /// <summary>503 for a transient, retryable condition (e.g. contention budget exhausted), with a Retry-After hint.</summary>
    public static IResult ServiceUnavailable(string code, string detail, int retryAfterSeconds = 1) =>
        new RetryableProblem(code, detail, retryAfterSeconds);

    // Wraps the ProblemDetails 503 to also emit a Retry-After header so clients (and proxies) back off correctly.
    private sealed class RetryableProblem(string code, string detail, int retryAfterSeconds) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
            await Problem(StatusCodes.Status503ServiceUnavailable, code, detail).ExecuteAsync(httpContext);
        }
    }
}
