namespace OneGoalDiscover_Backend_Payments.Services.Plans;

public interface IPlansService
{
    Task<IEnumerable<PlanDTO>> GetActivePlansAsync();
}
