namespace OneGoalDiscover_Backend_Payments.Exceptions;

/// <summary>
/// Excepción de negocio del módulo de pagos. El mensaje es seguro para mostrar al usuario final.
/// </summary>
public class PaymentException(string message) : Exception(message);
