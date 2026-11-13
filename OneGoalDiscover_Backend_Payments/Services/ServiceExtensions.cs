using OneGoalDiscover_Backend_Payments.Middlewares;
using OneGoalDiscover_Backend_Payments.Services.Infrastructure;
using OneGoalDiscover_Backend_Payments.Services.Payments;
using OneGoalDiscover_Backend_Payments.Services.Plans;
using OneGoalDiscover_Utilities.System.Database;
using OneGoalDiscover_Utilities.System.Database.Interfaces;

namespace OneGoalDiscover_Backend_Payments.Services;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IDatabaseConnectionUtility, DatabaseConnectionUtility>();

        services.AddScoped<IPlansService, PlansService>();
        services.AddScoped<IOpenPayPaymentService, OpenPayPaymentService>();

        services.AddHttpClient<ILicensingClient, LicensingClient>(client =>
        {
            string baseUrl = configuration["LicensingService:BaseUrl"]
                ?? throw new InvalidOperationException("LicensingService:BaseUrl no configurado.");
            client.BaseAddress = new Uri(baseUrl);
        });

        services.AddHttpClient<IHubsClient, HubsClient>(client =>
        {
            string baseUrl = configuration["HubsService:BaseUrl"]
                ?? throw new InvalidOperationException("HubsService:BaseUrl no configurado.");
            client.BaseAddress = new Uri(baseUrl);
        });

        return services;
    }

    public static IApplicationBuilder UseErrorHandlingMiddleware(this IApplicationBuilder app)
        => app.UseMiddleware<ErrorHandlingMiddleware>();
}
