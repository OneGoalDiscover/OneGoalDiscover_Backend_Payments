namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public enum VerifiedChargeState
{
    Completed,   // OpenPay confirma el cobro y coincide con el pago guardado
    Failed,      // OpenPay confirma que el cargo falló o fue cancelado/expiró
    InProgress,  // Aún sin resolver en OpenPay
    Matched,     // El cargo existe y su orden y monto coinciden; no se interpreta su estatus (reembolsos y contracargos)
    Invalid      // No existe, la orden o el monto no coinciden, o el estatus no se maneja: se descarta el evento
}

/// <summary>Resultado de contrastar un evento de webhook con el cargo real en OpenPay y con tDSV_LIC_Pagos.</summary>
public record ChargeVerification(VerifiedChargeState State, string? Reason = null);
