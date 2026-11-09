namespace OneGoalDiscover_Backend_Payments.Services.Plans;

public record PlanDTO(int Id, string Name, string Description, decimal Price, int DurationDays, bool IsActive);

public class PlansService : IPlansService
{
    // TODO Fase 0: leer desde tabla lic_planes via Dapper
    public Task<IEnumerable<PlanDTO>> GetActivePlansAsync()
        => Task.FromResult<IEnumerable<PlanDTO>>([]);
}
