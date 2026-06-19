# MAIA Auth — Enforcement Cutover Runbook

**Audience:** whoever deploys the build that turns on authorization enforcement (Phases 2+3).
**Why this exists:** today operators use MAIA anonymously — **no user accounts exist**. The
moment enforcement goes live, the *only* credential that works is the seeded admin, and the
account-management UI/API only exists from this same release. Unmanaged, the morning of the
cutover reads as "MAIA is down." Treat it as a **planned maintenance window**, not a hot deploy.

---

## The unavoidable sequence

Account provisioning **cannot** be done before the deploy (UsersController ships *in* this
release). So the order is fixed:

1. **Deploy** the backend (enforcement on) + frontend (login) together — see "Why together" below.
2. **Admin logs in** with the seeded credentials and is **forced to rotate** the password
   (the seeded admin has `MustChangePassword = true`; the app routes to `/change-password`
   before anything else works).
3. **Admin provisions accounts** — `POST /api/users` (or the Users admin screen when built)
   for each operator/user, assigning roles. Each new account is born with
   `MustChangePassword = true`, so its owner rotates on first login.
4. **Everyone else can now log in.** Distribute credentials (see Comms).

Until step 3 completes, non-admins genuinely cannot use the system. That is expected — plan
the window so it's short and announced, not discovered.

---

## Default seeded admin

| | |
|---|---|
| Username | `admin` |
| Password | `ChangeMe!2026` |
| Role | Administrator |
| Forced rotation | Yes (`MustChangePassword = true`) |

Seeded by migration `AddAuthTables` (raw SQL insert with a pre-computed PBKDF2 hash). The
default credential is only a bootstrap — the forced rotation at first login is what keeps it
from persisting. **Rotate it immediately in step 2.**

> **Create a second administrator in step 3.** With a single admin, a lost admin password has
> no in-app recovery (no email reset in v1). A second admin is your recovery path.

---

## Why deploy backend + frontend together

The API is the security boundary; the SPA login is how humans reach it. Two ordering rules
fall out, and the only safe way to satisfy both is a **single combined deploy**:

- **(a)** Backend enforcement cannot precede frontend login — every request (including the
  page load) would 401 before there's a way to log in.
- **(b)** Frontend role-gating must never precede backend enforcement — it would imply
  protection that isn't there.

If a staged rollout is unavoidable, the only acceptable gap is a **short, monitored window**
where known, audited accounts could over-reach (authenticated-but-not-yet-role-gated). Never
ship frontend role-gating ahead of backend enforcement. Enforcement only ever *adds* — no
runtime flag turns it back off.

---

## Pre-cutover gate (must be green)

- **Authorization matrix test** — `AuthorizationMatrixTests` drives the real pipeline
  (`WebApplicationFactory`) across the *entire* route inventory: every Admin write → 403 for
  Operator and User, every Operator action → 403 for User, anonymous → 401 everywhere except
  `login`/`me`/`health`. **Green is the go/no-go.** A missed `[Authorize]` attribute is
  invisible in normal use (the fallback policy is default-*authenticated*, not default-admin —
  a forgotten attribute silently lets any logged-in user through), so this matrix is the only
  thing that catches it. Re-run on the release build before cutover.
- Full backend suite green.
- Frontend builds clean (`ng build`).

---

## Deploy-time configuration

`appsettings.json` → `Auth` section:

| Key | Dev | Production |
|---|---|---|
| `CookieSecure` | `false` (http://localhost) | **`true`** (HTTPS) |
| `IdleTimeoutSeconds` | `10800` (3h sliding) | per policy |
| `CookieName` | `maia_session` | — |

- The session cookie is `HttpOnly` + `SameSite=Strict`. Strict is the CSRF defense and works
  because the SPA and API are same-site today. **If a future deployment splits them across
  different registrable domains**, the cookie won't be sent → you must switch to
  `SameSite=None` + `Secure` *and* add antiforgery (documented follow-up), not just flip a flag.
- **CORS** already sends `AllowCredentials` for the configured origins. Add the production SPA
  origin to the CORS allowlist in `Program.cs` (it currently lists localhost dev origins only).
- **K8s probes:** `/health/live` and `/health/ready` are `AllowAnonymous` — wire them as the
  liveness/readiness probes (they must not require a session).

---

## Comms (send before the window)

- Announce the maintenance window and that **sign-in is now required**.
- Tell the team they'll receive a username + temporary password and must set a new password on
  first login.
- Name the admin performing provisioning and the expected time-to-availability.

---

## Rollback / break-glass

- **Lost admin password (no second admin):** a DBA replaces `Users.PasswordHash` for `admin`
  with a hash generated by the same `PasswordHasher<T>` (PBKDF2-HMAC-SHA256). There is no
  in-app reset in v1. *Prevent this by creating a second admin in step 3.*
- **Enforcement itself has no off-switch by design** — rolling back means redeploying the
  prior build. Don't add a bypass flag.

---

## Post-cutover smoke checks

- `admin` logs in → forced to `/change-password` → after rotation, dashboard loads.
- A non-admin (Operator) is 403'd on a config write and sees the "no permission" toast (not a
  redirect); an Operator *can* read config and take actions.
- A User role sees only operational reads.
- Logging out and reusing the old cookie is rejected (server-side session revocation).
- Audit rows for config/recommendation/user writes show the real username in `Actor`.
