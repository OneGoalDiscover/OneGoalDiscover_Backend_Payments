using OneGoalDiscover_Models.DTOs.Payments;

namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public interface IOpenPayPaymentService
{
    Task<CreateChargeResponseDTO> CreateChargeAsync(string cfg, short idCompany, CreateChargeRequestDTO request, CancellationToken ct = default);
    Task UpdateStatusAsync(string cfg, string dbName, string orderId, string estatus, string? chargeId = null, string? referencia = null, CancellationToken ct = default);
    Task<(string InstalacionId, int EmpAdm, int EmpTotal, int DuracionMeses, int MesesGratis, string NombreBd)?> FindForExtendAsync(string cfg, string dbName, string orderId, CancellationToken ct = default);
    Task<OneGoalDiscover_Models.DTOs.Payments.PaymentStatusDTO?> GetLatestPaymentStatusAsync(string cfg, short idCompany, string instalacionId, CancellationToken ct = default);
}
