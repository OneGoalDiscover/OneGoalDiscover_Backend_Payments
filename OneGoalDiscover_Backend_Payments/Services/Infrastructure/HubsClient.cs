namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public class HubsClient(HttpClient httpClient, IConfiguration configuration) : IHubsClient
{
    public async Task BroadcastLicenseUpdatedAsync(string cfg, CancellationToken ct = default)
    {
        string internalKey = configuration["InternalKeys:HubsService"] ?? string.Empty;
        httpClient.DefaultRequestHeaders.Remove("X-Internal-Key");
        if (!string.IsNullOrEmpty(internalKey))
            httpClient.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);

        // Best-effort: si falla no propagamos — el pago ya fue confirmado
        try
        {
            await httpClient.PostAsync(
                $"api/notification/internal-broadcast-license-updated?cfg={Uri.EscapeDataString(cfg)}",
                null, ct);
        }
        catch { }
    }
}
