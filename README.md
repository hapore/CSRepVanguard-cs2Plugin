# CSRepVanguard

[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for CS2 that checks every player's **Trust Rating** when they connect to the server using the [csrep.gg](https://csrep.gg) API. If the rating is below the configured threshold, it automatically executes a ban command.

---

## Full Workflow

```
Player connects
        │
        ▼
Record in DB?  ──No──►  Query csrep.gg API
        │                          │
       Yes                         ▼
        │                  Save result to DB
        ▼                          │
is_banned = 1?                     │
   Yes ◄───────────────────────────┘
    │
    ▼
Execute BanCommand  ◄── (immediate exit, no API call)
        │
       No (is_banned = 0)
        │
        ▼
Has >= QueryCooldownDays passed since LastChecked?
       Yes                        No
        │                          │
        ▼                          ▼
Query csrep.gg API           Use DB cache
        │                          │
        └──────────────────────────┘
                    │
                    ▼
        TrustRating < MinTrustRating?
             │               │
            Yes              No
             │               │
             ▼               ▼
      Execute BanCommand   (no action)
```

### Step by Step

1. **Player connects** — On the `player_connect_full` event, the plugin ignores bots and GOTV and launches the check on a separate thread to avoid blocking the server.

2. **Database query** — The player's record is looked up by SteamID64 in the `{TablePrefix}players` table.

3. **Is the player already banned in DB?**
   - If `is_banned = 1` → `BanCommand` is executed **immediately**, without querying the API or checking the cooldown. The ban was already determined previously.

4. **Does the API need to be queried?** (only if `is_banned = 0`)
   - If **no record exists** → always query.
   - If a record exists but `(now - LastChecked) >= QueryCooldownDays` → query again.
   - If the cooldown has not expired → use the `TrustRating` stored in DB (no API call).

5. **API call** — `GET https://csrep.gg/api/players?ids={steamId64}` with the `x-api-key` header. If the API fails or does not respond, the player is **not banned** (benefit of the doubt).

6. **Persistence** — The result is saved/updated in the DB with the current timestamp and the `is_banned` flag.

7. **Ban decision** — If `TrustRating < MinTrustRating`, `BanCommand` is executed on the game thread via `Server.NextFrame()`.

---

## Installation

1. Build the project:
   ```bash
   dotnet build -c Release
   ```

2. Copy the generated `.dll` from `bin/Release/net8.0/` to:
   ```
   game/csgo/addons/counterstrikesharp/plugins/CSRepVanguard/CSRepVanguard.dll
   ```

3. Copy **all** `.dll` files from the output (not just the main one) to the plugin folder:
   ```
   addons/counterstrikesharp/plugins/CSRepVanguard/
   ├── CSRepVanguard.dll
   ├── MySqlConnector.dll
   ├── Dapper.dll
   └── (rest of output dlls)
   ```

4. Manually create the configuration file if the server does not have write permissions,
   or start the server once so CounterStrikeSharp generates it automatically.

   > **Note:** CounterStrikeSharp always names the file after the plugin's `ModuleName`.
   > The file will be called `CSRepVanguard.json`, not `config.json`.

---

## Configuration

File: `addons/counterstrikesharp/configs/plugins/CSRepVanguard/CSRepVanguard.json`

```json
{
  "ApiKey": "your-csrep.gg-api-key",
  "ApiBaseUrl": "https://csrep.gg/api/players",
  "MinTrustRating": 80.0,
  "BanCommand": "css_ban {steamid} 0 \"[CSRep] Insufficient Trust Rating ({trustrating})\"",
  "UnbanCommand": "css_unban {steamid}",
  "QueryCooldownDays": 1,
  "DatabaseConnectionString": "Server=localhost;Port=3306;Database=csrep;User=csrep_user;Password=CHANGE_ME;",
  "TablePrefix": "cs_rep_"
}
```

| Field | Type | Description |
|---|---|---|
| `ApiKey` | `string` | csrep.gg API key to authenticate queries. |
| `ApiBaseUrl` | `string` | Base URL of the endpoint. The SteamID64 is passed as `?ids=`. |
| `MinTrustRating` | `double` | Minimum Trust Rating. Players **below** this value are banned. |
| `BanCommand` | `string` | Command executed on ban. Supports `{steamid}`, `{name}` and `{trustrating}`. |
| `UnbanCommand` | `string` | Command executed on unban. Supports `{steamid}`. |
| `QueryCooldownDays` | `int` | Days that must pass before querying the API again for the same player. |
| `DatabaseConnectionString` | `string` | MySQL/MariaDB connection string. |
| `TablePrefix` | `string` | Table prefix. Default `cs_rep_` → table `cs_rep_players`. |

### `BanCommand` Placeholders

| Placeholder | Value |
|---|---|
| `{steamid}` | Player's SteamID64 |
| `{name}` | Player's Steam name |
| `{trustrating}` | Numeric value obtained from the API |

### `UnbanCommand` Placeholders

| Placeholder | Value |
|---|---|
| `{steamid}` | Player's SteamID64 |

---

## Admin Commands

| Command | Description |
|---|---|
| `css_csrep unbanall` | Unbans all players registered as banned in the DB. Executes `UnbanCommand` for each one and clears the `is_banned` flag in the table. Can be run from the server console or by an admin with permissions. |

---

## Database

The plugin automatically creates the following table on startup:

```sql
CREATE TABLE IF NOT EXISTS `cs_rep_players` (
    `steam_id`     VARCHAR(20)  NOT NULL,
    `trust_rating` DOUBLE       NOT NULL DEFAULT 0,
    `last_checked` DATETIME     NOT NULL,
    `is_banned`    TINYINT(1)   NOT NULL DEFAULT 0,
    PRIMARY KEY (`steam_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

When using a shared database (same `DatabaseConnectionString` across multiple servers) with a common `TablePrefix`, **all servers share the verification history**, avoiding redundant API queries when a player was already verified recently on another server.

---

## API Response

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

The plugin reads `result.trust_rating`. If `status` is not `"OK"` or the field does not exist, the check is skipped and the player is not banned.

---

## Dependencies

| Package | Usage |
|---|---|
| `CounterStrikeSharp.API` | CS2 plugin framework |
| `MySqlConnector` | Async driver for MySQL/MariaDB |
| `Dapper` | Micro-ORM for executing SQL queries |
