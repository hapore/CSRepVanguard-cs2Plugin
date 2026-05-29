using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CSRepVanguard.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSRepVanguard;

/// <summary>
/// Plugin principal de CSRepVanguard.
/// Al conectarse un jugador, consulta su Trust Rating en la API y,
/// si está por debajo del umbral configurado, ejecuta el comando de baneo.
/// Los registros se persisten en MySQL para compartirse entre servidores.
/// </summary>
public class CSRepVanguardPlugin : BasePlugin
{
    public override string ModuleName => "CSRepVanguard";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "laspi94";
    public override string ModuleDescription =>
        "Verifica el Trust Rating de jugadores al conectarse y los banea si es insuficiente.";

    // IPluginConfig<T> obliga a exponer la config y el método OnConfigParsed.
    public PluginConfig Config { get; set; } = new();

    private DatabaseService _db = null!;
    private ApiService _api = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // ── Ciclo de vida del plugin ───────────────────────────────────────────────

    public override void Load(bool hotReload)
    {
        Config = LoadOrCreateConfig();

        _db = new DatabaseService(Config, Logger);
        _api = new ApiService(Config, Logger);

        // Inicializar la tabla y reaplicar bans al arrancar el servidor.
        Task.Run(async () =>
        {
            try
            {
                await _db.InitializeAsync();
                Logger.LogInformation("[CSRepVanguard] Plugin cargado. MinTrustRating={Min}, CooldownDays={Days}",
                    Config.MinTrustRating, Config.QueryCooldownDays);

                // Al iniciarse el plugin por primera vez (o en cada carga), reaplicar ban
                // a todos los jugadores que ya estaban marcados como baneados en la DB.
                var bannedPlayers = (await _db.GetAllBannedPlayersAsync()).ToList();
                if (bannedPlayers.Count > 0)
                {
                    Logger.LogInformation(
                        "[CSRepVanguard] Startup: reaplicando ban a {Count} jugador(es) registrados en DB.",
                        bannedPlayers.Count);
                    foreach (var r in bannedPlayers)
                        ExecuteBan(r.SteamId, r.SteamId, r.TrustRating);
                }
                else
                {
                    Logger.LogInformation("[CSRepVanguard] Startup: no hay jugadores baneados en DB.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[CSRepVanguard] Fallo al conectar con la base de datos. El plugin no funcionará.");
            }
        });

        AddCommand("css_csrep", "Comandos de administración CSRepVanguard", OnCsrepCommand);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
    }

    public override void Unload(bool hotReload)
    {
        _api?.Dispose();
    }

    // ── Config manual ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lee el archivo de configuración si ya existe.
    /// Solo lo crea (con valores por defecto) si no existe.
    /// Nunca sobreescribe un archivo existente.
    /// </summary>
    private PluginConfig LoadOrCreateConfig()
    {
        // ModuleDirectory → .../plugins/CSRepVanguard/
        // config path     → .../configs/plugins/CSRepVanguard/CSRepVanguard.json
        var configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", ModuleName));
        var configPath = Path.Combine(configDir, $"{ModuleName}.json");

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<PluginConfig>(json, JsonOptions) ?? new PluginConfig();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[CSRepVanguard] Error al leer {Path}. Usando valores por defecto.", configPath);
                return new PluginConfig();
            }
        }

        // El archivo no existe — intentar crearlo con los valores por defecto.
        var defaultConfig = new PluginConfig();
        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            Logger.LogInformation("[CSRepVanguard] Archivo de configuración creado en {Path}", configPath);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[CSRepVanguard] No se pudo crear {Path}. Créalo manualmente con el contenido del README.",
                configPath);
        }

        return defaultConfig;
    }

    // ── Evento de conexión ────────────────────────────────────────────────────

    [GameEventHandler]
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        // Ignorar bots, entidades inválidas y GOTV.
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        var steamId = player.SteamID.ToString();
        var playerName = player.PlayerName;
        Logger.LogInformation("[CSRepVanguard] Jugador conectado: {Name} | SteamID={SteamId}", playerName, steamId);

        // Solo pasamos strings al hilo de fondo — nunca el objeto nativo CCSPlayerController.
        Task.Run(() => CheckPlayerTrustAsync(steamId, playerName));

        return HookResult.Continue;
    }

    // ── Lógica de verificación ────────────────────────────────────────────────

    private async Task CheckPlayerTrustAsync(string steamId, string playerName)
    {
        try
        {
            var record = await _db.GetPlayerRecordAsync(steamId);

            if (record is null)
                Logger.LogInformation("[CSRepVanguard] {Name} ({SteamId}) → sin registro en DB, consultará API.", playerName, steamId);
            else
                Logger.LogInformation("[CSRepVanguard] {Name} ({SteamId}) → registro en DB: TrustRating={Rating:F2}, LastChecked={LastChecked:u}, IsBanned={Banned}",
                    playerName, steamId, record.TrustRating, record.LastChecked, record.IsBanned);

            // ── Caso 1: jugador ya registrado y baneado → reaplicar ban inmediatamente ──
            // No se consulta la API ni se comprueba el cooldown: el ban ya fue dictaminado.
            if (record is not null && record.IsBanned)
            {
                Logger.LogInformation(
                    "[CSRepVanguard] {Name} ({SteamId}) está marcado como baneado en DB. Reaplicando ban sin consultar la API.",
                    playerName, steamId);
                ExecuteBan(steamId, playerName, record.TrustRating);
                return;
            }

            // ── Caso 2: no está baneado → verificar si el cooldown expiró ────────────
            bool needsApiQuery = record is null ||
                (DateTime.UtcNow - record.LastChecked).TotalDays >= Config.QueryCooldownDays;

            Logger.LogInformation("[CSRepVanguard] {Name} ({SteamId}) → needsApiQuery={NeedsQuery} (CooldownDays={Cooldown})",
                playerName, steamId, needsApiQuery, Config.QueryCooldownDays);

            double trustRating;

            if (needsApiQuery)
            {
                Logger.LogInformation("[CSRepVanguard] Consultando API para {Name} ({SteamId})...", playerName, steamId);

                var result = await _api.GetTrustRatingAsync(steamId);
                if (result is null)
                {
                    // Si la API falla, no banear: dar beneficio de la duda.
                    Logger.LogWarning("[CSRepVanguard] No se pudo obtener Trust Rating para {Name} ({SteamId}). Se omite el baneo.",
                        playerName, steamId);
                    return;
                }

                trustRating = result.Value;
                bool willBan = trustRating < Config.MinTrustRating;

                Logger.LogInformation("[CSRepVanguard] API devolvió TrustRating={Rating:F2} para {Name} ({SteamId}). Guardando en DB (willBan={WillBan})...",
                    trustRating, playerName, steamId, willBan);
                await _db.UpsertPlayerRecordAsync(steamId, trustRating, willBan);
                Logger.LogInformation("[CSRepVanguard] DB actualizada para {Name} ({SteamId}) → TrustRating={Rating:F2}", playerName, steamId, trustRating);
            }
            else
            {
                // Usar el valor cacheado en la DB.
                trustRating = record!.TrustRating;
                Logger.LogInformation(
                    "[CSRepVanguard] Usando Trust Rating en caché para {Name} ({SteamId}): {Rating:F2} (próxima consulta en {Days:F1} día/s)",
                    playerName, steamId, trustRating,
                    Config.QueryCooldownDays - (DateTime.UtcNow - record.LastChecked).TotalDays);
            }

            if (trustRating < Config.MinTrustRating)
            {
                ExecuteBan(steamId, playerName, trustRating);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[CSRepVanguard] Error inesperado verificando a {Name} ({SteamId}).", playerName, steamId);
        }
    }

    // ── Baneo ─────────────────────────────────────────────────────────────────

    private void ExecuteBan(string steamId, string playerName, double trustRating)
    {
        var command = Config.BanCommand
            .Replace("{steamid}", steamId)
            .Replace("{name}", playerName)
            .Replace("{trustrating}", trustRating.ToString("F2"));

        // Las operaciones del servidor deben ejecutarse en el hilo del juego.
        Server.NextFrame(() =>
        {
            Logger.LogInformation(
                "[CSRepVanguard] Baneando a {Name} ({SteamId}) por Trust Rating {Rating:F2} < {Min:F2}. Comando: {Cmd}",
                playerName, steamId, trustRating, Config.MinTrustRating, command);

            Server.ExecuteCommand(command);
        });
    }

    // ── Desbaneo ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Ejecuta el comando de desbaneo para un SteamID.
    /// DEBE llamarse desde dentro de un Server.NextFrame().
    /// </summary>
    private void ExecuteUnban(string steamId)
    {
        var command = Config.UnbanCommand.Replace("{steamid}", steamId);
        Logger.LogInformation("[CSRepVanguard] Desbaneando {SteamId}. Comando: {Cmd}", steamId, command);
        Server.ExecuteCommand(command);
    }

    // ── Comando css_csrep ─────────────────────────────────────────────────────

    private void OnCsrepCommand(CCSPlayerController? player, CommandInfo info)
    {
        var sub = info.ArgCount >= 2 ? info.GetArg(1).ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "unbanall":
                Task.Run(() => HandleUnbanAllAsync(player));
                break;

            default:
                info.ReplyToCommand("[CSRepVanguard] Uso: css_csrep unbanall");
                break;
        }
    }

    /// <summary>
    /// Desbanea a todos los jugadores registrados en la DB como baneados,
    /// ejecuta el UnbanCommand por cada uno y limpia el flag en la tabla.
    /// </summary>
    private async Task HandleUnbanAllAsync(CCSPlayerController? caller)
    {
        try
        {
            var bannedPlayers = (await _db.GetAllBannedPlayersAsync()).ToList();

            Logger.LogInformation(
                "[CSRepVanguard] css_csrep unbanall → desbaneando {Count} jugador(es).",
                bannedPlayers.Count);

            // Ejecutar el comando de desbaneo en el hilo del juego.
            Server.NextFrame(() =>
            {
                foreach (var r in bannedPlayers)
                    ExecuteUnban(r.SteamId);
            });

            // Actualizar la DB fuera del hilo del juego.
            await _db.UnbanAllPlayersAsync();

            var msg = $"[CSRepVanguard] {bannedPlayers.Count} jugador(es) desbaneados correctamente.";
            Logger.LogInformation(msg);

            Server.NextFrame(() =>
            {
                if (caller is not null && caller.IsValid)
                    caller.PrintToChat(msg);
                else
                    Server.PrintToConsole(msg);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[CSRepVanguard] Error ejecutando css_csrep unbanall.");
        }
    }
}
