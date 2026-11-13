namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public interface IHubsClient
{
    Task BroadcastLicenseUpdatedAsync(string cfg, CancellationToken ct = default);
}
