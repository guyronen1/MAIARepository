# ============================================================
# finish-rename.ps1
# Completes the two top-level folder renames that cannot be done
# while VS Code has this workspace open:
#   MaiaAIEngine.services  -> Maia.Services   (git GITLINK in the outer repo)
#   MaiaAIEngineClient     -> Maia.Client     (regular files in the outer repo)
# plus the .code-workspace and .gitignore updates.
#
# HOW TO RUN:
#   1. Save your work and CLOSE VS Code completely.
#   2. Open a plain PowerShell window (Start menu -> PowerShell), NOT inside VS Code.
#   3. Run:   powershell -ExecutionPolicy Bypass -File C:\Projects\MaiaAssistantAIEngine\finish-rename.ps1
#   4. Reopen the workspace:  C:\Projects\MaiaAssistantAIEngine\MaiaAIEngine.code-workspace
# ============================================================

$ErrorActionPreference = 'Stop'
$root = 'C:\Projects\MaiaAssistantAIEngine'
Set-Location $root

# --- Safety: refuse to run while VS Code is open (it holds the folder locks) ---
if (Get-Process -Name 'Code','devenv' -ErrorAction SilentlyContinue) {
    Write-Host 'VS Code / Visual Studio is still running. Close it fully, then re-run from a plain PowerShell window.' -ForegroundColor Red
    exit 1
}

function Write-NoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
}

# --- 1) Client: regular files -> git mv detects the rename ---
if (Test-Path 'MaiaAIEngineClient') {
    git mv 'MaiaAIEngineClient' 'Maia.Client'
    if ($LASTEXITCODE -ne 0) { throw 'git mv of client failed' }
    Write-Host 'OK  MaiaAIEngineClient -> Maia.Client'
} elseif (Test-Path 'Maia.Client') {
    Write-Host 'skip  client already renamed'
}

# --- 2) Services: GITLINK (mode 160000). Rename folder, then fix the outer index. ---
if (Test-Path 'MaiaAIEngine.services') {
    Rename-Item 'MaiaAIEngine.services' 'Maia.Services'
    git rm --cached 'MaiaAIEngine.services' 2>$null | Out-Null   # drop the old gitlink entry
    git add 'Maia.Services'                                       # re-add gitlink at the new path
    Write-Host 'OK  MaiaAIEngine.services -> Maia.Services (gitlink re-pointed)'
} elseif (Test-Path 'Maia.Services') {
    Write-Host 'skip  services already renamed'
}

# --- 3) Workspace file: update both folder paths ---
$ws = 'MaiaAIEngine.code-workspace'
if (Test-Path $ws) {
    $t = [System.IO.File]::ReadAllText($ws)
    $t = $t.Replace('MaiaAIEngine.services', 'Maia.Services').Replace('MaiaAIEngineClient', 'Maia.Client')
    Write-NoBom $ws $t
    Write-Host 'OK  updated MaiaAIEngine.code-workspace'
}

# --- 4) .gitignore: update client paths (dist/.angular) ---
$gi = '.gitignore'
if (Test-Path $gi) {
    $t = [System.IO.File]::ReadAllText($gi)
    $t = $t.Replace('MaiaAIEngineClient', 'Maia.Client')
    Write-NoBom $gi $t
    Write-Host 'OK  updated .gitignore'
}

Write-Host ''
Write-Host 'Done. Next:' -ForegroundColor Green
Write-Host '  - Reopen C:\Projects\MaiaAssistantAIEngine\MaiaAIEngine.code-workspace in VS Code'
Write-Host '  - In Maia.Client, run: npm run build   (the .angular/dist caches were cleared)'
Write-Host '  - Commit when ready (outer repo has the gitlink + renames staged)'
