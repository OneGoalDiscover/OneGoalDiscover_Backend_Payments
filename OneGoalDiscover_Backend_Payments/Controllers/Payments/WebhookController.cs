using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OneGoalDiscover_Backend_Payments.Services.Payments;
using OneGoalDiscover_Models.DTOs.Payments;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneGoalDiscover_Backend_Payments.Controllers.Payments;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]   // OpenPay no envía JWT: se autentica con HTTP Basic (usuario/contraseña definidos al registrar el webhook en su dashboard)
public class WebhookController(
    IOpenPayPaymentService paymentService,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<WebhookController> logger) : ControllerBase
{
    private static readonly HashSet<string> ChargeEvents = ["charge.succeeded", "charge.failed", "charge.cancelled"];

    // Ajustes posteriores al cobro -> estatus que se registra en el pago. Solo se REGISTRAN: la licencia no se revoca de forma
    // automática (decisión de negocio pendiente) y queda un error en el log para revisión manual.
    // chargeback.rejected = la disputa se resolvió a favor del comercio: el pago vuelve a "completado".
    private static readonly Dictionary<string, string> AdjustmentEvents = new()
    {
        ["charge.refunded"]      = "reembolsado",
        ["chargeback.created"]   = "contracargo",
        ["chargeback.accepted"]  = "contracargo",
        ["chargeback.rejected"]  = "completado"
    };

    [HttpPost("openpay")]
    [EnableRateLimiting("webhook")]
    public async Task<IActionResult> OpenPayWebhook(CancellationToken ct)
    {
        if (!IsAuthorized())
        {
            logger.LogWarning("Webhook OpenPay: credenciales inválidas o ausentes desde {IP}.", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        using var reader  = new StreamReader(Request.Body);
        string    rawBody = await reader.ReadToEndAsync(ct);
        logger.LogDebug("Webhook OpenPay RAW: {Body}", rawBody);

        OpenPayWebhookDTO? evt;
        try
        {
            evt = JsonSerializer.Deserialize<OpenPayWebhookDTO>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            // Un cuerpo ilegible no mejora al reintentar: se registra y se responde 200 para no entrar en un ciclo de reintentos.
            logger.LogError(ex, "Webhook OpenPay: JSON inválido.");
            return Ok();
        }

        if (string.Equals(evt?.Type, "verification", StringComparison.OrdinalIgnoreCase))
        {
            // Al registrar el webhook OpenPay envía un código; hay que capturarlo del log y confirmarlo en su dashboard.
            logger.LogWarning("Webhook OpenPay: evento de verificación. Confirme el código en el dashboard de OpenPay: {Body}", rawBody);
            return Ok();
        }

        // OpenPay puede enviar tipos nuevos o repetidos: los que no gestionamos (o sin orden) se aceptan sin hacer nada
        if (evt is null || string.IsNullOrEmpty(evt.Transaction?.OrderId)
            || !(ChargeEvents.Contains(evt.Type) || AdjustmentEvents.ContainsKey(evt.Type)))
            return Ok();

        OpenPayTransactionDTO tx = evt.Transaction!;
        if (!OrderIdParser.TryParse(tx.OrderId, out string cfg, out string dbName))
        {
            logger.LogWarning("Webhook OpenPay: order_id con formato no reconocido ({OrderId}) — ignorado.", tx.OrderId);
            return Ok();
        }

        logger.LogInformation("Webhook OpenPay: type={Type} orderId={OrderId} cfg={Cfg} db={Db}", evt.Type, tx.OrderId, cfg, dbName);

        try
        {
            // Se actúa según lo que OpenPay confirma por API, no según lo que diga el payload.
            // En reembolsos y contracargos el estatus del cargo cambia de forma distinta: solo se confirma que el cargo es real
            // y coincide en orden y monto.
            var verification = await paymentService.VerifyChargeAsync(
                cfg, dbName, tx.OrderId, tx.Id, interpretStatus: !AdjustmentEvents.ContainsKey(evt.Type), ct: ct);

            if (!await ApplyVerifiedChargeAsync(evt.Type, cfg, dbName, tx, verification, ct))
            {
                logger.LogWarning("Webhook OpenPay: {Type} orderId={OrderId} no pudo aplicarse; se responde 500 para que OpenPay reintente.", evt.Type, tx.OrderId);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook OpenPay: error procesando evento {Type} orderId={OrderId}.", evt.Type, tx.OrderId);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        // 200 solo si el evento quedó procesado. Los reintentos son seguros: estatus y extensión de licencia son idempotentes.
        return Ok();
    }

    // false = falló algo transitorio y conviene que OpenPay reintente
    private async Task<bool> ApplyVerifiedChargeAsync(string eventType, string cfg, string dbName, OpenPayTransactionDTO tx, ChargeVerification verification, CancellationToken ct)
    {
        switch (verification.State)
        {
            case VerifiedChargeState.Matched:
                return await ApplyAdjustmentAsync(eventType, cfg, dbName, tx, ct);

            case VerifiedChargeState.Completed:
                // El estatus es idempotente; si extender la licencia corresponde lo decide el reclamo en BD.
                await paymentService.UpdateStatusAsync(cfg, dbName, tx.OrderId, "completado", tx.Id, ct: ct);
                return await paymentService.ExtendLicenseFromPaymentAsync(cfg, dbName, tx.OrderId, tx.Id, ct);

            case VerifiedChargeState.Failed:
                await paymentService.UpdateStatusAsync(cfg, dbName, tx.OrderId, "fallido", tx.Id, ct: ct);
                return true;

            case VerifiedChargeState.InProgress:
                logger.LogInformation("Webhook OpenPay: orderId={OrderId} aún en proceso en OpenPay; sin cambios.", tx.OrderId);
                return true;

            default:
                logger.LogError("Webhook OpenPay descartado: orderId={OrderId} chargeId={ChargeId}. {Reason}", tx.OrderId, tx.Id, verification.Reason);
                return true;
        }
    }

    private async Task<bool> ApplyAdjustmentAsync(string eventType, string cfg, string dbName, OpenPayTransactionDTO tx, CancellationToken ct)
    {
        string status  = AdjustmentEvents[eventType];
        bool   restore = status == "completado";

        bool updated = await paymentService.UpdateStatusAsync(cfg, dbName, tx.OrderId, status, tx.Id, restore: restore, ct: ct);

        if (restore)
            logger.LogInformation("Webhook OpenPay: contracargo resuelto a favor del comercio, orderId={OrderId} (actualizado={Updated}).", tx.OrderId, updated);
        else
            logger.LogError(
                "AJUSTE DE PAGO {Type}: orderId={OrderId} chargeId={ChargeId} pasó a '{Status}' (actualizado={Updated}). La licencia NO se revoca automáticamente: revisar manualmente.",
                eventType, tx.OrderId, tx.Id, status, updated);

        return true;
    }

    private bool IsAuthorized()
    {
        string expectedUser = configuration["OpenPay:WebhookUser"]     ?? string.Empty;
        string expectedPass = configuration["OpenPay:WebhookPassword"] ?? string.Empty;

        if (expectedUser.Length == 0 || expectedPass.Length == 0)
        {
            if (environment.IsDevelopment())
            {
                logger.LogWarning("Webhook OpenPay sin credenciales configuradas: se acepta solo porque el ambiente es Development.");
                return true;
            }

            logger.LogError("Webhook OpenPay rechazado: falta configurar OpenPay:WebhookUser y OpenPay:WebhookPassword.");
            return false;
        }

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        int separator = decoded.IndexOf(':');
        if (separator < 0)
            return false;

        // & (no &&) para que ambas comparaciones se evalúen siempre
        return FixedTimeEquals(expectedUser, decoded[..separator]) & FixedTimeEquals(expectedPass, decoded[(separator + 1)..]);
    }

    private static bool FixedTimeEquals(string expected, string received) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received));
}
