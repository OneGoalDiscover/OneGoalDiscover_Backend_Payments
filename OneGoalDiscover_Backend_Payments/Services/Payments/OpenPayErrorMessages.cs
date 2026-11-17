namespace OneGoalDiscover_Backend_Payments.Services.Payments;

/// <summary>
/// Traduce los códigos de error de OpenPay a mensajes en español aptos para el usuario final.
/// El texto original de OpenPay (en inglés y con detalles técnicos) solo se registra en el log.
/// Códigos según la documentación de OpenPay (errores de cargo 1xxx, almacenamiento 2xxx y tarjeta 3xxx).
/// </summary>
public static class OpenPayErrorMessages
{
    private const string DeclinedGeneric = "No se pudo procesar el pago con esta tarjeta. Intenta con otra tarjeta o con otro método de pago.";
    private const string ContactBank     = "Tu banco requiere que lo contactes para autorizar este pago. También puedes usar otro método de pago.";

    public static string ToUserMessage(int errorCode, int httpStatus) => errorCode switch
    {
        3001 => "Tu banco declinó la tarjeta. Intenta con otra tarjeta o contacta a tu banco.",
        3002 or 2005 => "La tarjeta ha expirado. Usa otra tarjeta.",
        3003 => "La tarjeta no tiene fondos suficientes. Usa otra tarjeta o elige otro método de pago.",
        // Robada / extraviada / rechazada por el emisor: sin dar detalles sensibles
        3004 or 3009 or 3007 => "Tu banco no autorizó esta tarjeta. Usa otro método de pago o contacta a tu banco.",
        3005 => "El pago fue rechazado por el sistema de prevención de fraude. Intenta con otro método de pago.",
        3006 or 3008 or 1013 => "La operación no está permitida para esta tarjeta (por ejemplo, compras en línea no habilitadas). Usa otra tarjeta o contacta a tu banco.",
        3010 or 3011 or 3012 => ContactBank,
        2004 => "El número de tarjeta no es válido. Revísalo e inténtalo de nuevo.",
        2006 or 2009 => "El código de seguridad (CVV) no es válido. Revísalo e inténtalo de nuevo.",
        2010 => "La autenticación bancaria (3D Secure) no se completó. Inténtalo de nuevo.",
        1012 => "El monto está fuera de los límites permitidos para este método de pago.",
        1018 => "Se hicieron demasiados intentos de cobro. Espera unos minutos antes de intentarlo de nuevo.",
        1006 => "Ya existe un pago con esta referencia. Actualiza la pantalla e inténtalo de nuevo.",
        // Cualquier otro rechazo de tarjeta (HTTP 402) o de otro tipo
        _ when httpStatus == 402 => DeclinedGeneric,
        _ => "No se pudo procesar el pago. Inténtalo de nuevo en unos minutos."
    };
}
