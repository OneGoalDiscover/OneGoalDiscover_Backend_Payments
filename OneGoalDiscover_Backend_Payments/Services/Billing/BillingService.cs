using Dapper;
using Microsoft.Data.SqlClient;
using OneGoalDiscover_Helpers.System;
using OneGoalDiscover_Models.DTOs.Payments;
using OneGoalDiscover_Utilities.System.Database.Interfaces;
using System.Data;

namespace OneGoalDiscover_Backend_Payments.Services.Billing;

public class BillingService(
    IDatabaseConnectionUtility db,
    IConfiguration configuration,
    ILogger<BillingService> logger) : IBillingService
{
    private readonly DatabaseHelper _dbHelper = new(db);

    public async Task<IEnumerable<BillingPaymentDTO>> GetPaymentsAsync(
        string cfg, short idCompany, BillingPaymentsFilterDTO filter, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);

        await using var conn = new SqlConnection(db.getConnectionStringBuilder(useConsolidationDatabase: true).ConnectionString);

        string instalacionId = await conn.ExecuteScalarAsync<string>(
            "SELECT TOP 1 CONVERT(VARCHAR(100), id_instalacion) FROM dsv.tDSV_SIS_Licencia") ?? string.Empty;

        if (string.IsNullOrEmpty(instalacionId))
        {
            logger.LogWarning("GetPayments: tDSV_SIS_Licencia sin id_instalacion (cfg={Cfg}, empresa={Company}).", cfg, idCompany);
            return [];
        }

        var p = new DynamicParameters();
        p.Add("@instalacion_id", instalacionId);
        p.Add("@fecha_desde",    filter.FechaDesde,  dbType: DbType.DateTime2);
        p.Add("@fecha_hasta",    filter.FechaHasta,  dbType: DbType.DateTime2);
        p.Add("@estatus",        filter.Estatus,     dbType: DbType.String);
        p.Add("@metodo_pago",    filter.MetodoPago,  dbType: DbType.String);

        return await conn.QueryAsync<BillingPaymentDTO>(
            "dsv.pDSV_LIC_PagoObtenerPorInstalacion",
            p,
            commandType: CommandType.StoredProcedure);
    }

    public async Task<BillingPaymentDetailDTO?> GetPaymentDetailAsync(
        string cfg, short idCompany, int idPago, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);

        await using var conn = new SqlConnection(db.getConnectionStringBuilder(useConsolidationDatabase: true).ConnectionString);

        var p = new DynamicParameters();
        p.Add("@id_pago", idPago, dbType: DbType.Int32);

        return await conn.QuerySingleOrDefaultAsync<BillingPaymentDetailDTO>(
            "dsv.pDSV_LIC_PagoObtenerDetalle",
            p,
            commandType: CommandType.StoredProcedure);
    }
}
