using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace CSRepVanguard;

/// <summary>
/// Configuración del plugin. Se serializa/deserializa desde
/// addons/counterstrikesharp/configs/plugins/CSRepVanguard/CSRepVanguard.json
/// </summary>
public class PluginConfig : BasePluginConfig
{
    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clave de autenticación para la API de Trust Rating.
    /// </summary>
    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = "CHANGE_ME";

    /// <summary>
    /// URL base del endpoint de csrep.gg.
    /// El SteamID64 se pasa como query param: {ApiBaseUrl}?ids={steamid64}
    /// Ejemplo: https://csrep.gg/api/players?ids=76561199015845161
    /// </summary>
    [JsonPropertyName("ApiBaseUrl")]
    public string ApiBaseUrl { get; set; } = "https://csrep.gg/api/players";

    // ── Lógica de baneo ───────────────────────────────────────────────────────

    /// <summary>
    /// Valor mínimo de Trust Rating para que el jugador NO sea baneado.
    /// Jugadores con Trust Rating estrictamente menor a este valor serán baneados.
    /// </summary>
    [JsonPropertyName("MinTrustRating")]
    public double MinTrustRating { get; set; } = 80.0;

    /// <summary>
    /// Comando del servidor que se ejecutará para banear al jugador.
    /// Placeholders disponibles:
    ///   {steamid}     → SteamID64 del jugador
    ///   {name}        → Nombre del jugador
    ///   {trustrating} → Valor de Trust Rating obtenido
    /// Por defecto usa el comando nativo de CounterStrikeSharp.
    /// </summary>
    [JsonPropertyName("BanCommand")]
    public string BanCommand { get; set; } = "css_ban #{steamid} 0 \"[CSRep] Trust Rating insuficiente ({trustrating})\"";

    // ── Cooldown de consultas ─────────────────────────────────────────────────

    /// <summary>
    /// Días mínimos que deben transcurrir desde la última consulta a la API
    /// antes de volver a consultar para el mismo jugador.
    /// Ejemplo: 1 → sólo se consulta una vez al día por jugador.
    /// </summary>
    [JsonPropertyName("QueryCooldownDays")]
    public int QueryCooldownDays { get; set; } = 1;

    // ── Base de datos ─────────────────────────────────────────────────────────

    /// <summary>
    /// Cadena de conexión a la base de datos MySQL/MariaDB compartida entre servidores.
    /// Formato: Server=host;Port=3306;Database=csrep;User=user;Password=pass;
    /// </summary>
    [JsonPropertyName("DatabaseConnectionString")]
    public string DatabaseConnectionString { get; set; } =
        "Server=localhost;Port=3306;Database=csrep;User=csrep_user;Password=CHANGE_ME;";

    /// <summary>
    /// Prefijo que se añade al nombre de las tablas creadas por el plugin.
    /// Ejemplo: "srv1_" genera la tabla "srv1_players".
    /// Dejar vacío para usar el nombre sin prefijo ("players").
    /// </summary>
    [JsonPropertyName("TablePrefix")]
    public string TablePrefix { get; set; } = "cs_rep_";
}
