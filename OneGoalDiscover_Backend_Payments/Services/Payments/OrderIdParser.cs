using System.Text.RegularExpressions;

namespace OneGoalDiscover_Backend_Payments.Services.Payments;

/// <summary>
/// Interpreta el order_id que genera pDSV_LIC_PagoGuardar: {cfg}-{DB_NAME()}-OGD{yyyyMMddHHmmssffff}{8 hex}.
/// El webhook usa cfg y base de datos para ubicar el pago, así que el formato se valida de forma estricta
/// (nada de rutas ni caracteres inesperados en el nombre del CFG que se abre desde disco).
/// </summary>
public static partial class OrderIdParser
{
    [GeneratedRegex(@"^(?<cfg>[A-Za-z0-9_]+)-(?<db>[\w.\-]+)-OGD\d{18}[0-9A-F]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderIdRegex();

    public static bool TryParse(string? orderId, out string cfg, out string dbName)
    {
        cfg = string.Empty;
        dbName = string.Empty;

        if (string.IsNullOrEmpty(orderId) || orderId.Length > 100)
            return false;

        Match match = OrderIdRegex().Match(orderId);
        if (!match.Success)
            return false;

        cfg = match.Groups["cfg"].Value;
        dbName = match.Groups["db"].Value;
        return true;
    }
}
