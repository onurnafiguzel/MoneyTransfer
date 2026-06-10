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
}
