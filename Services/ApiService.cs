using System.Text.Json;

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
    private readonly HttpClient _http;

    public ApiService(PluginConfig config)
    {
        _config = config;

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
            Console.WriteLine($"[CSRepVanguard] API → GET {request.RequestUri}");

            using var response = await _http.SendAsync(request);
            Console.WriteLine($"[CSRepVanguard] API ← HTTP {(int)response.StatusCode} para {steamId64}");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[CSRepVanguard] API respondió {(int)response.StatusCode} para SteamID {steamId64}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[CSRepVanguard] API body para {steamId64}: {body}");
            return ParseResponse(body, steamId64);
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine($"[CSRepVanguard] Timeout al consultar API para {steamId64}.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error inesperado al consultar API para {steamId64}: {ex.Message}");
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
                Console.WriteLine($"[CSRepVanguard] La API devolvió status={status.GetString()} para {steamId64}");
                return null;
            }

            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("trust_rating", out var prop) &&
                prop.TryGetDouble(out var value))
            {
                return value;
            }

            Console.WriteLine($"[CSRepVanguard] No se encontró 'result.trust_rating' en la respuesta para {steamId64}. JSON: {json}");
            return null;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[CSRepVanguard] Respuesta JSON inválida para {steamId64}: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
