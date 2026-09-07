# Security Practices

**Status:** Phase 0 baseline. Expanded in later phases as the surface area grows.
**Scope:** how this repository and the running system handle secrets, credentials and untrusted input.

---

## 1. The rule that matters most right now

**No secret is stored in source control. Ever. Including in `appsettings.json`.**

This repository currently contains **zero** secrets, and that is precisely why the mechanism is
being established now. The first market-data, news or AI provider API key acquired will —
without a mechanism already in place — be pasted into a tracked configuration file and pushed.
That failure is permanent: rotating the key is necessary but the value stays in git history.

---

## 2. Where secrets live

| Environment | Mechanism | Notes |
|---|---|---|
| Local development | .NET **user-secrets** | Stored outside the repository in the user profile. The API project declares a `UserSecretsId`. |
| CI | GitHub Actions **secrets** | Injected as environment variables for the job that needs them. Never echoed. |
| Production | Environment variables or a managed secret store | Chosen when the first deployment target is chosen. |

### Setting a development secret

```bash
cd src/AI.Investment.Api
dotnet user-secrets init          # only needed once; the UserSecretsId is already in the .csproj
dotnet user-secrets set "Database:ConnectionString" "Host=localhost;Port=5432;Database=ai_investment;Username=...;Password=..."
dotnet user-secrets list
```

`Database:ConnectionString` is the key the application actually binds - see
`DatabaseOptions.SectionName`. An earlier revision of this document named
`ConnectionStrings:Primary`, which nothing reads; a developer who followed it still met
"The ConnectionString field is required" at start-up.

Configuration precedence in ASP.NET Core means a user-secret overrides `appsettings.json`
in the Development environment without any code change.

Full first-run instructions, including the database and the migration step, are in
[LOCAL-DEVELOPMENT.md](LOCAL-DEVELOPMENT.md).

### Local configuration overrides

`appsettings.Local.json` and `.env` are git-ignored (see `.gitignore`, Phase 0 section). Use
them for non-secret local convenience only; anything genuinely sensitive belongs in
user-secrets even locally, because a git-ignored file is one `git add -f` away from being
committed.

---

## 2a. The EODHD API token

The first real vendor credential this repository has had to hold. It authenticates both EODHD
connectors — end-of-day prices and corporate actions — because they are one subscription.

| | |
|---|---|
| Configuration path | `Providers:Eodhd:ApiKey` |
| Environment variable | `Providers__Eodhd__ApiKey` |
| Bound by | `EodhdOptions` (`SectionName` + the property name) |
| Read by | `EodhdProvider`, `EodhdSplitsProvider`, through `IOptions<EodhdOptions>` |

Both names are **derived in code** from `EodhdOptions.SectionName`, and exposed as
`EodhdOptions.ApiKeyPath` and `EodhdOptions.ApiKeyEnvironmentVariable`. Nothing — not a
validation message, not a connector's refusal, not this document's instructions in the
`appsettings` comment blocks — writes them out by hand any more. A name that appears in prose
does not get renamed with the property, and an operator following a stale instruction sets a
variable nothing reads, which presents as a missing credential rather than a misspelt one.

### Setting it on Windows

A **user** environment variable, not a machine one: it is then readable by this account and by
nothing else on the box, and it needs no elevation.

```powershell
# Prompts, and never echoes. The value is not stored in your PowerShell history.
.\scripts\set-eodhd-credential.ps1
```

Or by hand, if you prefer to see the mechanism:

```powershell
$token = Read-Host -AsSecureString "EODHD API token"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($token))
[Environment]::SetEnvironmentVariable('Providers__Eodhd__ApiKey', $plain, 'User')
Remove-Variable plain
```

**Do not use `setx`** for this. It writes the value into the command line, which means the
console history and, on some systems, the process-creation audit log. `SetEnvironmentVariable`
does not.

**A variable set this way is not visible to the shell that set it.** Close the terminal — and
any editor or IDE started before it was set — and open a new one. Every "the token is set but
the application cannot see it" report is this.

### Checking it reached the application

```powershell
.\scripts\check-eodhd-credential.cmd
```

Boots the same composition the backfill runs under and reports four things: whether a
credential arrived, its length, a domain-separated fingerprint, and **which configuration
provider supplied it**. It makes no provider calls and never prints the value. The report is
written to `artifacts/verify/credential.md`, which is git-ignored, and contains no credential.

The fingerprint is the part worth understanding. It is the first twelve hex characters of a
SHA-256 over a constant domain string and the credential. Two machines can compare fingerprints
to learn whether they hold the same token without either disclosing it, and the domain string
means a fingerprint published here cannot be checked against a digest computed anywhere else.

### Stored vs. inherited — the failure that looks like a missing variable

A Windows user environment variable lives in the per-user registry. **A process never reads it
from there.** It receives a copy of its parent's environment block when it starts, and that copy
is a snapshot of whatever the parent held. Explorer refreshes its own block when it processes
the broadcast that follows a change — *when* it processes it. Everything it launches afterwards
inherits the refreshed block; if it did not refresh, everything it launches inherits the stale
one, indefinitely.

That produces two true statements that contradict each other on screen:

```powershell
# Reads the registry. Says the variable is set.
[Environment]::GetEnvironmentVariable('Providers__Eodhd__ApiKey', 'User')

# Reads this process's inherited block. Says it is not.
$env:Providers__Eodhd__ApiKey
```

`WindowsUserEnvironment` closes it by reading the stored value directly, as a configuration
source registered **below** `AddEnvironmentVariables` — so a value in the process block still
wins, and setting a variable for one run overrides the stored one exactly as it always did. It
supplies an allow-list of one path, the credential; mirroring the whole user environment would
let any variable named after a settings path bind on a machine whose owner never asked for that.

Signing out and back in still fixes the underlying staleness, and is worth doing. Nothing
depends on it any more.

`check-eodhd-credential.cmd` reports **stored** and **inherited** as separate rows, and when
neither has a value it lists the per-user variable *names* beginning with `Providers` — names
only, never values — because a variable set under a nearly-right name is otherwise
indistinguishable from no variable at all.

### Precedence, and the one place it was wrong

Later configuration sources override earlier ones. The host's order is `appsettings.json`, then
`appsettings.{Environment}.json`, then user-secrets, then environment variables — so an
environment variable beats a user-secret, which is what makes it the deployment mechanism.

`BackfillApiFactory`, the fixture the billable backfill and the acquisition dry run both boot,
appended `AddUserSecrets` after the host's own sources, which reversed that for exactly this
setting. A stale token in a developer's secrets store would have silently outranked the
environment variable an operator had just set, spent the subscription against the wrong account,
and reported a 401 naming neither. It now re-adds the environment afterwards, and
`EodhdCredentialSourcingTests` pins the rule.

### What is asserted, and what never is

`EodhdCredentialSourcingTests` and `CredentialHygieneTests` prove the credential resolves, is
refused when blank or whitespace-padded, and appears in no tracked configuration file. **No test
compares a credential to an expected value and none writes one to test output.** Everything goes
through `CredentialPresence`, which reports presence, length and fingerprint. The values in the
test suite are synthetic, so the rule costs nothing there; it is kept because
`check-eodhd-credential` points the same code at a real installation.

### If the token is disclosed

Rotate it at EODHD first — see section 4. A monthly subscription's token is a billing
credential as well as a data one.

---

## 3. Repository settings to enable on GitHub

These are settings, not code, and must be enabled in the GitHub UI. Recorded here because a
control nobody wrote down is a control nobody re-checks.

- [ ] Repository visibility is **Private**.
- [ ] **Secret scanning** enabled.
- [ ] **Push protection** enabled — this blocks a commit containing a recognised credential
      at push time, which is the only point at which the mistake is still cheap.
- [ ] **Dependabot alerts** and security updates enabled.
- [ ] Branch protection on `master`: require the CI workflow to pass, require a pull request.

CI additionally runs a secret scan and a vulnerable-dependency check on every push and pull
request, so the protection does not depend solely on repository settings being correct.

---

## 4. If a secret is committed anyway

1. **Revoke and rotate the credential first.** Removing it from git history does not
   un-disclose it. Treat it as compromised from the moment it was pushed.
2. Then purge it from history (`git filter-repo`, or a fresh repository if the history is
   short — as it is today).
3. Record what happened and why the existing controls did not catch it.

---

## 5. Authentication and authorization

**Current state: the API has no authentication, and this is deliberate and temporary.**

The pre-Phase-0 solution called `app.UseAuthorization()` with no authentication scheme
registered — a no-op that reads as security in a code review (audit finding F-03). Phase 0
removed the decorative call rather than leaving it in place.

Planned, in order:

- Real authentication (OIDC / JWT bearer).
- Policy-based authorization, `[Authorize]` by default with explicit opt-out.
- **Step-up authentication on approval endpoints** — approving an action that commits capital
  must not be reachable with the same session that reads a dashboard.

Until authentication exists, **the API must not be exposed beyond localhost.**

---

## 6. Untrusted input

Everything fetched from outside the system — news articles, regulatory filings, marketplace
listings, supplier pages, provider API responses — is treated as adversarial input:

- It is labelled and delimited as *data* on the way into any model.
- **Agent output is data, never execution authority.** It cannot trigger a side effect
  directly; it can only produce an `ActionProposal` that the deterministic `PolicyEngine`
  then judges. This is the structural defence against prompt injection, and it is enforced
  by the Action/Policy seam introduced in Phase 1.
- Agent output is never rendered as HTML without sanitisation.

---

## 7. Separation of planes

Analytical code and execution code are separate processes with separate identities. Only the
execution process will ever hold venue credentials, and it re-validates limits and the kill
switch itself rather than trusting its caller. **The analysis plane cannot move money even if
fully compromised, because it does not hold the capability.**

No execution plane exists yet. No broker or venue credential exists yet. Neither is in scope
before the gate described in the roadmap.

---

## 8. Supply chain

- Central Package Management (`Directory.Packages.props`) — one pinned version per package.
- CI runs `dotnet list package --vulnerable --include-transitive` and fails on any finding.
- New packages require a stated reason, recorded in `Directory.Packages.props` and in the
  phase implementation report.
