using Dapper;
using Microsoft.Data.SqlClient;
using OneGoalDiscover_Helpers.System;
using OneGoalDiscover_Models.DTOs.Payments;
using OneGoalDiscover_Utilities.System.Database.Interfaces;
using System.Data;

namespace OneGoalDiscover_Backend_Payments.Services.Plans;

public class PlansService(IDatabaseConnectionUtility db, IConfiguration configuration) : IPlansService
{
    private readonly DatabaseHelper _dbHelper = new(db);

    public async Task<List<PlanDTO>> GetActivePlansAsync(string cfg, short idCompany, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);

        await using var conn = new SqlConnection(db.getConnectionStringBuilder().ConnectionString);

        using var multi = await conn.QueryMultipleAsync(
            "pDSV_LIC_PlanListar", commandType: CommandType.StoredProcedure);

        var planRows   = (await multi.ReadAsync<PlanRow>()).ToList();
        var tarifaRows = (await multi.ReadAsync<TarifaRow>()).ToList();
        var promoRows  = (await multi.ReadAsync<PromoRow>()).ToList();

        return planRows.Select(p => new PlanDTO
        {
            IdPlan        = p.id_plan,
            Nombre        = p.nombre,
            Descripcion   = p.descripcion ?? string.Empty,
            DuracionMeses = p.duracion_meses,
            RibbonTexto   = p.ribbon_texto,
            RibbonColor   = p.ribbon_color,
            FeaturesJson  = p.features_json,
            OrdenDisplay  = p.orden_display,
            Tarifas = tarifaRows
                .Where(t => t.id_plan == p.id_plan)
                .Select(t => new TarifaPlanDTO
                {
                    IdTarifa      = t.id_tarifa,
                    MinEmp        = t.min_emp,
                    MaxEmp        = t.max_emp,
                    PrecioAdmMes  = t.precio_adm_mes,
                    PrecioEmpMes  = t.precio_emp_mes
                }).ToList(),
            Promocion = promoRows.FirstOrDefault(pr => pr.id_plan == p.id_plan) is { } pr
                ? new PromocionDTO
                {
                    IdPromo      = pr.id_promo,
                    Etiqueta     = pr.etiqueta,
                    MesesGratis  = pr.meses_gratis,
                    PctDescuento = pr.pct_descuento,
                    VigenciaIni  = pr.vigencia_ini,
                    VigenciaFin  = pr.vigencia_fin
                }
                : null
        }).ToList();
    }

    private class PlanRow
    {
        public int      id_plan        { get; set; }
        public string   nombre         { get; set; } = string.Empty;
        public string?  descripcion    { get; set; }
        public int      duracion_meses { get; set; }
        public string?  ribbon_texto   { get; set; }
        public string?  ribbon_color   { get; set; }
        public string?  features_json  { get; set; }
        public int      orden_display  { get; set; }
    }

    private class TarifaRow
    {
        public int      id_tarifa      { get; set; }
        public int      id_plan        { get; set; }
        public int      min_emp        { get; set; }
        public int?     max_emp        { get; set; }
        public decimal  precio_adm_mes { get; set; }
        public decimal  precio_emp_mes { get; set; }
    }

    private class PromoRow
    {
        public int      id_promo       { get; set; }
        public int      id_plan        { get; set; }
        public string   etiqueta       { get; set; } = string.Empty;
        public int      meses_gratis   { get; set; }
        public decimal  pct_descuento  { get; set; }
        public DateTime vigencia_ini   { get; set; }
        public DateTime vigencia_fin   { get; set; }
    }
}
