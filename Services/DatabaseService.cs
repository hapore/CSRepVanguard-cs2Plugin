using CSRepVanguard.Models;
using Dapper;
using MySqlConnector;

namespace CSRepVanguard.Services;

/// <summary>
/// Gestiona la persistencia de registros de jugadores en MySQL/MariaDB.
/// La tabla cs_rep_players es compartida entre servidores.
/// </summary>
public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _tablePlayers;

    public DatabaseService(PluginConfig config)
    {
        _connectionString = config.DatabaseConnectionString;
        _tablePlayers = $"`{config.TablePrefix}players`";
    }

    // ── Inicialización ────────────────────────────────────────────────────────

    /// <summary>
    /// Crea la tabla si no existe. Se llama al cargar el plugin.
    /// </summary>
    public async Task InitializeAsync()
    {
        var sql = $"""
            CREATE TABLE IF NOT EXISTS {_tablePlayers} (
                `steam_id`     VARCHAR(20)  NOT NULL,
                `trust_rating` DOUBLE       NOT NULL DEFAULT 0,
                `last_checked` DATETIME     NOT NULL,
                `is_banned`    TINYINT(1)   NOT NULL DEFAULT 0,
                PRIMARY KEY (`steam_id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.ExecuteAsync(sql);
            Console.WriteLine($"[CSRepVanguard] Tabla {_tablePlayers} lista.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error al inicializar la base de datos: {ex.Message}");
            throw;
        }
    }

    // ── Lectura ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Obtiene el registro de un jugador por SteamID64.
    /// Devuelve null si el jugador nunca fue registrado.
    /// </summary>
    public async Task<PlayerRecord?> GetPlayerRecordAsync(string steamId)
    {
        var sql = $"""
            SELECT steam_id   AS SteamId,
                   trust_rating AS TrustRating,
                   last_checked AS LastChecked,
                   is_banned    AS IsBanned
            FROM {_tablePlayers}
            WHERE steam_id = @SteamId
            LIMIT 1;
            """;

        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            return await conn.QuerySingleOrDefaultAsync<PlayerRecord>(sql, new { SteamId = steamId });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error al obtener registro para {steamId}: {ex.Message}");
            return null;
        }
    }

    // ── Escritura ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserta o actualiza el registro del jugador con el Trust Rating obtenido.
    /// </summary>
    public async Task UpsertPlayerRecordAsync(string steamId, double trustRating, bool isBanned = false)
    {
        var sql = $"""
            INSERT INTO {_tablePlayers} (steam_id, trust_rating, last_checked, is_banned)
            VALUES (@SteamId, @TrustRating, @LastChecked, @IsBanned)
            ON DUPLICATE KEY UPDATE
                trust_rating = VALUES(trust_rating),
                last_checked = VALUES(last_checked),
                is_banned    = VALUES(is_banned);
            """;

        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.ExecuteAsync(sql, new
            {
                SteamId = steamId,
                TrustRating = trustRating,
                LastChecked = DateTime.UtcNow,
                IsBanned = isBanned ? 1 : 0
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error al guardar registro para {steamId}: {ex.Message}");
        }
    }

    // ── Consultas masivas ─────────────────────────────────────────────────────

    /// <summary>
    /// Devuelve todos los registros con is_banned = 1.
    /// Se usa al arrancar el servidor para reaplicar bans.
    /// </summary>
    public async Task<IEnumerable<PlayerRecord>> GetAllBannedPlayersAsync()
    {
        var sql = $"""
            SELECT steam_id   AS SteamId,
                   trust_rating AS TrustRating,
                   last_checked AS LastChecked,
                   is_banned    AS IsBanned
            FROM {_tablePlayers}
            WHERE is_banned = 1;
            """;

        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            return await conn.QueryAsync<PlayerRecord>(sql);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error al obtener jugadores baneados: {ex.Message}");
            return Enumerable.Empty<PlayerRecord>();
        }
    }

    /// <summary>
    /// Establece is_banned = 0 y resetea last_checked a una fecha antigua en todos los
    /// registros, forzando una consulta fresca a la API en la próxima conexión de cada jugador.
    /// Se llama desde el comando css_csrep unbanall.
    /// </summary>
    public async Task UnbanAllPlayersAsync()
    {
        var sql = $"""
            UPDATE {_tablePlayers}
            SET is_banned    = 0,
                last_checked = '2000-01-01 00:00:00';
            """;

        try
        {
            await using var conn = new MySqlConnection(_connectionString);
            await conn.ExecuteAsync(sql);
            Console.WriteLine("[CSRepVanguard] Todos los registros marcados como desbaneados en DB.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CSRepVanguard] Error al desbanear todos los jugadores en DB: {ex.Message}");
        }
    }
}
