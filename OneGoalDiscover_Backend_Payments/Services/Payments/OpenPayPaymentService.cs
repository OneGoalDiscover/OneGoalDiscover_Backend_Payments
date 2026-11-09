using OneGoalDiscover_Backend_Payments.Exceptions;
using openpay = Openpay;

namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public class OpenPayPaymentService : IOpenPayPaymentService
{
    private readonly openpay.OpenpayAPI _api;

    public OpenPayPaymentService(IConfiguration configuration)
    {
        string merchantId = configuration["OpenPay:MerchantId"]
            ?? throw new InvalidOperationException("OpenPay:MerchantId no configurado.");
        string privateKey = configuration["OpenPay:PrivateKey"]
            ?? throw new InvalidOperationException("OpenPay:PrivateKey no configurado.");
        bool productionMode = configuration.GetValue<bool>("OpenPay:ProductionMode");

        _api = new openpay.OpenpayAPI(privateKey, merchantId, productionMode);
    }

    public Task<ChargeResult> CreateChargeAsync(CreateChargeRequest request, CancellationToken ct = default)
    {
        // TODO Fase 1: implementar flujo completo (card/SPEI/OXXO)
        // - Crear o recuperar customer en OpenPay
        // - Construir ChargeRequest según payment method
        // - Llamar _api.ChargeService.Create()
        // - Mapear respuesta a ChargeResult
        throw new PaymentException("Integración con OpenPay no implementada aún.");
    }
}
