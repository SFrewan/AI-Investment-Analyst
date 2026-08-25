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
