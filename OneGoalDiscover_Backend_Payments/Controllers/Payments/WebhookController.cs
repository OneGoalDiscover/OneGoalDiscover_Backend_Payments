using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Services.Infrastructure;
using OneGoalDiscover_Backend_Payments.Services.Payments;
using OneGoalDiscover_Backend_Payments.Services;
using OneGoalDiscover_Models.DTOs.Payments;
using System.Text.Json;

namespace OneGoalDiscover_Backend_Payments.Controllers.Payments;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]   // OpenPay no envía JWT; autenticidad verificada por token del dashboard
public class WebhookController(
    IOpenPayPaymentService paymentService,
    ILicensingClient licensingClient,
    IHubsClient hubsClient,
    IConfiguration configuration,
    ILogger<WebhookController> logger) : ControllerBase
{
    [HttpPost("openpay")]
    public async Task<IActionResult> OpenPayWebhook(CancellationToken ct)
    {
        using var reader  = new StreamReader(Request.Body);
        string    rawBody = await reader.ReadToEndAsync(ct);

        logger.LogInformation("Webhook OpenPay RAW: {Body}", rawBody);

        string expectedToken = configuration["OpenPay:WebhookVerificationToken"] ?? string.Empty;
        if (!string.IsNullOrEmpty(expectedToken))
        {
            string? received = Request.Headers["Openpay-Verification-Token"].FirstOrDefault();
            if (received != expectedToken)
            {
                logger.LogWarning("Webhook OpenPay: token de verificación inválido desde {IP}.",
                    HttpContext.Connection.RemoteIpAddress);
                return Unauthorized();
            }
        }

        OpenPayWebhookDTO? evt;
        try
        {
            evt = JsonSerializer.Deserialize<OpenPayWebhookDTO>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook OpenPay: JSON inválido.");
            return BadRequest();
        }

        if (evt?.Transaction?.OrderId is null or "")
        {
            logger.LogWarning("Webhook OpenPay: evento sin transaction.order_id — ignorado.");
            return Ok();
        }

        // orderId = "{cfg}-{dbName}-OGD{timestamp}{hex}"
        string orderId = evt.Transaction.OrderId;
        int    oGDIdx  = orderId.IndexOf("-OGD", StringComparison.Ordinal);
        string prefix  = oGDIdx > 0 ? orderId[..oGDIdx] : orderId;
        int    dashIdx = prefix.IndexOf('-');
        string cfg     = dashIdx >= 0 ? prefix[..dashIdx]       : prefix;
        string dbName  = dashIdx >= 0 ? prefix[(dashIdx + 1)..] : string.Empty;

        logger.LogInformation("Webhook OpenPay: type={Type} orderId={OrderId} cfg={Cfg} db={Db}",
            evt.Type, orderId, cfg, dbName);

        try
        {
            switch (evt.Type)
            {
                case "charge.succeeded":
                    await HandleChargeSucceededAsync(cfg, dbName, evt.Transaction, ct);
                    break;

                case "charge.failed":
                case "charge.cancelled":
                    await paymentService.UpdateStatusAsync(cfg, dbName, orderId, "fallido", evt.Transaction.Id, ct: ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook OpenPay: error procesando evento {Type} orderId={OrderId}.", evt.Type, orderId);
        }

        // Siempre 200: evita que OpenPay reintente infinitamente ante errores transitorios
        return Ok();
    }

    private async Task HandleChargeSucceededAsync(string cfg, string dbName, OpenPayTransactionDTO tx, CancellationToken ct)
    {
        await paymentService.UpdateStatusAsync(cfg, dbName, tx.OrderId, "completado", tx.Id, ct: ct);

        var info = await paymentService.FindForExtendAsync(cfg, dbName, tx.OrderId, ct);
        if (info is null)
        {
            logger.LogWarning("Webhook: pago no encontrado en BD para orderId={OrderId}.", tx.OrderId);
            return;
        }

        int duracionTotal = info.Value.DuracionMeses + info.Value.MesesGratis;

        try
        {
            await licensingClient.ExtendLicenseAsync(
                info.Value.InstalacionId,
                duracionTotal,
                info.Value.EmpAdm,
                info.Value.EmpTotal,
                tx.OrderId,
                ct);

            logger.LogInformation(
                "Licencia extendida: instalacion={Id} meses={Meses} bd={Bd}",
                info.Value.InstalacionId, duracionTotal, info.Value.NombreBd);

            await hubsClient.BroadcastLicenseUpdatedAsync(cfg, ct);
        }
        catch (Exception ex)
        {
            // Log pero no fallar el webhook — el pago ya se confirmó; la extensión se puede reintentar
            logger.LogError(ex, "Webhook: error al extender licencia para instalación {Id}.", info.Value.InstalacionId);
        }
    }
}
