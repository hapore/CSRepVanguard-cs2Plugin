# CSRepVanguard

Plugin de [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) para CS2 que verifica el **Trust Rating** de cada jugador al conectarse al servidor usando la API de [csrep.gg](https://csrep.gg). Si el rating está por debajo del umbral configurado, ejecuta automáticamente un comando de baneo.

---

## Workflow completo

```
Jugador se conecta
        │
        ▼
¿Registro en DB?  ──No──►  Consultar API csrep.gg
        │                          │
       Sí                          ▼
        │                  Guardar resultado en DB
        ▼                          │
¿is_banned = 1?                    │
   Sí ◄────────────────────────────┘
    │
    ▼
Ejecutar BanCommand  ◄── (salida inmediata, sin llamar a la API)
        │
       No (is_banned = 0)
        │
        ▼
¿Pasaron >= QueryCooldownDays desde LastChecked?
       Sí                         No
        │                          │
        ▼                          ▼
Consultar API csrep.gg       Usar caché DB
        │                          │
        └──────────────────────────┘
                    │
                    ▼
        ¿TrustRating < MinTrustRating?
             │               │
            Sí               No
             │               │
             ▼               ▼
      Ejecutar BanCommand   (sin acción)
```

### Paso a paso

1. **Conexión del jugador** — Al recibir el evento `player_connect_full`, el plugin ignora bots y GOTV y lanza la verificación en un hilo separado para no bloquear el servidor.

2. **Consulta a la base de datos** — Se busca el registro del jugador por SteamID64 en la tabla `{TablePrefix}players`.

3. **¿El jugador ya está baneado en DB?**
   - Si `is_banned = 1` → se ejecuta `BanCommand` **inmediatamente**, sin consultar la API ni comprobar el cooldown. El ban ya fue dictaminado previamente.

4. **¿Se necesita consultar la API?** (solo si `is_banned = 0`)
   - Si **no existe registro** → consultar siempre.
   - Si existe pero `(ahora - LastChecked) >= QueryCooldownDays` → consultar de nuevo.
   - Si el cooldown no ha expirado → usar el `TrustRating` guardado en DB (sin llamada a la API).

5. **Llamada a la API** — `GET https://csrep.gg/api/players?ids={steamId64}` con el header `x-api-key`. Si la API falla o no responde, **no se banea** al jugador (beneficio de la duda).

6. **Persistencia** — El resultado se guarda/actualiza en la DB con el timestamp actual y el flag `is_banned`.

7. **Decisión de baneo** — Si `TrustRating < MinTrustRating`, se ejecuta `BanCommand` en el hilo del juego via `Server.NextFrame()`.

---

## Instalación

1. Compilar el proyecto:
   ```bash
   dotnet build -c Release
   ```

2. Copiar el `.dll` generado en `bin/Release/net8.0/` a:
   ```
   game/csgo/addons/counterstrikesharp/plugins/CSRepVanguard/CSRepVanguard.dll
   ```

3. Copiar **todos** los `.dll` del output (no solo el principal) a la carpeta del plugin:
   ```
   addons/counterstrikesharp/plugins/CSRepVanguard/
   ├── CSRepVanguard.dll
   ├── MySqlConnector.dll
   ├── Dapper.dll
   └── (resto de dlls del output)
   ```

4. Crear manualmente el archivo de configuración si el servidor no tiene permisos de escritura,
   o arrancar el servidor una vez para que CounterStrikeSharp lo genere automáticamente.

   > **Nota:** CounterStrikeSharp siempre nombra el archivo según el `ModuleName` del plugin.
   > El archivo se llamará `CSRepVanguard.json`, no `config.json`.

---

## Configuración

Archivo: `addons/counterstrikesharp/configs/plugins/CSRepVanguard/CSRepVanguard.json`

```json
{
  "ApiKey": "tu-api-key-de-csrep.gg",
  "ApiBaseUrl": "https://csrep.gg/api/players",
  "MinTrustRating": 80.0,
  "BanCommand": "css_ban {steamid} 0 \"[CSRep] Trust Rating insuficiente ({trustrating})\"",
  "UnbanCommand": "css_unban {steamid}",
  "QueryCooldownDays": 1,
  "DatabaseConnectionString": "Server=localhost;Port=3306;Database=csrep;User=csrep_user;Password=CHANGE_ME;",
  "TablePrefix": "cs_rep_"
}
```

| Campo | Tipo | Descripción |
|---|---|---|
| `ApiKey` | `string` | API key de csrep.gg para autenticar las consultas. |
| `ApiBaseUrl` | `string` | URL base del endpoint. El SteamID64 se pasa como `?ids=`. |
| `MinTrustRating` | `double` | Trust Rating mínimo. Jugadores **por debajo** de este valor son baneados. |
| `BanCommand` | `string` | Comando ejecutado al banear. Soporta `{steamid}`, `{name}` y `{trustrating}`. |
| `UnbanCommand` | `string` | Comando ejecutado al desbanear. Soporta `{steamid}`. |
| `QueryCooldownDays` | `int` | Días que deben pasar antes de volver a consultar la API por el mismo jugador. |
| `DatabaseConnectionString` | `string` | Cadena de conexión MySQL/MariaDB. |
| `TablePrefix` | `string` | Prefijo de las tablas. Default `cs_rep_` → tabla `cs_rep_players`. |

### Placeholders de `BanCommand`

| Placeholder | Valor |
|---|---|
| `{steamid}` | SteamID64 del jugador |
| `{name}` | Nombre en Steam del jugador |
| `{trustrating}` | Valor numérico obtenido de la API |

### Placeholders de `UnbanCommand`

| Placeholder | Valor |
|---|---|
| `{steamid}` | SteamID64 del jugador |

---

## Comandos de administración

| Comando | Descripción |
|---|---|
| `css_csrep unbanall` | Desbanea a todos los jugadores registrados como baneados en la DB. Ejecuta `UnbanCommand` por cada uno y limpia el flag `is_banned` en la tabla. Puede ejecutarse desde consola del servidor o por un admin con permisos. |

---

## Base de datos

El plugin crea automáticamente la siguiente tabla al arrancar:

```sql
CREATE TABLE IF NOT EXISTS `cs_rep_players` (
    `steam_id`     VARCHAR(20)  NOT NULL,
    `trust_rating` DOUBLE       NOT NULL DEFAULT 0,
    `last_checked` DATETIME     NOT NULL,
    `is_banned`    TINYINT(1)   NOT NULL DEFAULT 0,
    PRIMARY KEY (`steam_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

Al usar una base de datos compartida (mismo `DatabaseConnectionString` en varios servidores) y un `TablePrefix` común, **todos los servidores comparten el historial de verificaciones**, evitando consultas redundantes a la API cuando el jugador ya fue verificado recientemente en otro servidor.

---

## Respuesta de la API

```json
{
  "status": "OK",
  "result": {
    "id": "76561199015845161",
    "name": "GC | Foxx",
    "trust_rating": 100,
    "bans": {
      "vac": false,
      "game": false,
      "faceit": false
    }
  }
}
```

El plugin lee `result.trust_rating`. Si `status` no es `"OK"` o el campo no existe, la verificación se omite y no se banea al jugador.

---

## Dependencias

| Paquete | Uso |
|---|---|
| `CounterStrikeSharp.API` | Framework de plugins para CS2 |
| `MySqlConnector` | Driver asíncrono para MySQL/MariaDB |
| `Dapper` | Micro-ORM para ejecutar queries SQL |
