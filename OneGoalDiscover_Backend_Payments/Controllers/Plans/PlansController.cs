using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OneGoalDiscover_Backend_Payments.Services.Plans;

namespace OneGoalDiscover_Backend_Payments.Controllers.Plans;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PlansController(IPlansService plansService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetPlans()
    {
        var plans = await plansService.GetActivePlansAsync();
        return Ok(plans);
    }
}
