using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CSRepVanguard.Services;

/// <summary>
/// Consulta la API externa de Trust Rating usando el SteamID64 del jugador.
///
/// ⚠️  PENDIENTE DE CONFIGURACIÓN:
///   Cuando facilites el endpoint, apikey y formato de respuesta, actualiza:
///     1. BuildRequestMessage()  → para armar la URL/headers correctos.
///     2. ParseResponse()        → para leer el campo correcto del JSON.
/// </summary>
public class ApiService : IDisposable
{
    private readonly PluginConfig _config;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    public ApiService(PluginConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // La API de csrep.gg usa el header "x-api-key" para autenticación.
        _http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
    }

    // ── Consulta principal ────────────────────────────────────────────────────

    /// <summary>
    /// Obtiene el Trust Rating de un jugador.
    /// Devuelve null si la petición falla o el campo no está disponible.
    /// </summary>
    public async Task<double?> GetTrustRatingAsync(string steamId64)
    {
        try
        {
            using var request = BuildRequestMessage(steamId64);
            _logger.LogInformation("[CSRepVanguard] API → GET {Url}", request.RequestUri);

            using var response = await _http.SendAsync(request);
            _logger.LogInformation("[CSRepVanguard] API ← HTTP {StatusCode} para {SteamId}", (int)response.StatusCode, steamId64);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[CSRepVanguard] API respondió {StatusCode} para SteamID {SteamId}",
                    (int)response.StatusCode, steamId64);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[CSRepVanguard] API body para {SteamId}: {Body}", steamId64, body);
            return ParseResponse(body, steamId64);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[CSRepVanguard] Timeout al consultar API para {SteamId}.", steamId64);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CSRepVanguard] Error inesperado al consultar API para {SteamId}.", steamId64);
            return null;
        }
    }

    // ── Helpers (adaptar cuando llegue la info del endpoint) ──────────────────

    /// <summary>
    /// Construye el HttpRequestMessage.
    /// GET https://csrep.gg/api/players?ids={steamId64}
    /// Header: x-api-key (ya inyectado en DefaultRequestHeaders del constructor).
    /// </summary>
    private HttpRequestMessage BuildRequestMessage(string steamId64)
    {
        var url = $"{_config.ApiBaseUrl.TrimEnd('/')}?ids={Uri.EscapeDataString(steamId64)}";
        return new HttpRequestMessage(HttpMethod.Get, url);
    }

    /// <summary>
    /// Extrae el valor de Trust Rating del JSON de respuesta.
    /// Estructura esperada:
    /// {
    ///   "status": "OK",
    ///   "result": {
    ///     "id": "76561199015845161",
    ///     "trust_rating": 100,
    ///     ...
    ///   }
    /// }
    /// </summary>
    private double? ParseResponse(string json, string steamId64)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Verificar que la API indicó éxito.
            if (root.TryGetProperty("status", out var status) &&
                status.GetString() != "OK")
            {
                _logger.LogWarning(
                    "[CSRepVanguard] La API devolvió status={Status} para {SteamId}",
                    status.GetString(), steamId64);
                return null;
            }

            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("trust_rating", out var prop) &&
                prop.TryGetDouble(out var value))
            {
                return value;
            }

            _logger.LogWarning(
                "[CSRepVanguard] No se encontró 'result.trust_rating' en la respuesta para {SteamId}. JSON: {Json}",
                steamId64, json);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[CSRepVanguard] Respuesta JSON inválida para {SteamId}.", steamId64);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
