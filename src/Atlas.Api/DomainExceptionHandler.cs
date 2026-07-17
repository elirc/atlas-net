using Atlas.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api;

/// <summary>
/// Safety net: any DomainException that escapes an endpoint becomes a 409
/// ProblemDetails instead of a 500. Other exceptions fall through to the
/// default ProblemDetails handler.
/// </summary>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Domain rule violated",
                Detail = domainException.Message,
            },
            cancellationToken);
        return true;
    }
}
