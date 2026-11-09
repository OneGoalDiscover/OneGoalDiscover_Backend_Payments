namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public interface ILicensingClient
{
    /// <summary>
    /// Extiende la licencia de una instalación en Backend_Licensing.
    /// </summary>
    Task ExtendLicenseAsync(string installationId, int days, string paymentReference, CancellationToken ct = default);
}
