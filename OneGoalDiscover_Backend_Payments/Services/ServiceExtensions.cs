using OneGoalDiscover_Backend_Payments.Middlewares;
using OneGoalDiscover_Backend_Payments.Services.Infrastructure;
using OneGoalDiscover_Backend_Payments.Services.Payments;
using OneGoalDiscover_Backend_Payments.Services.Plans;

namespace OneGoalDiscover_Backend_Payments.Services;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IPlansService, PlansService>();
        services.AddScoped<IOpenPayPaymentService, OpenPayPaymentService>();

        services.AddHttpClient<ILicensingClient, LicensingClient>(client =>
        {
            string baseUrl = configuration["LicensingService:BaseUrl"]
                ?? throw new InvalidOperationException("LicensingService:BaseUrl no configurado.");
            client.BaseAddress = new Uri(baseUrl);
        });

        return services;
    }
}
