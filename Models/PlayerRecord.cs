namespace CSRepVanguard.Models;

/// <summary>
/// Registro almacenado en la base de datos para cada jugador verificado.
/// La tabla puede ser compartida entre múltiples servidores.
/// </summary>
public class PlayerRecord
{
    /// <summary>SteamID64 del jugador (ej. "76561198xxxxxxxxx").</summary>
    public string SteamId { get; set; } = string.Empty;

    /// <summary>Último valor de Trust Rating obtenido desde la API.</summary>
    public double TrustRating { get; set; }

    /// <summary>Momento UTC en que se realizó la última consulta a la API.</summary>
    public DateTime LastChecked { get; set; }

    /// <summary>Indica si el jugador fue baneado por esta verificación.</summary>
    public bool IsBanned { get; set; }
}
