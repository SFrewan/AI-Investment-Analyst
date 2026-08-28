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
dotnet user-secrets set "ConnectionStrings:Primary" "Host=localhost;Database=ai_investment;Username=...;Password=..."
dotnet user-secrets list
```

Configuration precedence in ASP.NET Core means a user-secret overrides `appsettings.json`
in the Development environment without any code change.

### Local configuration overrides

`appsettings.Local.json` and `.env` are git-ignored (see `.gitignore`, Phase 0 section). Use
them for non-secret local convenience only; anything genuinely sensitive belongs in
user-secrets even locally, because a git-ignored file is one `git add -f` away from being
committed.

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

---

## 9. Incident: a database password was committed (2026-08-28)

**What happened.** `src/AI.Investment.Api/appsettings.json`,
`src/AI.Investment.Api/appsettings.Development.json` and `scripts/verify.ps1` each carried a
PostgreSQL connection string containing a real password. All three are tracked, and the repository
has a remote that had been pushed to, so the value reached GitHub. This is precisely the failure
section 1 of this document was written to prevent, in the phase after it was written.

**What it was.** A live credential, not a placeholder: the local PostgreSQL superuser password used
by the development database and by the `ai_investment_tests` database on the same server. Its blast
radius is that server. It is not a cloud credential and it is not a provider key, but it is real.

**What was done.**

1. The value was removed from all three tracked files. `appsettings.json` and
   `appsettings.Development.json` now carry an empty `Database:ConnectionString`, which fails
   `ValidateOnStart` at start-up rather than starting with a guess - the connection string belongs
   in user-secrets or the environment, and the options validation now says so loudly.
2. `scripts/verify.ps1` reads `AIINV_TEST_POSTGRES` from the environment, or from
   `scripts/verify.local.ps1`, which is git-ignored. `scripts/verify.local.example.ps1` is tracked,
   documents the shape, and contains no value.
3. `.gitignore` gained `scripts/verify.local.ps1` and `scripts/*.local.ps1`.
4. `scripts/secret-scan.ps1` scans every tracked file for credential-shaped patterns and writes
   `artifacts/verify/secret-scan.log`. It reports file and line and never the matched text, because
   a log that quotes the credential is a second copy of the problem.

**What was verified, by execution rather than by reading.**

`scripts/run-secret-scan.cmd` was run against the working tree on 2026-08-28. It scanned 355 tracked
files and reported **no credential findings**. Three matches were reported as allowed placeholders
(`docs/SECURITY.md` twice, `tests/AI.Investment.Api.Tests/ApiFactory.cs` once) and one further match,
`tests/AI.Investment.Application.UnitTests/Ingestion/IngestionGatewayTests.cs`, was inspected and
added to the allow-list: it is a fabricated `apikey=SECRET` inside the test asserting that a
provider's exception message is never copied into the ingestion ledger, so the literal is the thing
under test rather than a credential.

The same run searched history for commits touching a credential line in the three affected files and
found two: `a94b12c` ("first changes") and `8d0c8d0` ("phase3 still"). That is the exposure that
survives the working-tree fix, and it is why the next paragraph is the important one.

**What remains, and it is the important part.**

- **The value is still in git history and on the remote**, reachable from commits `a94b12c` and
  `8d0c8d0`. Removing it from the working tree does
  not un-disclose it. History was **not** rewritten, because rewriting a pushed branch is a
  destructive operation that requires the owner's explicit decision, not an assistant's.
- **The credential must be treated as compromised and rotated.** Rotating it is the step that
  actually ends the exposure; everything above only stops it recurring. Until it is rotated,
  anything that password protects should be considered reachable by anyone who has ever had read
  access to the repository.
- Once it is rotated, the history entry becomes a dead value and purging it is optional
  housekeeping (`git filter-repo`, or a fresh repository - the history is six commits).

**Setting the development connection string after this change**

```bash
cd src/AI.Investment.Api
dotnet user-secrets set "Database:ConnectionString" "Host=...;Database=ai_investment;Username=...;Password=..."
```

For the test suite, copy `scripts/verify.local.example.ps1` to `scripts/verify.local.ps1` and fill
in the value there. The integration fixture refuses any database whose name does not end in
`_tests`, so a mistyped value cannot be pointed at the development database.
