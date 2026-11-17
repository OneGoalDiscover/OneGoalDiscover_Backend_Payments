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
        catch (PaymentValidationException ex)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = (int)HttpStatusCode.BadRequest,
                Title = "Datos de pago no válidos",
                Detail = ex.Message
            });
        }
        catch (PaymentException ex)
        {
            // Regla de negocio no satisfecha (cargo rechazado, plan inexistente, intento anterior ya no vigente...): 422, no 409.
            context.Response.StatusCode = (int)HttpStatusCode.UnprocessableEntity;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = (int)HttpStatusCode.UnprocessableEntity,
                Title = "Pago no procesado",
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
