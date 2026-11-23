using OneGoalDiscover_Models.DTOs.Payments;

namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public interface IOpenPayPaymentService
{
    Task<CreateChargeResponseDTO> CreateChargeAsync(string cfg, short idCompany, CreateChargeRequestDTO request, CancellationToken ct = default);
    // interpretStatus = false: solo confirma que el cargo existe y coincide en orden y monto (reembolsos y contracargos)
    Task<ChargeVerification> VerifyChargeAsync(string cfg, string dbName, string orderId, string chargeId, bool interpretStatus = true, CancellationToken ct = default);
    // restore = true: devuelve a "completado" un pago en "contracargo" cuya disputa se ganó
    Task<bool> UpdateStatusAsync(string cfg, string dbName, string orderId, string estatus, string? chargeId = null, string? referencia = null, DateTime? fechaVencimiento = null, bool restore = false, CancellationToken ct = default);
    Task<(string InstalacionId, int EmpAdm, int EmpTotal, int DuracionMeses, int MesesGratis, string NombreBd)?> FindForExtendAsync(string cfg, string dbName, string orderId, CancellationToken ct = default);
    Task<OneGoalDiscover_Models.DTOs.Payments.PaymentStatusDTO?> GetLatestPaymentStatusAsync(string cfg, short idCompany, CancellationToken ct = default);
    Task<OneGoalDiscover_Models.DTOs.Payments.PaymentStatusDTO?> GetPaymentStatusAsync(string cfg, short idCompany, string orderId, CancellationToken ct = default);
    OneGoalDiscover_Models.DTOs.Payments.OpenPayPublicConfigDTO GetPublicConfig();
    Task<bool> ExtendLicenseFromPaymentAsync(string cfg, string dbName, string orderId, string chargeId, CancellationToken ct = default);
}
