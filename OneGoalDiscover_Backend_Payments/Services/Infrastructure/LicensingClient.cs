using OneGoalDiscover_Backend_Payments.Exceptions;
using System.Net.Http.Json;

namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public class LicensingClient(HttpClient httpClient, IConfiguration configuration) : ILicensingClient
{
    public async Task ExtendLicenseAsync(string installationId, int days, string paymentReference, CancellationToken ct = default)
    {
        string internalKey = configuration["InternalKeys:LicensingService"] ?? string.Empty;
        httpClient.DefaultRequestHeaders.Remove("X-Internal-Key");
        if (!string.IsNullOrEmpty(internalKey))
            httpClient.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);

        var response = await httpClient.PostAsJsonAsync(
            $"api/licenses/{installationId}/extend",
            new { Days = days, PaymentReference = paymentReference },
            ct);

        if (!response.IsSuccessStatusCode)
            throw new PaymentException("No fue posible extender la licencia. Contacte al soporte.");
    }
}
