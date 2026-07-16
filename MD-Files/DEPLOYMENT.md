> Part of MAIA CLAUDE.md, split out for size. Root index: ../CLAUDE.md

# IIS Deployment Guide (Maia.API + Maia.Client)

## Topology

```
IIS Site "Maia"  (https, one binding, NO Host name set on the binding)
├── /Maia.API      → Application, App Pool: MaiaApp (No Managed Code)
└── /Maia.Client   → Application, same or separate pool (static files only)
```

Same site/host/port for both — required for the `SameSite=Strict` session cookie to work
without CORS.

If the aliases `Maia.API` / `Maia.Client` ever change, update to match:
- `Maia.Client/src/environments/environment.prod.ts` → `apiUrl`
- `Maia.Client/angular.json` → `production` config → `baseHref`

---

## Prerequisites (once per server)

- IIS role (Web Server (IIS))
- **URL Rewrite module** — https://www.iis.net/downloads/microsoft/url-rewrite
- **.NET 8 Hosting Bundle** — https://dotnet.microsoft.com/download/dotnet/8.0
- **VC++ Redistributable (x64)** — https://aka.ms/vs/17/release/vc_redist.x64.exe
- SQL Server reachable from this box, login provisioned (see below)
- TLS certificate (self-signed OK for internal use)

---

## IIS setup

1. Create app pool **`MaiaApp`**: .NET CLR version = **No Managed Code**, Integrated pipeline.
2. Create site **`Maia`**, bind HTTPS (443) with the cert. **Leave Host name blank.**
3. Add application **`Maia.API`** → `D:\WebApps\Maia\Maia.API`, pool `MaiaApp`.
4. Add application **`Maia.Client`** → `D:\WebApps\Maia\Maia.Client`, pool `MaiaApp`.
5. Replace the **site root** `web.config` (if one exists from a prior app) with:
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <configuration>
     <system.webServer>
     </system.webServer>
   </configuration>
   ```

---

## Database

Standard connection string:
```
Server=<sql-host>;Database=MaiaDb;User Id=...;Password=...;TrustServerCertificate=True;
```
Set this in **`appsettings.Production.json`** on the server (not in the repo's checked-in
placeholder). SQL auth (`User Id`/`Password`) and Windows Auth (`Integrated Security=true`
+ app pool identity granted a SQL login) both work fine once TCP is forced — pick either.

---

## Deploy: API

```powershell
# Build — framework-dependent (NOT self-contained: causes a
# Microsoft.Data.SqlClient PlatformNotSupportedException)
cd Maia.Services
dotnet publish Maia.API -c Release -o Maia.API\publish
```

On the server:
```powershell
Stop-WebAppPool -Name "MaiaApp"
Remove-Item D:\WebApps\Maia\Maia.API\* -Recurse -Force
# copy contents of Maia.API\publish\ into D:\WebApps\Maia\Maia.API\
# set the real connection string in the deployed appsettings.Production.json
Start-WebAppPool -Name "MaiaApp"
iisreset
```

Optional — enable Swagger in Production (without switching environment) by adding to
`web.config`'s `<aspNetCore>` element:
```xml
<environmentVariables>
  <environmentVariable name="EnableSwagger" value="true" />
</environmentVariables>
```
(Note: every republish regenerates `web.config`, so this must be re-added each time.)

---

## Deploy: SPA (Maia.Client)

```powershell
cd Maia.Client
npm run build   # production config: apiUrl + baseHref already set for this topology
```

On the server:
```powershell
Remove-Item D:\WebApps\Maia\Maia.Client\* -Recurse -Force
# copy the CONTENTS of dist\Maia.Client\browser\ (not the browser\ folder
# itself) into D:\WebApps\Maia\Maia.Client\
```

Verify `index.html` and `web.config` sit directly in that folder, not nested under a
`browser\` subfolder.

---

## Smoke test

```powershell
# Anonymous endpoint
Invoke-WebRequest https://<host>/Maia.API/api/auth/me -UseBasicParsing
# → 200 { "authenticated": false }

# Login (exercises DB)
$body = @{ username = "admin"; password = "admin" } | ConvertTo-Json
Invoke-WebRequest https://<host>/Maia.API/api/auth/login -Method POST `
  -Body $body -ContentType "application/json" -UseBasicParsing
# → 200 + Set-Cookie: maia_session=...; secure; samesite=strict; httponly

# SPA
# Browse https://<host>/Maia.Client/ → log in → Network tab shows calls to
# /Maia.API/api/...
```

Self-signed cert + Windows PowerShell 5.1 (no `-SkipCertificateCheck`): use `curl.exe -k -v
<url>`, or disable cert validation for the session:
```powershell
Add-Type @"
using System.Net;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsPolicy : ICertificatePolicy {
    public bool CheckValidationResult(ServicePoint sp, X509Certificate cert, WebRequest req, int problem) {
        return true;
    }
}
"@
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
```

---

## If something breaks — quick lookup

| Symptom | Cause | Fix |
|---|---|---|
| `500.19` (`0x8007000d`) | App pool CLR = v4.0, or inherited legacy `system.web`/rewrite config | Pool → No Managed Code; clean site-root `web.config` |
| `403.14` on the API | Application not converted from a plain folder | Convert to Application in IIS |
| `403.14` / blank on the SPA | Copied `browser\` folder itself instead of its contents | Flatten — files must sit directly in the app folder |
| SPA loads, all assets 404 | `baseHref` doesn't match the app's sub-path | Set `baseHref` in `angular.json`'s production config |
| Generic 404, nothing in app/IIS config explains it | HTTPS binding has a Host name set, request's Host header doesn't match | Clear Host name on the binding |
| Config looks right, runtime disagrees | Stale IIS config cache | Full `iisreset`, not just pool recycle |
| `503` mid-troubleshooting | Rapid-Fail Protection stopped the pool after repeated crashes | `Start-WebAppPool` |
| `PlatformNotSupportedException` on `Microsoft.Data.SqlClient` | Self-contained publish | Republish framework-dependent |
| `Shared Memory Provider: No process is on the other end of the pipe` | Same-server SQL connection under IIS service identity | Force `tcp:<host>,1433` in the connection string |
