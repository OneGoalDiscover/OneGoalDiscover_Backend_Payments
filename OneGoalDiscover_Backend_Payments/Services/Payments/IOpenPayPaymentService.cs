namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public record CreateChargeRequest(
    string PlanId,
    string PaymentMethod,   // "card" | "bank_account" | "store"
    string? TokenId,         // para tarjeta: token generado por openpay.js
    string CustomerName,
    string CustomerEmail,
    string CustomerPhone,
    string? Rfc,
    string? Description);

public record ChargeResult(
    string OrderId,
    string Status,
    string? PaymentBarcode,     // OXXO
    string? PaymentClabe,       // SPEI
    string? PaymentReference);

public interface IOpenPayPaymentService
{
    Task<ChargeResult> CreateChargeAsync(CreateChargeRequest request, CancellationToken ct = default);
}
