namespace OneGoalDiscover_Backend_Payments.Services.Infrastructure;

public interface ILicensingClient
{
    /// <summary>
    /// Extiende la licencia de una instalación en Backend_Licensing tras un pago confirmado.
    /// </summary>
    Task ExtendLicenseAsync(string installationId, int durationMonths, int empAdm, int empTotal, string paymentOrderId, CancellationToken ct = default);
}
