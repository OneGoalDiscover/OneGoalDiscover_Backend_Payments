using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Exceptions;
using System.Net;

namespace OneGoalDiscover_Backend_Payments.Middlewares;

public class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (PaymentException ex)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Conflict;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = (int)HttpStatusCode.Conflict,
                Title = "Error de pago",
                Detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error no controlado en Backend_Payments");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = (int)HttpStatusCode.InternalServerError,
                Title = "Error interno",
                Detail = "Ocurrió un error inesperado. Intente más tarde."
            });
        }
    }
}

public static class ErrorHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseErrorHandlingMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorHandlingMiddleware>();
}
