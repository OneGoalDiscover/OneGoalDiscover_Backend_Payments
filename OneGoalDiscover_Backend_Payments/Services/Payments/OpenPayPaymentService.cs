using Dapper;
using Microsoft.Data.SqlClient;
using OneGoalDiscover_Backend_Payments.Exceptions;
using OneGoalDiscover_Backend_Payments.Services.Infrastructure;
using OneGoalDiscover_Backend_Payments.Services.Plans;
using OneGoalDiscover_Helpers.System;
using OneGoalDiscover_Models.DTOs.Payments;
using OneGoalDiscover_Utilities.System.Database.Interfaces;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenPaySDK = Openpay;

namespace OneGoalDiscover_Backend_Payments.Services.Payments;

public class OpenPayPaymentService(
    IDatabaseConnectionUtility db,
    IPlansService plansService,
    IConfiguration configuration,
    ILicensingClient licensingClient,
    IHubsClient hubsClient,
    ILogger<OpenPayPaymentService> logger) : IOpenPayPaymentService
{
    private const decimal IvaRate = 0.16m;

    // Valores configurables en la sección OpenPay de appsettings
    private TimeSpan OpenPayTimeout        => TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue<int?>("OpenPay:TimeoutSeconds") ?? 30));
    private int      ReferenceExpirationHours => Math.Max(1, configuration.GetValue<int?>("OpenPay:ReferenceExpirationHours") ?? 72);
    private decimal  OxxoMaxAmount         => configuration.GetValue<decimal?>("OpenPay:OxxoMaxAmount") ?? 10000m;
    private bool     Use3DSecure           => configuration.GetValue<bool>("OpenPay:Use3DSecure");   // apagado por defecto: requiere ThreeDSecureReturnUrl válida

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
        ValidateRequest(request);

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

        decimal subtotal = (tarifa.PrecioAdmMes * request.EmpAdm
                          + tarifa.PrecioEmpMes * (request.EmpTotal - request.EmpAdm))
                          * plan.DuracionMeses;

        if (promo is not null && promo.PctDescuento > 0)
            subtotal = subtotal * (1 - promo.PctDescuento / 100m);

        // Los precios de tDSV_LIC_TarifasPlan son antes de IVA; el monto cobrado (y guardado en tDSV_LIC_Pagos.monto) es el total con IVA.
        subtotal = Math.Round(subtotal, 2);
        decimal monto = subtotal + Math.Round(subtotal * IvaRate, 2);

        // OXXO limita el monto por operación: se rechaza ANTES de registrar el pago o cobrar
        if (request.MetodoPago == "oxxo" && monto > OxxoMaxAmount)
            throw new PaymentValidationException(
                $"El monto a pagar ({FormatMoney(monto)}) excede el límite de pago en OXXO ({FormatMoney(OxxoMaxAmount)}). Elige transferencia SPEI o tarjeta.");

        string? threeDSecureReturnUrl = request.MetodoPago == "card"
            ? ResolveThreeDSecureReturnUrl(request.RedirectUrl, required: Use3DSecure)
            : null;

        // tDSV_SIS_Licencia, tDSV_LIC_Pagos y pDSV_LIC_PagoGuardar viven en la DB consolidada (MCP).
        await using var conn = new SqlConnection(db.getConnectionStringBuilder(useConsolidationDatabase: true).ConnectionString);

        string instalacionId = await conn.ExecuteScalarAsync<string>(
            "SELECT TOP 1 CONVERT(VARCHAR(100), id_instalacion) FROM dsv.tDSV_SIS_Licencia") ?? string.Empty;

        // Sin instalación válida no hay licencia que extender: se rechaza ANTES de registrar el pago o cobrar.
        if (!Guid.TryParse(instalacionId, out _))
        {
            logger.LogError("CreateCharge rechazado: tDSV_SIS_Licencia no tiene un id_instalacion válido (cfg={Cfg}, empresa={Company}).", cfg, idCompany);
            throw new PaymentValidationException("No se encontró la instalación de la licencia. Contacte a soporte antes de realizar el pago.");
        }

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
        spParams.Add("@idempotency_key",     request.IdempotencyKey, dbType: DbType.String, size: 64);
        spParams.Add("@ya_existia",          dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await conn.ExecuteAsync("dsv.pDSV_LIC_PagoGuardar", spParams, commandType: CommandType.StoredProcedure);

        int    idPago  = spParams.Get<int>("@id_pago");
        string orderId = spParams.Get<string>("@openpay_order_id");

        // Reintento del mismo intento de pago: se devuelve el pago original, no se crea otro cobro.
        if (spParams.Get<bool>("@ya_existia"))
            return await BuildExistingChargeResponseAsync(cfg, orderId, ct);

        try
        {
            var chargeReq = new OpenPaySDK.Entities.Request.ChargeRequest
            {
                Method      = MapMethod(request.MetodoPago),
                Amount      = monto,
                Currency    = "MXN",
                Description = $"Licencia OneGoalDiscover – {plan.Nombre}",
                OrderId     = orderId,
                SendEmail   = true,
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
                chargeReq.Use3DSecure     = Use3DSecure;
                if (threeDSecureReturnUrl is not null)
                    chargeReq.RedirectUrl = threeDSecureReturnUrl;
            }
            else
            {
                // SPEI / OXXO: vigencia explícita de la referencia (si no, OpenPay aplica su plazo por defecto)
                chargeReq.DueDate = MexicoNow().AddHours(ReferenceExpirationHours);
            }

            var charge = await RunOpenPayAsync(() => _api.ChargeService.Create(chargeReq), ct);

            // El orderId ya tiene formato {cfg}-{dbname}-OGD... así que UpdateStatus y FindForExtend
            // reciben dbName separado para construir la conexión correcta en el webhook.
            string dbName = ParseDbName(orderId);
            string? referencia = request.MetodoPago.ToLowerInvariant() switch
            {
                "spei" => charge.PaymentMethod?.CLABE,
                "oxxo" => charge.PaymentMethod?.Reference,
                _      => null
            };

            // Tarjeta 3DS: banco requiere autenticación del usuario → redirect, no completado aún.
            bool isRedirect   = string.Equals(charge.PaymentMethod?.Type, "redirect", StringComparison.OrdinalIgnoreCase);

            // Con 3D Secure activo, un cargo con tarjeta no debería completarse sin la autenticación del banco
            if (request.MetodoPago == "card" && Use3DSecure && !isRedirect
                && string.Equals(charge.Status, "completed", StringComparison.OrdinalIgnoreCase))
                logger.LogWarning(
                    "Cargo {ChargeId} con tarjeta completado SIN 3D Secure aunque OpenPay:Use3DSecure está activo (orderId={OrderId}). Revise la configuración de 3D Secure del comercio en OpenPay.",
                    charge.Id, orderId);

            // Tarjeta: OpenPay completa el cobro síncronamente — status llega "completed" de inmediato.
            // SPEI/OXXO quedan en "pendiente" hasta que el cliente paga (webhook posterior).
            bool cardCompleted = request.MetodoPago.Equals("card", StringComparison.OrdinalIgnoreCase)
                                 && !isRedirect
                                 && string.Equals(charge.Status, "completed", StringComparison.OrdinalIgnoreCase);
            string estatusInicial = cardCompleted ? "completado" : "pendiente";

            await UpdateStatusAsync(cfg, dbName, orderId, estatusInicial, charge.Id, referencia, ct: ct);

            // Idempotente: si el webhook ya extendió la licencia, el reclamo en BD evita extenderla otra vez.
            // Si falla, el pago queda completado con la licencia pendiente y el webhook (o conciliación) la reintenta.
            if (cardCompleted)
                await ExtendLicenseFromPaymentAsync(cfg, dbName, orderId, charge.Id, ct);

            var response = new CreateChargeResponseDTO
            {
                IdPago          = idPago,
                OpenpayOrderId  = orderId,
                OpenpayChargeId = charge.Id,
                Estatus         = estatusInicial,
                Monto           = monto
            };

            ApplyChargeToResponse(response, charge);
            return response;
        }
        catch (OpenPaySDK.OpenpayException ex) when ((int)ex.StatusCode < 500)
        {
            // OpenPay rechazó la solicitud (tarjeta rechazada, datos inválidos...): el cargo no se creó. Un 5xx es ambiguo y
            // cae en el catch siguiente.
            await UpdateStatusAsync(cfg, ParseDbName(orderId), orderId, "fallido", ct: ct);
            logger.LogWarning(ex, "OpenPay rechazó el cargo orderId={OrderId}: código {Code}, HTTP {Http}: {Description}",
                orderId, ex.ErrorCode, (int)ex.StatusCode, ex.Description);
            throw new PaymentException(OpenPayErrorMessages.ToUserMessage(ex.ErrorCode, (int)ex.StatusCode));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin respuesta confiable (timeout, red, fallo posterior al cobro): el cargo PUDO crearse, así que NO se marca
            // como fallido ni se invita a pagar de nuevo. Queda 'iniciado'; el webhook (verificado contra OpenPay) o la
            // consulta de estatus por orden lo resuelven, y un reintento con la misma clave devuelve este mismo pago.
            logger.LogError(ex, "CreateCharge sin resultado confirmado para orderId={OrderId}; queda 'iniciado'.", orderId);
            return new CreateChargeResponseDTO { IdPago = idPago, OpenpayOrderId = orderId, Estatus = "iniciado", Monto = monto };
        }
    }

    private static void ApplyChargeToResponse(CreateChargeResponseDTO response, OpenPaySDK.Entities.Charge charge)
    {
        if (charge.PaymentMethod is null) return;

        response.Clabe                 = charge.PaymentMethod.CLABE;
        response.BancoNombre           = charge.PaymentMethod.BankName;
        response.CodigoBarras          = charge.PaymentMethod.Reference;
        response.UrlBarcode            = charge.PaymentMethod.BarcodeURL;
        response.VencimientoReferencia = charge.DueDate;

        // Tarjeta 3DS: el banco requiere autenticación del usuario
        if (string.Equals(charge.PaymentMethod.Type, "redirect", StringComparison.OrdinalIgnoreCase))
            response.RedirectUrl = charge.PaymentMethod.Url;
    }

    // Reintento del mismo intento de pago (misma clave de idempotencia): se devuelve el pago original y NO se cobra otra vez.
    private async Task<CreateChargeResponseDTO> BuildExistingChargeResponseAsync(string cfg, string orderId, CancellationToken ct)
    {
        string dbName = ParseDbName(orderId);
        var row = await GetPaymentRowAsync(cfg, dbName, orderId)
                  ?? throw new PaymentException("No se encontró el pago del intento anterior. Inténtalo de nuevo.");

        var response = new CreateChargeResponseDTO
        {
            IdPago          = row.id_pago,
            OpenpayOrderId  = orderId,
            OpenpayChargeId = row.openpay_charge_id,
            Estatus         = row.estatus,
            Monto           = row.monto
        };

        // 'iniciado': el cobro anterior aún se procesa o no se pudo confirmar (el cliente consulta el estatus por orden).
        // 'completado': ya está cobrado.
        if (row.estatus != "pendiente" || string.IsNullOrEmpty(row.openpay_charge_id))
            return response;

        OpenPaySDK.Entities.Charge charge;
        try
        {
            charge = await RunOpenPayAsync(() => _api.ChargeService.Get(row.openpay_charge_id), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo consultar el cargo {ChargeId} para el reintento; se devuelve el pago guardado.", row.openpay_charge_id);
            return response;
        }

        var verification = ClassifyCharge(charge, orderId, row.monto);
        switch (verification.State)
        {
            case VerifiedChargeState.Completed:
                await UpdateStatusAsync(cfg, dbName, orderId, "completado", charge.Id, ct: ct);
                await ExtendLicenseFromPaymentAsync(cfg, dbName, orderId, charge.Id, ct);
                response.Estatus = "completado";
                ApplyChargeToResponse(response, charge);
                return response;

            case VerifiedChargeState.InProgress:
                ApplyChargeToResponse(response, charge);
                return response;

            case VerifiedChargeState.Failed:
                await UpdateStatusAsync(cfg, dbName, orderId, "fallido", charge.Id, ct: ct);
                throw new PaymentException("El intento de pago anterior ya no está vigente. Inténtalo de nuevo.");

            default:
                logger.LogWarning("Reintento de pago {OrderId} no verificable contra OpenPay: {Reason}", orderId, verification.Reason);
                return response;
        }
    }

    // El SDK de OpenPay es síncrono y no admite cancelación ni plazo: se ejecuta en el pool y se deja de esperar al
    // cumplirse OpenPay:TimeoutSeconds (la llamada abandonada termina sola). Un TimeoutException deja el pago 'iniciado'.
    private async Task<T> RunOpenPayAsync<T>(Func<T> call, CancellationToken ct)
    {
        var task = Task.Run(call);
        task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);   // observa el fallo si se abandona

        if (await Task.WhenAny(task, Task.Delay(OpenPayTimeout, ct)) != task)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"OpenPay no respondió en {OpenPayTimeout.TotalSeconds:0} segundos.");
        }

        return await task;
    }

    // due_date se envía en hora del centro de México (zona del comercio)
    private static DateTime MexicoNow()
    {
        foreach (string id in new[] { "Central Standard Time (Mexico)", "America/Mexico_City" })
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return DateTime.UtcNow.AddHours(-6);
    }

    // URL a la que el banco regresa al usuario tras 3D Secure. Si hay una URL fija configurada se usa esa (en la app MAUI la
    // dirección del WebView local no es alcanzable desde el banco); si no, se acepta la del cliente solo si es https de un
    // origen permitido (AllowedOrigins) o local.
    private string? ResolveThreeDSecureReturnUrl(string? requested, bool required)
    {
        string? configured = configuration["OpenPay:ThreeDSecureReturnUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        if (Uri.TryCreate(requested, UriKind.Absolute, out var uri) && IsAllowedReturnUri(uri))
            return uri.ToString();

        if (required)
            throw new PaymentValidationException("No se pudo determinar la dirección de retorno para la autenticación bancaria. Contacte a soporte.");

        return null;
    }

    private bool IsAllowedReturnUri(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        string origin = uri.GetLeftPart(UriPartial.Authority);
        string[] allowed = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
        return uri.Scheme == Uri.UriSchemeHttps
               && allowed.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatMoney(decimal amount) => amount.ToString("C", CultureInfo.GetCultureInfo("es-MX"));

    // Límite de OpenPay para el nombre y el correo del cliente
    private const int MaxCustomerFieldLength = 100;
    private static readonly HashSet<string> SupportedMethods = ["card", "spei", "oxxo"];

    // Valida lo que el servidor no debe aceptar del cliente. Normaliza MetodoPago a minúsculas.
    private static void ValidateRequest(CreateChargeRequestDTO request)
    {
        if (request.EmpAdm < 1)
            throw new PaymentValidationException("Debe contratarse al menos un administrador.");

        if (request.EmpTotal < request.EmpAdm)
            throw new PaymentValidationException("El total de empleados no puede ser menor que el de administradores.");

        if (request.EmpTotal > short.MaxValue)
            throw new PaymentValidationException("La cantidad de empleados no es válida.");

        string method = request.MetodoPago?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SupportedMethods.Contains(method))
            throw new PaymentValidationException($"Método de pago no soportado: {request.MetodoPago}");
        request.MetodoPago = method;

        if (method == "card" && (string.IsNullOrWhiteSpace(request.TokenId) || string.IsNullOrWhiteSpace(request.DeviceSessionId)))
            throw new PaymentValidationException("No se recibieron los datos de la tarjeta. Captúrala de nuevo e inténtalo otra vez.");

        // Clave de idempotencia opcional (GUID/identificador corto generado por el cliente por intento de pago)
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            request.IdempotencyKey = null;
        else if (request.IdempotencyKey.Length > 64 || !request.IdempotencyKey.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new PaymentValidationException("La clave del intento de pago no es válida.");

        ValidateCustomer(request.DatosFiscales);
    }

    private static void ValidateCustomer(DatosFiscalesDTO? datos)
    {
        string name = datos?.RazonSocial?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new PaymentValidationException("El nombre de la empresa es obligatorio.");
        if (name.Length > MaxCustomerFieldLength)
            throw new PaymentValidationException($"El nombre de la empresa no puede exceder {MaxCustomerFieldLength} caracteres.");

        if (!IsValidEmail(datos!.Email))
            throw new PaymentValidationException("El correo electrónico no es válido.");

        if (new[] { datos.Calle, datos.Ciudad, datos.Estado }.Any(v => (v?.Trim().Length ?? 0) > MaxCustomerFieldLength))
            throw new PaymentValidationException($"Calle, ciudad y estado no pueden exceder {MaxCustomerFieldLength} caracteres.");

        string phone = datos.Telefono?.Trim() ?? string.Empty;
        if (phone.Length > 0 && !(phone.Length == 10 && phone.All(char.IsDigit)))
            throw new PaymentValidationException("El teléfono debe tener 10 dígitos.");
    }

    private static bool IsValidEmail(string? email)
    {
        string value = email?.Trim() ?? string.Empty;
        return value.Length is > 0 and <= MaxCustomerFieldLength
               && MailAddress.TryCreate(value, out var address)
               && address.Address == value;   // descarta formas como "Nombre <correo@x.com>"
    }

    // Conexión via dbName — para webhook (no hay JWT ni idCompany).
    // Retorna true si el registro fue actualizado; false si ya estaba 'completado' (guard idempotente).
    public async Task<bool> UpdateStatusAsync(string cfg, string dbName, string orderId, string estatus, string? chargeId = null, string? referencia = null, bool restore = false, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg);

        var csb = db.getConnectionStringBuilder();
        csb.InitialCatalog = dbName;

        var p = new DynamicParameters();
        p.Add("@openpay_order_id",  orderId);
        p.Add("@estatus",           estatus);
        p.Add("@openpay_charge_id", chargeId,   dbType: DbType.String);
        p.Add("@referencia_pago",   referencia, dbType: DbType.String);
        if (restore)
            p.Add("@restaurar", true);   // solo se envía cuando hace falta: así el SP anterior sigue funcionando
        p.Add("@rows_affected",     dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.ExecuteAsync("dsv.pDSV_LIC_PagoActualizarEstatus", p, commandType: CommandType.StoredProcedure);

        return p.Get<int>("@rows_affected") > 0;
    }

    public async Task<(string InstalacionId, int EmpAdm, int EmpTotal, int DuracionMeses, int MesesGratis, string NombreBd)?> FindForExtendAsync(string cfg, string dbName, string orderId, CancellationToken ct = default)
    {
        var row = await GetPaymentRowAsync(cfg, dbName, orderId);
        if (row is null) return null;

        string instalacionId = row.instalacion_id;
        if (string.IsNullOrEmpty(instalacionId))
        {
            await using var conn = await CreateConnectionAsync(cfg, dbName);
            instalacionId = await conn.ExecuteScalarAsync<string>(
                "SELECT CONVERT(VARCHAR(100), id_instalacion) FROM dsv.tDSV_SIS_Licencia") ?? string.Empty;
        }

        return (instalacionId, row.emp_adm, row.emp_total, row.duracion_meses, row.meses_gratis, row.nombre_bd);
    }

    // No se confía en el payload del webhook: la orden, el monto y el estatus se confirman consultando el cargo
    // en OpenPay (con la llave privada) y contrastándolo con el pago guardado. Solo un cargo real puede completar un pago.
    public async Task<ChargeVerification> VerifyChargeAsync(string cfg, string dbName, string orderId, string chargeId, bool interpretStatus = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chargeId))
            return new ChargeVerification(VerifiedChargeState.Invalid, "El evento no trae el id del cargo.");

        OpenPaySDK.Entities.Charge charge;
        try
        {
            charge = await RunOpenPayAsync(() => _api.ChargeService.Get(chargeId), ct);
        }
        catch (OpenPaySDK.OpenpayException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ChargeVerification(VerifiedChargeState.Invalid, $"El cargo {chargeId} no existe en OpenPay.");
        }

        var payment = await GetPaymentRowAsync(cfg, dbName, orderId);
        if (payment is null)
            return new ChargeVerification(VerifiedChargeState.Invalid, "El pago no existe en la base de datos.");

        return ClassifyCharge(charge, orderId, payment.monto, interpretStatus);
    }

    // Contrasta el cargo de OpenPay con la orden y el monto guardados antes de interpretar su estatus
    private static ChargeVerification ClassifyCharge(OpenPaySDK.Entities.Charge charge, string orderId, decimal expectedAmount, bool interpretStatus = true)
    {
        if (!string.Equals(charge.OrderId, orderId, StringComparison.Ordinal))
            return new ChargeVerification(VerifiedChargeState.Invalid, $"El order_id del cargo ({charge.OrderId}) no coincide con el esperado.");

        if (charge.Amount != expectedAmount)
            return new ChargeVerification(VerifiedChargeState.Invalid, $"El monto del cargo ({charge.Amount}) no coincide con el pago guardado ({expectedAmount}).");

        return interpretStatus ? MapChargeStatus(charge.Status) : new ChargeVerification(VerifiedChargeState.Matched);
    }

    private static ChargeVerification MapChargeStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "completed"                    => new ChargeVerification(VerifiedChargeState.Completed),
        "failed" or "cancelled"        => new ChargeVerification(VerifiedChargeState.Failed),
        "in_progress" or "charge_pending" => new ChargeVerification(VerifiedChargeState.InProgress),
        _                              => new ChargeVerification(VerifiedChargeState.Invalid, $"Estatus de cargo no manejado: {status}")
    };

    private async Task<PagoExtendRow?> GetPaymentRowAsync(string cfg, string dbName, string orderId)
    {
        await using var conn = await CreateConnectionAsync(cfg, dbName);

        var p = new DynamicParameters();
        p.Add("@openpay_order_id", orderId);

        return (await conn.QueryAsync<PagoExtendRow>(
            "dsv.pDSV_LIC_PagoObtenerPorOrden", p, commandType: CommandType.StoredProcedure)).FirstOrDefault();
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

    // Último pago pendiente de la empresa. Para SPEI/OXXO/3DS se consulta OpenPay y se sincroniza antes de responder:
    //  - ya cobrado (webhook perdido)  -> se completa, se extiende la licencia y se responde "completado";
    //  - fallido, cancelado o referencia vencida -> se cierra como "fallido" y se responde null (el usuario puede reintentar);
    //  - vigente -> se responde con los datos de la referencia (CLABE/código de barras, banco, vencimiento).
    // Es idempotente: el estatus y la extensión de licencia ya lo son. Si OpenPay no responde se devuelve lo guardado.
    public async Task<PaymentStatusDTO?> GetLatestPaymentStatusAsync(
        string cfg, short idCompany, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);

        PendingPaymentRow? row = await QueryPaymentAsync();
        if (row is null) return null;

        var status = row.ToStatusDto();
        if (string.IsNullOrEmpty(status.OpenpayChargeId))
            return status;   // aún no se creó el cargo en OpenPay

        return await RefreshPendingFromOpenPayAsync(cfg, status, ct);
    }

    // Estatus de un pago concreto de la empresa (cualquier estatus). Lo usa el wizard para consultar periódicamente cuando
    // el aviso por hub no llega (o no existe, como en la renovación fuera del sistema). Si sigue pendiente se sincroniza con
    // OpenPay igual que el último pendiente; si OpenPay lo cerró (vencido/cancelado) responde "fallido".
    public async Task<PaymentStatusDTO?> GetPaymentStatusAsync(string cfg, short idCompany, string orderId, CancellationToken ct = default)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg, idCompany);

        PendingPaymentRow? row = await QueryPaymentAsync(orderId);
        if (row is null) return null;

        var status = row.ToStatusDto();
        if (status.Estatus != "pendiente" || string.IsNullOrEmpty(status.OpenpayChargeId))
            return status;

        var refreshed = await RefreshPendingFromOpenPayAsync(cfg, status, ct);
        if (refreshed is not null)
            return refreshed;

        status.Estatus = "fallido";
        return status;
    }

    // Sin orderId: el último pago pendiente de la empresa. Con orderId: ese pago, en cualquier estatus.
    private async Task<PendingPaymentRow?> QueryPaymentAsync(string? orderId = null)
    {
        // tDSV_LIC_Pagos vive en la DB consolidada (MCP), igual que tDSV_SIS_Licencia.
        await using var conn = new SqlConnection(db.getConnectionStringBuilder(useConsolidationDatabase: true).ConnectionString);

        string instalacionId = string.Empty;
        string filter;
        if (orderId is not null)
        {
            filter = "openpay_order_id = @order_id";
        }
        else
        {
            instalacionId = await conn.ExecuteScalarAsync<string>(
                "SELECT TOP 1 CONVERT(VARCHAR(100), id_instalacion) FROM dsv.tDSV_SIS_Licencia") ?? string.Empty;

            // Con instalacion_id se filtra por ella; si no hay (sandbox / entorno nuevo) se toma el último pendiente de la empresa:
            // el aislamiento ya lo da la conexión por empresa.
            filter = "estatus = 'pendiente'" + (string.IsNullOrEmpty(instalacionId)
                ? string.Empty
                : " AND CONVERT(VARCHAR(100), instalacion_id) = @instalacion_id");
        }

        string sql = $@"SELECT TOP 1
                            id_pago                               AS IdPago,
                            CONVERT(VARCHAR(100), instalacion_id) AS InstalacionId,
                            openpay_order_id                      AS OpenpayOrderId,
                            openpay_charge_id                     AS OpenpayChargeId,
                            estatus                               AS Estatus,
                            monto                                 AS Monto,
                            metodo_pago                           AS MetodoPago,
                            referencia_pago                       AS ReferenciaPago,
                            created_at                            AS CreatedAt,
                            updated_at                            AS UpdatedAt,
                            id_plan                               AS IdPlan,
                            emp_adm                               AS EmpAdm,
                            emp_total                             AS EmpTotal,
                            datos_fiscales_json                   AS DatosFiscalesJson
                        FROM dsv.tDSV_LIC_Pagos
                        WHERE {filter}
                        ORDER BY created_at DESC";

        return await conn.QueryFirstOrDefaultAsync<PendingPaymentRow>(sql, new { instalacion_id = instalacionId, order_id = orderId });
    }

    private async Task<PaymentStatusDTO?> RefreshPendingFromOpenPayAsync(string cfg, PaymentStatusDTO status, CancellationToken ct)
    {
        OpenPaySDK.Entities.Charge charge;
        try
        {
            charge = await RunOpenPayAsync(() => _api.ChargeService.Get(status.OpenpayChargeId!), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo consultar el cargo {ChargeId} en OpenPay; se devuelve el estatus guardado.", status.OpenpayChargeId);
            return status;
        }

        string dbName = ParseDbName(status.OpenpayOrderId);
        var verification = ClassifyCharge(charge, status.OpenpayOrderId, status.Monto);

        switch (verification.State)
        {
            case VerifiedChargeState.Completed:
                await UpdateStatusAsync(cfg, dbName, status.OpenpayOrderId, "completado", charge.Id, ct: ct);
                await ExtendLicenseFromPaymentAsync(cfg, dbName, status.OpenpayOrderId, charge.Id, ct);
                status.Estatus = "completado";
                return status;

            case VerifiedChargeState.Failed:
                await UpdateStatusAsync(cfg, dbName, status.OpenpayOrderId, "fallido", charge.Id, ct: ct);
                return null;

            case VerifiedChargeState.InProgress:
                ApplyChargeDetails(status, charge);
                if (!IsReferenceExpired(charge.DueDate))
                    return status;

                // Vencida sin que OpenPay la haya cancelado todavía: se libera al usuario. Si el pago llegara tarde,
                // el webhook charge.succeeded (verificado por API) lo vuelve a completar.
                await UpdateStatusAsync(cfg, dbName, status.OpenpayOrderId, "fallido", charge.Id, ct: ct);
                return null;

            default:
                logger.LogWarning("Pago pendiente {OrderId} no verificable contra OpenPay: {Reason}", status.OpenpayOrderId, verification.Reason);
                return status;
        }
    }

    private static void ApplyChargeDetails(PaymentStatusDTO status, OpenPaySDK.Entities.Charge charge)
    {
        status.BancoNombre           = charge.PaymentMethod?.BankName;
        status.UrlBarcode            = charge.PaymentMethod?.BarcodeURL;
        status.VencimientoReferencia = charge.DueDate;
        status.ReferenciaPago      ??= string.Equals(status.MetodoPago, "spei", StringComparison.OrdinalIgnoreCase)
                                       ? charge.PaymentMethod?.CLABE
                                       : charge.PaymentMethod?.Reference;
    }

    // Margen para diferencias de zona horaria y pagos de último minuto antes de dar por vencida una referencia
    private static readonly TimeSpan ReferenceExpirationGrace = TimeSpan.FromHours(2);

    private static bool IsReferenceExpired(DateTime? dueDate) =>
        dueDate.HasValue && dueDate.Value.ToUniversalTime() < DateTime.UtcNow - ReferenceExpirationGrace;

    public OpenPayPublicConfigDTO GetPublicConfig() => new()
    {
        MerchantId     = configuration["OpenPay:MerchantId"]  ?? string.Empty,
        PublicKey      = configuration["OpenPay:PublicKey"]   ?? string.Empty,
        ProductionMode = configuration.GetValue<bool>("OpenPay:ProductionMode"),
        OxxoMaxAmount  = OxxoMaxAmount
    };

    // Extiende la licencia de un pago completado. Es idempotente y reintentable: el reclamo en BD
    // (pDSV_LIC_PagoReclamarLicencia) garantiza una sola extensión por pago aunque coincidan el cobro síncrono
    // con tarjeta y el webhook. Devuelve false solo cuando la extensión falló y debe reintentarse.
    public async Task<bool> ExtendLicenseFromPaymentAsync(string cfg, string dbName, string orderId, string chargeId, CancellationToken ct = default)
    {
        try
        {
            if (!await TryClaimLicenseAsync(cfg, dbName, orderId, ct))
                return true;   // ya aplicada, o la está aplicando otro proceso

            return await ApplyClaimedLicenseAsync(cfg, dbName, orderId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ExtendLicense: error inesperado orderId={OrderId}.", orderId);
            return false;
        }
    }

    private async Task<bool> ApplyClaimedLicenseAsync(string cfg, string dbName, string orderId, CancellationToken ct)
    {
        var info = await FindForExtendAsync(cfg, dbName, orderId, ct);
        if (info is null)
        {
            logger.LogError("ExtendLicense: pago no encontrado en BD para orderId={OrderId}.", orderId);
            await RegisterLicenseResultAsync(cfg, dbName, orderId, false, "Pago no encontrado en BD.", ct);
            return false;
        }

        int duracionTotal = info.Value.DuracionMeses + info.Value.MesesGratis;
        try
        {
            await licensingClient.ExtendLicenseAsync(
                info.Value.InstalacionId, duracionTotal,
                info.Value.EmpAdm, info.Value.EmpTotal, orderId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error al extender licencia orderId={OrderId}; quedará pendiente de reintento.", orderId);
            await RegisterLicenseResultAsync(cfg, dbName, orderId, false, ex.Message, ct);
            return false;
        }

        logger.LogInformation(
            "Licencia extendida: instalacion={Id} meses={Meses} bd={Db} orderId={OrderId}",
            info.Value.InstalacionId, duracionTotal, info.Value.NombreBd, orderId);

        try
        {
            await RegisterLicenseResultAsync(cfg, dbName, orderId, true, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La licencia ya se extendió, pero Licensing es idempotente por orden de pago: el reintento solo completará este registro.
            logger.LogError(ex, "Licencia extendida pero no se pudo registrar en tDSV_LIC_Pagos orderId={OrderId}; se reintentará.", orderId);
            return false;
        }

        await hubsClient.BroadcastLicenseUpdatedAsync(cfg, ct);
        return true;
    }

    private async Task<bool> TryClaimLicenseAsync(string cfg, string dbName, string orderId, CancellationToken ct)
    {
        await using var conn = await CreateConnectionAsync(cfg, dbName);

        var p = new DynamicParameters();
        p.Add("@openpay_order_id", orderId);
        p.Add("@rows_affected",    dbType: DbType.Int32, direction: ParameterDirection.Output);

        await conn.ExecuteAsync("dsv.pDSV_LIC_PagoReclamarLicencia", p, commandType: CommandType.StoredProcedure);
        return p.Get<int>("@rows_affected") > 0;
    }

    private async Task RegisterLicenseResultAsync(string cfg, string dbName, string orderId, bool applied, string? error, CancellationToken ct)
    {
        await using var conn = await CreateConnectionAsync(cfg, dbName);

        var p = new DynamicParameters();
        p.Add("@openpay_order_id", orderId);
        p.Add("@aplicada",         applied);
        p.Add("@error",            error, dbType: DbType.String);

        await conn.ExecuteAsync("dsv.pDSV_LIC_PagoRegistrarLicencia", p, commandType: CommandType.StoredProcedure);
    }

    private async Task<SqlConnection> CreateConnectionAsync(string cfg, string dbName)
    {
        await _dbHelper.configureConnections(configuration["CFGsPath"]!, cfg);

        var csb = db.getConnectionStringBuilder();
        csb.InitialCatalog = dbName;
        return new SqlConnection(csb.ConnectionString);
    }

    private static string MapMethod(string method) => method.ToLowerInvariant() switch
    {
        "card" => "card",
        "spei" => "bank_account",
        "oxxo" => "store",
        _      => throw new PaymentException($"Método de pago no soportado: {method}")
    };

    // Renglón de tDSV_LIC_Pagos para reconstruir el pago pendiente; el JSON fiscal no sale del servicio
    private sealed class PendingPaymentRow
    {
        public int       IdPago            { get; set; }
        public string    InstalacionId     { get; set; } = string.Empty;
        public string    OpenpayOrderId    { get; set; } = string.Empty;
        public string?   OpenpayChargeId   { get; set; }
        public string    Estatus           { get; set; } = string.Empty;
        public decimal   Monto             { get; set; }
        public string    MetodoPago        { get; set; } = string.Empty;
        public string?   ReferenciaPago    { get; set; }
        public DateTime  CreatedAt         { get; set; }
        public DateTime? UpdatedAt         { get; set; }
        public int       IdPlan            { get; set; }
        public int       EmpAdm            { get; set; }
        public int       EmpTotal          { get; set; }
        public string?   DatosFiscalesJson { get; set; }

        public PaymentStatusDTO ToStatusDto() => new()
        {
            IdPago          = IdPago,
            InstalacionId   = InstalacionId,
            OpenpayOrderId  = OpenpayOrderId,
            OpenpayChargeId = OpenpayChargeId,
            Estatus         = Estatus,
            Monto           = Monto,
            MetodoPago      = MetodoPago,
            ReferenciaPago  = ReferenciaPago,
            CreatedAt       = CreatedAt,
            UpdatedAt       = UpdatedAt,
            IdPlan          = IdPlan,
            EmpAdm          = EmpAdm,
            EmpTotal        = EmpTotal,
            RazonSocial     = ReadCompanyName()
        };

        private string? ReadCompanyName()
        {
            if (string.IsNullOrWhiteSpace(DatosFiscalesJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(DatosFiscalesJson);
                return doc.RootElement.TryGetProperty("RazonSocial", out var name) ? name.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private class PagoExtendRow
    {
        public int    id_pago        { get; set; }
        public string? openpay_charge_id { get; set; }
        public string estatus        { get; set; } = string.Empty;
        public string instalacion_id { get; set; } = string.Empty;
        public int    emp_adm        { get; set; }
        public int    emp_total      { get; set; }
        public decimal monto         { get; set; }
        public int    duracion_meses { get; set; }
        public int    meses_gratis   { get; set; }
        public string nombre_bd      { get; set; } = string.Empty;
    }
}
