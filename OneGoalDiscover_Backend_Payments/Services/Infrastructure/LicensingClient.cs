using Microsoft.Extensions.Logging;
using OneGoalDiscover_Backend_Payments.Exceptions;
using System.Net.Http.Json;

namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public class LicensingClient(HttpClient httpClient, IConfiguration configuration, ILogger<LicensingClient> logger) : ILicensingClient
{
    public async Task ExtendLicenseAsync(string installationId, int durationMonths, int empAdm, int empTotal, string paymentOrderId, CancellationToken ct = default)
    {
        string internalKey = configuration["InternalKeys:LicensingService"] ?? string.Empty;
        httpClient.DefaultRequestHeaders.Remove("X-Internal-Key");
        if (!string.IsNullOrEmpty(internalKey))
            httpClient.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);

        var response = await httpClient.PostAsJsonAsync(
            $"api/licenses/{installationId}/extend",
            new { DurationMonths = durationMonths, EmpAdm = empAdm, EmpTotal = empTotal, PaymentOrderId = paymentOrderId },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError(
                "LicensingClient: extend falló. installationId={Id} status={Status} body={Body}",
                installationId, (int)response.StatusCode, body);
            throw new PaymentException("No fue posible extender la licencia. Contacte al soporte.");
        }
    }
}
