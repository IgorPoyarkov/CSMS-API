using Csms.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Csms.Api;

/// <summary>
/// Нарушение бизнес-правила — это 409 с текстом для человека, а не 500 и не
/// стектрейс. Сообщения домена написаны на русском специально: их показывает
/// интерфейс, а не только лог.
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainError)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Действие недопустимо в текущем состоянии пакета",
                Detail = domainError.Message,
            },
        });
    }
}
