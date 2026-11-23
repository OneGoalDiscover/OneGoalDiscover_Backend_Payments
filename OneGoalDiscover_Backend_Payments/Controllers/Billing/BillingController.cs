using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Services.Billing;
using OneGoalDiscover_Helpers.Auth;
using OneGoalDiscover_Models.DTOs.Auth;
using OneGoalDiscover_Models.DTOs.Payments;

namespace OneGoalDiscover_Backend_Payments.Controllers.Billing;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BillingController(IBillingService billingService) : ControllerBase
{
    [HttpGet("payments")]
    public async Task<IActionResult> GetPayments([FromQuery] BillingPaymentsFilterDTO filter, CancellationToken ct)
    {
        var token = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var result = await billingService.GetPaymentsAsync(jwtInfo.cfg, jwtInfo.idCompany, filter, ct);
        return Ok(result);
    }

    [HttpGet("payments/{idPago:int}")]
    public async Task<IActionResult> GetPaymentDetail(int idPago, CancellationToken ct)
    {
        var token = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var result = await billingService.GetPaymentDetailAsync(jwtInfo.cfg, jwtInfo.idCompany, idPago, ct);
        return result is null ? NotFound() : Ok(result);
    }
}
