using OneGoalDiscover_Models.DTOs.Payments;

namespace OneGoalDiscover_Backend_Payments.Services.Plans;

public interface IPlansService
{
    Task<List<PlanDTO>> GetActivePlansAsync(string cfg, short idCompany, CancellationToken ct = default);
}
