<#
.SYNOPSIS
  Publishes a new PadNook version on GitHub: sets the version, builds the exe, computes its SHA-256 and
  creates the release with PadNook.exe + PadNook.exe.sha256. Installed copies then offer the update.
.EXAMPLE
  .\release.ps1 -Version 1.2.0 -Notes "Novità: ..."
  .\release.ps1 -Version 1.2.1 -NotesFile note.md
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Notes = '',
    [string]$NotesFile = '',
    [string]$Repo = 'AgaresFX/PadNook'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: usa il formato 1.2.3" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) non trovato: https://cli.github.com" }

# 1. Version in the server and in the webapp
$csproj = Join-Path $root 'server\PadNook.csproj'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($csproj, ((Get-Content $csproj -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"), $utf8)
$pkg = Join-Path $root 'webapp\package.json'
[IO.File]::WriteAllText($pkg, ((Get-Content $pkg -Raw) -replace '"version":\s*"[^"]+"', "`"version`": `"$Version`""), $utf8)

# 2. Build (dist\PadNook.exe)
& (Join-Path $root 'build.ps1')
if ($LASTEXITCODE) { throw "Compilazione non riuscita" }

# 3. Checksum checked by the updater before installing
$exe = Join-Path $root 'dist\PadNook.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
$shaFile = Join-Path $root 'dist\PadNook.exe.sha256'
"$hash  PadNook.exe" | Set-Content $shaFile -NoNewline -Encoding ascii

# 4. GitHub release
$ghArgs = @('release', 'create', "v$Version", $exe, $shaFile, '--repo', $Repo, '--title', "PadNook $Version")
if ($NotesFile) { $ghArgs += @('--notes-file', $NotesFile) }
elseif ($Notes) { $ghArgs += @('--notes', $Notes) }
else { $ghArgs += @('--notes', "PadNook $Version") }
gh @ghArgs
if ($LASTEXITCODE) { throw "Creazione della release non riuscita" }

Write-Host "`nPubblicata PadNook $Version (SHA-256 $hash)" -ForegroundColor Green
Write-Host "Le copie installate la proporranno entro 6 ore (o subito con «Cerca aggiornamenti»)."
