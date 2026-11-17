using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OneGoalDiscover_Backend_Payments.Services.Payments;
using OneGoalDiscover_Helpers.Auth;
using OneGoalDiscover_Models.DTOs.Auth;
using OneGoalDiscover_Models.DTOs.Payments;

namespace OneGoalDiscover_Backend_Payments.Controllers.Payments;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController(IOpenPayPaymentService paymentService) : ControllerBase
{
    /// <summary>
    /// Inicia un cobro. Para tarjeta se requiere el token generado por openpay.js en el frontend.
    /// La private key de OpenPay NUNCA llega al cliente: se usa solo en este backend.
    /// </summary>
    [HttpPost("charge")]
    [EnableRateLimiting("charge")]
    public async Task<IActionResult> CreateCharge([FromBody] CreateChargeRequestDTO request, CancellationToken ct)
    {
        var token    = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var result = await paymentService.CreateChargeAsync(jwtInfo.cfg, jwtInfo.idCompany, request, ct);
        return Ok(result);
    }

    // Devuelve MerchantId y PublicKey de OpenPay para que el frontend inicialice openpay.js.
    // No requiere autenticación: estas claves son públicas por diseño de OpenPay.
    [HttpGet("openpay-config")]
    [AllowAnonymous]
    public IActionResult GetOpenPayConfig()
    {
        var config = paymentService.GetPublicConfig();
        return Ok(config);
    }

    // Devuelve el último pago pendiente de la empresa autenticada.
    // La instalacion_id se deriva de tDSV_SIS_Licencia en el backend; no se pasa por URL.
    [HttpGet("status/pending")]
    [EnableRateLimiting("status")]
    public async Task<IActionResult> GetPendingPaymentStatus(CancellationToken ct)
    {
        var token    = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var result = await paymentService.GetLatestPaymentStatusAsync(jwtInfo.cfg, jwtInfo.idCompany, ct);
        if (result is null) return NoContent();
        return Ok(result);
    }

    // Estatus de un pago concreto de la empresa autenticada (cualquier estatus), para consultarlo periódicamente.
    // La orden debe tener el formato generado por este sistema y pertenecer al cfg del token.
    [HttpGet("status/{orderId}")]
    [EnableRateLimiting("status")]
    public async Task<IActionResult> GetPaymentStatus(string orderId, CancellationToken ct)
    {
        if (!OrderIdParser.TryParse(orderId, out string orderCfg, out _))
            return BadRequest("La orden de pago no es válida.");

        var token    = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        if (!string.Equals(orderCfg, jwtInfo.cfg, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var result = await paymentService.GetPaymentStatusAsync(jwtInfo.cfg, jwtInfo.idCompany, orderId, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
