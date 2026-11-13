using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public async Task<IActionResult> CreateCharge([FromBody] CreateChargeRequestDTO request, CancellationToken ct)
    {
        var token    = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var result = await paymentService.CreateChargeAsync(jwtInfo.cfg, jwtInfo.idCompany, request, ct);
        return Ok(result);
    }

    // Devuelve el último pago pendiente o completado de la instalación del usuario.
    // Usado por el wizard al montar el paso de confirmación para restaurar estado.
    [HttpGet("status/installation/{instalacionId}")]
    public async Task<IActionResult> GetInstallationPaymentStatus(string instalacionId, CancellationToken ct)
    {
        var token    = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var result = await paymentService.GetLatestPaymentStatusAsync(jwtInfo.cfg, jwtInfo.idCompany, instalacionId, ct);
        if (result is null) return NoContent();
        return Ok(result);
    }
}
