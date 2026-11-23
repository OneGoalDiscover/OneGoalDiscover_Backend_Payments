using OneGoalDiscover_Models.DTOs.Payments;

namespace OneGoalDiscover_Backend_Payments.Services.Billing;

public interface IBillingService
{
    Task<IEnumerable<BillingPaymentDTO>> GetPaymentsAsync(string cfg, short idCompany, BillingPaymentsFilterDTO filter, CancellationToken ct = default);
    Task<BillingPaymentDetailDTO?> GetPaymentDetailAsync(string cfg, short idCompany, int idPago, CancellationToken ct = default);
}
