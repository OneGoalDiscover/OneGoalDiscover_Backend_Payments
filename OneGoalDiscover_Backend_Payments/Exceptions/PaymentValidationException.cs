namespace OneGoalDiscover_Backend_Payments.Exceptions;

/// <summary>
/// Solicitud de cobro inválida por datos que el usuario puede corregir (cantidades, método, contacto, tarjeta).
/// Se responde 400 con el mensaje; el resto de <see cref="PaymentException"/> sigue respondiendo 409.
/// </summary>
public class PaymentValidationException(string message) : PaymentException(message);
