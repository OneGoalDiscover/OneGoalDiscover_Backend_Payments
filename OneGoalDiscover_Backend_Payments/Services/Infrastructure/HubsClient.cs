namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public class HubsClient(HttpClient httpClient, IConfiguration configuration, ILogger<HubsClient> logger) : IHubsClient
{
    public async Task BroadcastLicenseUpdatedAsync(string cfg, CancellationToken ct = default)
    {
        string internalKey = configuration["InternalKeys:HubsService"] ?? string.Empty;
        httpClient.DefaultRequestHeaders.Remove("X-Internal-Key");
        if (!string.IsNullOrEmpty(internalKey))
            httpClient.DefaultRequestHeaders.Add("X-Internal-Key", internalKey);

        // Best-effort: si falla no propagamos (el pago ya fue confirmado), pero SÍ se deja constancia: un aviso rechazado
        // (por ejemplo llave interna distinta) deja a las sesiones abiertas sin enterarse de la licencia renovada.
        try
        {
            using var response = await httpClient.PostAsync(
                $"api/notification/internal-broadcast-license-updated?cfg={Uri.EscapeDataString(cfg)}",
                null, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning(
                    "Hubs rechazó el aviso de licencia actualizada (cfg={Cfg}): HTTP {Status}. Verifique que InternalKeys:HubsService de Payments coincida con InternalKeys:Hangfire de Hubs.",
                    cfg, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo avisar a Hubs de la licencia actualizada (cfg={Cfg}).", cfg);
        }
    }
}
