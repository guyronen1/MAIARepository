# ============================================================
# generate-clean-schema.ps1
# Generates a pure CREATE TABLE / index / seed SQL schema
# script with no EF migration-history machinery.
#
# Usage (from Maia.Services root):
#   .\Scripts\generate-clean-schema.ps1
#
# Output: Scripts/schema.sql  (idempotent, for dotnet ef apply)
#         Scripts/schema-clean.sql  (pure DDL, for fresh deploy)
# ============================================================

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot | Split-Path -Parent
$scriptsDir  = $PSScriptRoot
$rawFile     = Join-Path $scriptsDir "schema.sql"
$cleanFile   = Join-Path $scriptsDir "schema-clean.sql"

Write-Host "Generating idempotent migration script -> schema.sql ..."
Push-Location $projectRoot
try {
    & dotnet ef migrations script --idempotent `
        --output $rawFile `
        --project Infrastructure `
        --startup-project Maia.API
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations script failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

Write-Host "Stripping migration-history wrappers -> schema-clean.sql ..."

$lines  = [System.IO.File]::ReadAllLines($rawFile, [System.Text.Encoding]::UTF8)
$output = [System.Collections.ArrayList]::new()
$i      = 0

while ($i -lt $lines.Length) {
    $line    = $lines[$i]
    $trimmed = $line.TrimEnd()
    $trm     = $trimmed.Trim()

    # Skip __EFMigrationsHistory table creation block
    if ($trm -eq "IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL") {
        while ($i -lt $lines.Length -and $lines[$i].Trim() -ne 'GO') { $i++ }
        $i++
        continue
    }

    # Skip BEGIN TRANSACTION / COMMIT and their trailing GO
    if ($trm -eq 'BEGIN TRANSACTION;' -or $trm -eq 'COMMIT;') {
        $i++
        if ($i -lt $lines.Length -and $lines[$i].Trim() -eq 'GO') { $i++ }
        continue
    }

    # Handle IF NOT EXISTS migration wrapper
    if ($trm -eq 'IF NOT EXISTS (') {
        $nextTrm = if ($i + 1 -lt $lines.Length) { $lines[$i+1].Trim() } else { '' }
        if ($nextTrm -eq 'SELECT * FROM [__EFMigrationsHistory]') {
            # Skip: IF NOT EXISTS ( ... ) BEGIN
            while ($i -lt $lines.Length -and $lines[$i].Trim() -ne 'BEGIN') { $i++ }
            $i++   # skip BEGIN

            # Collect body until END;
            $body = [System.Collections.ArrayList]::new()
            while ($i -lt $lines.Length -and $lines[$i].Trim() -ne 'END;') {
                [void]$body.Add($lines[$i])
                $i++
            }
            $i++   # skip END;

            # Discard blocks whose only content is INSERT INTO __EFMigrationsHistory
            $joined = ($body | Where-Object { $_.Trim() -ne '' }) -join ' '
            if ($joined -match 'INSERT INTO \[__EFMigrationsHistory\]') { continue }

            # De-indent by 4 spaces (the wrapper's own indent) and emit
            foreach ($bl in $body) {
                if ($bl.Length -ge 4 -and $bl.StartsWith('    ')) {
                    [void]$output.Add($bl.Substring(4))
                } else {
                    [void]$output.Add($bl)
                }
            }
            continue
        }
    }

    [void]$output.Add($line)
    $i++
}

# Collapse consecutive blank lines to one
$sb        = [System.Text.StringBuilder]::new()
$prevBlank = $false
foreach ($ln in $output) {
    $blank = ($ln.Trim() -eq '')
    if ($blank) {
        if (-not $prevBlank) { [void]$sb.AppendLine('') }
    } else {
        [void]$sb.AppendLine($ln)
    }
    $prevBlank = $blank
}

$finalText = $sb.ToString().TrimEnd()
[System.IO.File]::WriteAllText($cleanFile, $finalText, [System.Text.UTF8Encoding]::new($false))

$lineCount = ($finalText -split "`n").Count
Write-Host "Done. schema-clean.sql written - $lineCount lines"
