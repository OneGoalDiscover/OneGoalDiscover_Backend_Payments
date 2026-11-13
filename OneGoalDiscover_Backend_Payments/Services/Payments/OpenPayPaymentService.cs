using Dapper;
using Microsoft.Data.SqlClient;
using OneGoalDiscover_Backend_Payments.Exceptions;
using OneGoalDiscover_Backend_Payments.Services.Plans;
using OneGoalDiscover_Helpers.System;
using OneGoalDiscover_Models.DTOs.Payments;
using OneGoalDiscover_Utilities.System.Database.Interfaces;
using System.Data;
using System.Text.Json;
using OpenPaySDK = Openpay;

namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public class OpenPayPaymentService(
    IDatabaseConnectionUtility db,
    IPlansService plansService,
    IConfiguration configuration) : IOpenPayPaymentService
{
    private readonly DatabaseHelper    _dbHelper = new(db);
    private readonly OpenPaySDK.OpenpayAPI _api  = CreateApi(configuration);

    private static OpenPaySDK.OpenpayAPI CreateApi(IConfiguration cfg)
    {
        string merchantId = cfg["OpenPay:MerchantId"]  ?? throw new InvalidOperationException("OpenPay:MerchantId no configurado.");
        string privateKey = cfg["OpenPay:PrivateKey"]  ?? throw new InvalidOperationException("OpenPay:PrivateKey no configurado.");
        bool   production = cfg.GetValue<bool>("OpenPay:ProductionMode");
        var    api        = new OpenPaySDK.OpenpayAPI(privateKey, merchantId);
        api.Production    = production;
        return api;
    }

    // Conexión via JWT (cfg + idCompany) — endpoints normales
    public async Task<CreateChargeResponseDTO> CreateChargeAsync(string cfg, short idCompany, CreateChargeRequestDTO request, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);

        var plans  = await plansService.GetActivePlansAsync(cfg, idCompany, ct);
        var plan   = plans.FirstOrDefault(p => p.IdPlan == request.IdPlan)
                     ?? throw new PaymentException($"Plan {request.IdPlan} no encontrado o inactivo.");

        var tarifa = plan.Tarifas.FirstOrDefault(
                         t => t.MinEmp <= request.EmpTotal && (t.MaxEmp == null || t.MaxEmp >= request.EmpTotal));

        if (tarifa is null || tarifa.MaxEmp is null)
            throw new PaymentException("El número de empleados supera el límite permitido para contratar en línea. Contacte a ventas.");

        var promo = (request.IdPromo.HasValue && plan.Promocion?.IdPromo == request.IdPromo)
                    ? plan.Promocion
                    : null;

        decimal monto = (tarifa.PrecioAdmMes * request.EmpAdm
                       + tarifa.PrecioEmpMes * (request.EmpTotal - request.EmpAdm))
                       * plan.DuracionMeses;

        if (promo is not null && promo.PctDescuento > 0)
            monto = Math.Round(monto * (1 - promo.PctDescuento / 100m), 2);

        await using var conn = new SqlConnection(db.getConnectionStringBuilder().ConnectionString);
        string instalacionId = await conn.ExecuteScalarAsync<string>(
            "SELECT CONVERT(VARCHAR(100), id_instalacion) FROM tDSV_SIS_Licencia") ?? string.Empty;

        var spParams = new DynamicParameters();
        spParams.Add("@cfg",                 cfg);
        spParams.Add("@instalacion_id",      instalacionId);
        spParams.Add("@id_plan",             request.IdPlan);
        spParams.Add("@id_promo",            request.IdPromo, dbType: DbType.Int32);
        spParams.Add("@emp_adm",             request.EmpAdm);
        spParams.Add("@emp_total",           request.EmpTotal);
        spParams.Add("@monto",               monto);
        spParams.Add("@metodo_pago",         request.MetodoPago);
        spParams.Add("@datos_fiscales_json", JsonSerializer.Serialize(request.DatosFiscales));
        spParams.Add("@id_pago",             dbType: DbType.Int32,  direction: ParameterDirection.Output);
        spParams.Add("@openpay_order_id",    dbType: DbType.String, size: 100, direction: ParameterDirection.Output);

        await conn.ExecuteAsync("pDSV_LIC_PagoGuardar", spParams, commandType: CommandType.StoredProcedure);

        int    idPago  = spParams.Get<int>("@id_pago");
        string orderId = spParams.Get<string>("@openpay_order_id");

        try
        {
            var chargeReq = new OpenPaySDK.Entities.Request.ChargeRequest
            {
                Method      = MapMethod(request.MetodoPago),
                Amount      = monto,
                Currency    = "MXN",
                Description = $"Licencia OneGoalDiscover – {plan.Nombre}",
                OrderId     = orderId,
                Customer    = new OpenPaySDK.Entities.Customer
                {
                    Name        = request.DatosFiscales.RazonSocial,
                    Email       = request.DatosFiscales.Email,
                    PhoneNumber = request.DatosFiscales.Telefono
                }
            };

            if (request.MetodoPago.Equals("card", StringComparison.OrdinalIgnoreCase))
            {
                chargeReq.SourceId        = request.TokenId;
                chargeReq.DeviceSessionId = request.DeviceSessionId;
            }

            var charge = await Task.Run(() => _api.ChargeService.Create(chargeReq), ct);

            // El orderId ya tiene formato {cfg}-{dbname}-OGD... así que UpdateStatus y FindForExtend
            // reciben dbName separado para construir la conexión correcta en el webhook.
            string dbName = ParseDbName(orderId);
            string? referencia = request.MetodoPago.ToLowerInvariant() switch
            {
                "spei" => charge.PaymentMethod?.CLABE,
                "oxxo" => charge.PaymentMethod?.Reference,
                _      => null
            };
            await UpdateStatusAsync(cfg, dbName, orderId, "pendiente", charge.Id, referencia, ct: ct);

            var response = new CreateChargeResponseDTO
            {
                IdPago         = idPago,
                OpenpayOrderId = orderId,
                Estatus        = "pendiente",
                Monto          = monto
            };

            // ponytail: propiedades de PaymentMethod verificar contra la versión del SDK Openpay 1.0.27
            if (charge.PaymentMethod is not null)
            {
                response.Clabe                 = charge.PaymentMethod.CLABE;
                response.BancoNombre           = charge.PaymentMethod.BankName;
                response.CodigoBarras          = charge.PaymentMethod.Reference;
                response.UrlBarcode            = charge.PaymentMethod.BarcodeURL;
                response.VencimientoReferencia = charge.DueDate;
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string dbName = ParseDbName(orderId);
            await UpdateStatusAsync(cfg, dbName, orderId, "fallido", ct: ct);
            throw new PaymentException($"Error al crear el cargo en OpenPay: {ex.Message}");
        }
    }

    // Conexión via dbName — para webhook (no hay JWT ni idCompany)
    public async Task UpdateStatusAsync(string cfg, string dbName, string orderId, string estatus, string? chargeId = null, string? referencia = null, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg);

        var csb = db.getConnectionStringBuilder();
        csb.InitialCatalog = dbName;

        var p = new DynamicParameters();
        p.Add("@openpay_order_id",  orderId);
        p.Add("@estatus",           estatus);
        p.Add("@openpay_charge_id", chargeId,   dbType: DbType.String);
        p.Add("@referencia_pago",   referencia, dbType: DbType.String);

        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.ExecuteAsync("pDSV_LIC_PagoActualizarEstatus", p, commandType: CommandType.StoredProcedure);
    }

    public async Task<(string InstalacionId, int EmpAdm, int EmpTotal, int DuracionMeses, int MesesGratis, string NombreBd)?> FindForExtendAsync(string cfg, string dbName, string orderId, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg);

        var csb = db.getConnectionStringBuilder();
        csb.InitialCatalog = dbName;

        var p = new DynamicParameters();
        p.Add("@openpay_order_id", orderId);

        await using var conn = new SqlConnection(csb.ConnectionString);
        var row = (await conn.QueryAsync<PagoExtendRow>(
            "pDSV_LIC_PagoObtenerPorOrden", p, commandType: CommandType.StoredProcedure)).FirstOrDefault();

        if (row is null) return null;

        string instalacionId = row.instalacion_id;
        if (string.IsNullOrEmpty(instalacionId))
            instalacionId = await conn.ExecuteScalarAsync<string>(
                "SELECT CONVERT(VARCHAR(100), id_instalacion) FROM tDSV_SIS_Licencia") ?? string.Empty;

        return (instalacionId, row.emp_adm, row.emp_total, row.duracion_meses, row.meses_gratis, row.nombre_bd);
    }

    // orderId = "{cfg}-{dbname}-OGD{timestamp}{hex}"
    // cfg no tiene '-', así que el primer '-' separa cfg del dbname, y "-OGD" delimita el resto.
    private static string ParseDbName(string orderId)
    {
        int oGDIdx  = orderId.IndexOf("-OGD", StringComparison.Ordinal);
        string prefix  = oGDIdx > 0 ? orderId[..oGDIdx] : orderId;
        int dashIdx = prefix.IndexOf('-');
        return dashIdx >= 0 ? prefix[(dashIdx + 1)..] : string.Empty;
    }

    public async Task<OneGoalDiscover_Models.DTOs.Payments.PaymentStatusDTO?> GetLatestPaymentStatusAsync(
        string cfg, short idCompany, string instalacionId, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);
        await using var conn = new SqlConnection(db.getConnectionStringBuilder().ConnectionString);
        return await conn.QueryFirstOrDefaultAsync<OneGoalDiscover_Models.DTOs.Payments.PaymentStatusDTO>(
            @"SELECT TOP 1
                id_pago                                    AS IdPago,
                CONVERT(VARCHAR(100), instalacion_id)      AS InstalacionId,
                openpay_order_id                           AS OpenpayOrderId,
                estatus                                    AS Estatus,
                monto                                      AS Monto,
                metodo_pago                                AS MetodoPago,
                referencia_pago                            AS ReferenciaPago,
                created_at                                 AS CreatedAt,
                updated_at                                 AS UpdatedAt
              FROM tDSV_LIC_Pagos
              WHERE CONVERT(VARCHAR(100), instalacion_id) = @instalacion_id
                AND estatus = 'pendiente'
              ORDER BY created_at DESC",
            new { instalacion_id = instalacionId });
    }

    private static string MapMethod(string method) => method.ToLowerInvariant() switch
    {
        "card" => "card",
        "spei" => "bank_account",
        "oxxo" => "store",
        _      => throw new PaymentException($"Método de pago no soportado: {method}")
    };

    private class PagoExtendRow
    {
        public string instalacion_id { get; set; } = string.Empty;
        public int    emp_adm        { get; set; }
        public int    emp_total      { get; set; }
        public int    duracion_meses { get; set; }
        public int    meses_gratis   { get; set; }
        public string nombre_bd      { get; set; } = string.Empty;
    }
}
