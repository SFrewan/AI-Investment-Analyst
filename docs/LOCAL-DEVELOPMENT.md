# Running AI Investment Analyst locally

From a fresh Windows development machine to a running API and an operator console.

**This is a setup document.** It changes nothing about how the platform behaves. Autonomy is L3,
live execution is structurally unavailable, and nothing here alters either.

---

## 1. Prerequisites

| | |
| --- | --- |
| .NET SDK | 8.0 |
| PostgreSQL | 14 or later, reachable from this machine |
| Visual Studio 2022 (optional) | 17.8+, or `dotnet` on the command line |
| EF Core tools | `dotnet tool install --global dotnet-ef` (once per machine) |

HTTPS on the `https` launch profile needs a trusted development certificate. Once per machine:

```powershell
dotnet dev-certs https --trust
```

---

## 2. The one thing that stops a fresh clone from starting

```
Microsoft.Extensions.Options.OptionsValidationException:
DataAnnotation validation failed for 'DatabaseOptions' members: 'ConnectionString' ...
```

This is correct behaviour, not a defect. `Database:ConnectionString` is a **secret**, so it is
shipped empty in both `appsettings.json` and `appsettings.Development.json`, and
`DatabaseOptions` is registered with `ValidateOnStart()` — a misconfigured host fails at start-up
rather than on the first request that happens to read the setting, which once background
processing exists could be hours later and on a different machine.

**Supply the value out of source control.** It is never committed, in any environment.

---

## 3. Create the database

The application does not create its own database. Any PostgreSQL instance will do; the name below
is a convention, not a requirement.

```powershell
psql -U postgres -c "CREATE DATABASE ai_investment;"
```

| Setting | Value used by this document |
| --- | --- |
| Host | `localhost` |
| Port | `5432` |
| Database | `ai_investment` |
| Username | whatever your PostgreSQL installation uses |
| Password | **yours. It is not in this repository, and no default is supplied here.** |

The integration test suite is unaffected by any of this: it starts its own PostgreSQL container.

---

## 4. Configure the connection string

**Preferred, and the mechanism this project already declares** — user-secrets. The API project
carries a `UserSecretsId`, and `WebApplication.CreateBuilder` loads the store automatically in the
Development environment.

```powershell
cd src\AI.Investment.Api
dotnet user-secrets set "Database:ConnectionString" "Host=localhost;Port=5432;Database=ai_investment;Username=<user>"
dotnet user-secrets list
```

**Append your own password to that connection string.** Npgsql needs a `Password` key, and the
example above deliberately stops short of it: this file is committed, and a committed document that
spells out a credential assignment — even with a placeholder in it — is a document the repository's
secret scan is right to flag. Add the key yourself, with your value, when you run the command.

The store lives in `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`, outside the
repository. Nothing you type above reaches source control.

**Alternative** — an environment variable, which works identically at runtime and is what a
deployment uses:

```powershell
$env:Database__ConnectionString = "Host=localhost;Port=5432;Database=ai_investment;Username=<user>"
```

Double underscore, not a colon: that is how ASP.NET Core spells section separators in environment
variables. The same `Password` key has to be appended here too, and is omitted here for the same
reason.

**Do not** put it in `appsettings.json` or `appsettings.Development.json`. Both are committed. If
you want a git-ignored file for non-secret local convenience, `appsettings.Local.json` is already
ignored — but a secret in a git-ignored file is one `git add -f` away from being committed, which
is how the credential recorded in `SECURITY.md` §9 got into this repository's history.

---

## 5. Apply migrations

Migrations already exist through Phase 8. Use the existing mechanism; **do not create a new
migration** — nothing in local setup changes the schema.

```powershell
dotnet ef database update --project src\AI.Investment.Infrastructure --startup-project src\AI.Investment.Api
```

`DesignTimeDbContextFactory` resolves the connection string the same way the running host does —
`AIINV_DESIGNTIME_DB` first as an explicit override, then environment variables, then user-secrets,
then `appsettings.{Environment}.json`, then `appsettings.json` — so once step 4 is done this needs
no extra configuration.

---

## 6. Run

```powershell
dotnet run --project src\AI.Investment.Api --launch-profile https
```

In Visual Studio: set **AI.Investment.Api** as the startup project and pick the **https** profile.

| | |
| --- | --- |
| HTTPS | `https://localhost:44367` |
| HTTP | `http://localhost:5143` |
| Swagger (Development only) | `https://localhost:44367/swagger` |
| Liveness | `https://localhost:44367/health/live` |
| Readiness, including PostgreSQL | `https://localhost:44367/health/ready` |

`/health/live` answers as soon as the host is up. `/health/ready` reports **Unhealthy** until
PostgreSQL is reachable *and* migrated — it is the quickest confirmation that steps 3 to 5 worked.

---

## 7. Open the operator console

```
https://localhost:44367/
```

The root serves `wwwroot/index.html` through the static-file middleware — no separate frontend
build, no npm, no dev server.

**It is a minimum operator console, not an analytics frontend.** It reads the existing read models
— health, cycles, escalations, opportunities, shadow decisions, autonomy grants, promotion state,
freshness, ingestion runs, the capital ledger, validation results, sources — and offers the five
operator actions that exist. There are no charts, no portfolio view and no backtest explorer.

**Read endpoints are anonymous; every operator action is not.** To act, you need a key.

### Giving yourself an operator key

Nothing authenticates by default: `Operators:Accounts` ships empty, and empty means nobody. Choose
a key, hash it, and configure the hash — the key itself is never stored anywhere.

```powershell
# Pick a key. Do not reuse a password.
$key = "<choose something long and random>"

# Hash it.
[BitConverter]::ToString(
  [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($key))
).Replace("-","").ToLower()
```

Then add the account through user-secrets, so nothing about it is committed:

```powershell
cd src\AI.Investment.Api
dotnet user-secrets set "Operators:Accounts:0:Id" "you@example.local"
dotnet user-secrets set "Operators:Accounts:0:DisplayName" "Your Name"
dotnet user-secrets set "Operators:Accounts:0:KeySha256" "<the 64-character hash printed above>"
dotnet user-secrets set "Operators:Accounts:0:Privileges:0" "DecideOpportunities"
dotnet user-secrets set "Operators:Accounts:0:Privileges:1" "AnswerEscalations"
dotnet user-secrets set "Operators:Accounts:0:Privileges:2" "AdministerKillSwitch"
dotnet user-secrets set "Operators:Accounts:0:Privileges:3" "AdministerWatches"
```

Grant only the privileges you need. Paste the **key** — not the hash — into the console page; it is
held for the browser session only and sent as `X-Operator-Key`. `GET /api/operator/whoami` confirms
the platform recognises you.

Full description of the surface, including what it deliberately cannot do:
[Blocks/BLOCK-1-OPERATOR-SURFACE.md](Blocks/BLOCK-1-OPERATOR-SURFACE.md).

---

## 8. Common start-up errors

| Message | Cause | Fix |
| --- | --- | --- |
| `DataAnnotation validation failed for 'DatabaseOptions'` | No connection string. | Step 4. The message itself names the three mechanisms. |
| `Npgsql.NpgsqlException: ... 28P01` | Wrong username or password. | Re-run the user-secrets command with the right credentials. |
| `... 3D000: database "ai_investment" does not exist` | Database not created. | Step 3, or point the connection string at one that exists. |
| `relation "..." does not exist` | Migrations not applied. | Step 5. |
| `/health/ready` is Unhealthy, `/health/live` is Healthy | The host is up; PostgreSQL is not reachable or not migrated. | Steps 3 to 5. |
| Browser warns about the certificate | No trusted development certificate. | `dotnet dev-certs https --trust`. |
| Every operator action returns 401 | No operators configured, or the wrong key pasted. | Step 7. Empty means nobody, by design. |
| An operator action returns 403 | Authenticated, but without that privilege. | Add the privilege in step 7. |
| An operator action returns 409 | The policy engine denied it, or the kill switch is engaged. | Not a configuration problem. The response body says which. |

`Operators` is deliberately **not** `ValidateOnStart`: a malformed account authenticates nobody
rather than stopping a host that is otherwise serving read traffic and health checks.

---

## 9. What running locally does not do

Starting the API does not start the operating loop. `OperationsHost:RunCycles` is `false` by
default — unattended operation does not begin because a host happened to start, and that is the
loop that proposes actions.

There is no market data. `Providers:PriceHistory` is disabled and has no directory; the platform
will not fabricate prices. Activating the observation window is described in
[Blocks/BLOCK-1-OPERATOR-SURFACE.md](Blocks/BLOCK-1-OPERATOR-SURFACE.md) §4 and needs a licensed
export this repository does not contain.
