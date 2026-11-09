using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Services.Infrastructure;

namespace OneGoalDiscover_Backend_Payments.Controllers.Payments;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]   // OpenPay no envía JWT; la autenticidad se verifica por firma HMAC
public class WebhookController(ILicensingClient licensingClient, ILogger<WebhookController> logger) : ControllerBase
{
    /// <summary>
    /// Receptor de eventos de OpenPay (charge.succeeded, etc.).
    /// TODO Fase 1: verificar firma HMAC del webhook antes de procesar.
    /// </summary>
    [HttpPost("openpay")]
    public async Task<IActionResult> OpenPayWebhook([FromBody] object payload, CancellationToken ct)
    {
        // TODO Fase 1:
        // 1. Validar firma HMAC (header verification_key de OpenPay)
        // 2. Deserializar evento
        // 3. Si event.type == "charge.succeeded" → extender licencia vía LicensingClient
        // 4. Registrar en lic_pagos con estatus = pagado

        logger.LogInformation("Webhook OpenPay recibido (pendiente implementación).");
        return Ok();
    }
}
