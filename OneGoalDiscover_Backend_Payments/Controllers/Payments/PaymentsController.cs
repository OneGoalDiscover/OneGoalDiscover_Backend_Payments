using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Services.Payments;

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
    public async Task<IActionResult> CreateCharge([FromBody] CreateChargeRequest request, CancellationToken ct)
    {
        var result = await paymentService.CreateChargeAsync(request, ct);
        return Ok(result);
    }
}
