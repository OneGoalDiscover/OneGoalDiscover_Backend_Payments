using OneGoalDiscover_Helpers.System;
using OneGoalDiscover_Utilities.System.Database.Interfaces;

namespace OneGoalDiscover_Backend_Payments.Middlewares;

public class PaymentsConnectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDatabaseConnectionUtility db, IConfiguration cfg)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Permite un CfgName global (raíz) compartido entre todos los backends,
            // con override por módulo en "Payments:CfgName" si se necesita una BD diferente.
            string cfgName = cfg["Payments:CfgName"] ?? cfg["CfgName"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cfgName))
                throw new InvalidOperationException(
                    "Falta 'CfgName' (o 'Payments:CfgName') en appsettings (nombre del ambiente CFG, ej. TABAR_DEV).");

            await new DatabaseHelper(db).configureConnections(cfg["CFGsPath"] ?? string.Empty, cfgName);
        }
        await next(context);
    }
}

public static class PaymentsConnectionMiddlewareExtensions
{
    public static IApplicationBuilder UsePaymentsConnectionMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<PaymentsConnectionMiddleware>();
}
