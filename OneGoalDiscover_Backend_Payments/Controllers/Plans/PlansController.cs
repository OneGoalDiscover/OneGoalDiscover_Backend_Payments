using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Services.Plans;
using OneGoalDiscover_Helpers.Auth;
using OneGoalDiscover_Models.DTOs.Auth;

namespace OneGoalDiscover_Backend_Payments.Controllers.Plans;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PlansController(IPlansService plansService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetPlans()
    {
        var token    = Request.Headers["Authorization"].ToString().Replace("Bearer ", "");
        JwtInformationTokenDTO jwtInfo = JwtToken.jwtInformation(token);

        var plans = await plansService.GetActivePlansAsync(jwtInfo.cfg, jwtInfo.idCompany);
        return Ok(plans);
    }
}
