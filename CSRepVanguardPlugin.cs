using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CSRepVanguard.Services;
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

        _db = new DatabaseService(Config);
        _api = new ApiService(Config);

        // Inicializar la tabla al arrancar el servidor.
        Task.Run(async () =>
        {
            try
            {
                await _db.InitializeAsync();
                Console.WriteLine($"[CSRepVanguard] Plugin cargado. MinTrustRating={Config.MinTrustRating}, CooldownDays={Config.QueryCooldownDays}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CSRepVanguard] Fallo al conectar con la base de datos: {ex.Message}");
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
                Console.WriteLine($"[CSRepVanguard] Error al leer {configPath}. Usando valores por defecto. Error: {ex.Message}");
                return new PluginConfig();
            }
        }

        // El archivo no existe — intentar crearlo con los valores por defecto.
        var defaultConfig = new PluginConfig();
        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
            Console.WriteLine($"[CSRepVanguard] Archivo de configuración creado en {configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] No se pudo crear {configPath}. Créalo manualmente con el contenido del README. Error: {ex.Message}");
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
        Console.WriteLine($"[CSRepVanguard] Jugador conectado: {playerName} | SteamID={steamId}");

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
                Console.WriteLine($"[CSRepVanguard] {playerName} ({steamId}) → sin registro en DB, consultará API.");
            else
                Console.WriteLine($"[CSRepVanguard] {playerName} ({steamId}) → registro en DB: TrustRating={record.TrustRating:F2}, LastChecked={record.LastChecked:u}, IsBanned={record.IsBanned}");

            // ── Caso 1: jugador ya registrado y baneado → reaplicar ban inmediatamente ──
            // No se consulta la API ni se comprueba el cooldown: el ban ya fue dictaminado.
            if (record is not null && record.IsBanned)
            {
                Console.WriteLine($"[CSRepVanguard] {playerName} ({steamId}) está marcado como baneado en DB. Reaplicando ban sin consultar la API.");
                ExecuteBan(steamId, playerName, record.TrustRating);
                return;
            }

            // ── Caso 2: no está baneado → verificar si el cooldown expiró ────────────
            bool needsApiQuery = record is null ||
                (DateTime.UtcNow - record.LastChecked).TotalDays >= Config.QueryCooldownDays;

            Console.WriteLine($"[CSRepVanguard] {playerName} ({steamId}) → needsApiQuery={needsApiQuery} (CooldownDays={Config.QueryCooldownDays})");

            double trustRating;

            if (needsApiQuery)
            {
                Console.WriteLine($"[CSRepVanguard] Consultando API para {playerName} ({steamId})...");

                var result = await _api.GetTrustRatingAsync(steamId);
                if (result is null)
                {
                    // Si la API falla, no banear: dar beneficio de la duda.
                    Console.WriteLine($"[CSRepVanguard] No se pudo obtener Trust Rating para {playerName} ({steamId}). Se omite el baneo.");
                    return;
                }

                trustRating = result.Value;
                bool willBan = trustRating < Config.MinTrustRating;

                Console.WriteLine($"[CSRepVanguard] API devolvió TrustRating={trustRating:F2} para {playerName} ({steamId}). Guardando en DB (willBan={willBan})...");
                await _db.UpsertPlayerRecordAsync(steamId, trustRating, willBan);
                Console.WriteLine($"[CSRepVanguard] DB actualizada para {playerName} ({steamId}) → TrustRating={trustRating:F2}");
            }
            else
            {
                // Usar el valor cacheado en la DB.
                trustRating = record!.TrustRating;
                Console.WriteLine($"[CSRepVanguard] Usando Trust Rating en caché para {playerName} ({steamId}): {trustRating:F2} (próxima consulta en {Config.QueryCooldownDays - (DateTime.UtcNow - record.LastChecked).TotalDays:F1} día/s)");
            }

            if (trustRating < Config.MinTrustRating)
            {
                ExecuteBan(steamId, playerName, trustRating);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error inesperado verificando a {playerName} ({steamId}): {ex.Message}");
        }
    }

    // ── Baneo ─────────────────────────────────────────────────────────────────

    private void ExecuteBan(string steamId, string playerName, double trustRating)
    {
        var command = Config.BanCommand
            .Replace("{steamid}", steamId)
            .Replace("{name}", playerName)
            .Replace("{trustrating}", trustRating.ToString("F2"));

        Server.NextFrame(() =>
        {
            Console.WriteLine($"[CSRepVanguard] *** BANEO *** {playerName} ({steamId}) | TrustRating={trustRating:F2} < {Config.MinTrustRating:F2} | Cmd: {command}");
            Server.ExecuteCommand(command);
        });
    }

    // ── Desbaneo ──────────────────────────────────────────────────────────────

    private void ExecuteUnban(string steamId)
    {
        var command = Config.UnbanCommand.Replace("{steamid}", steamId);
        // Server.ExecuteCommand requiere el hilo principal del juego → Server.NextFrame.
        Console.WriteLine($"[CSRepVanguard] Desbaneando {steamId}. Comando: {command}");
        Server.NextFrame(() => Server.ExecuteCommand(command));
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

            Console.WriteLine($"[CSRepVanguard] css_csrep unbanall → desbaneando {bannedPlayers.Count} jugador(es).");

            // Ejecutar el comando de desbaneo (cada llamada agenda su propio NextFrame).
            foreach (var r in bannedPlayers)
                ExecuteUnban(r.SteamId);

            // Actualizar la DB fuera del hilo del juego.
            await _db.UnbanAllPlayersAsync();

            var msg = $"[CSRepVanguard] {bannedPlayers.Count} jugador(es) desbaneados correctamente.";
            Console.WriteLine(msg);

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
            Console.WriteLine($"[CSRepVanguard] Error ejecutando css_csrep unbanall: {ex.Message}");
        }
    }
}
