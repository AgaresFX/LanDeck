<#
.SYNOPSIS
  Builds PadNook: compiles the webapp, embeds it in the server and publishes a single self-contained exe.
.EXAMPLE
  .\build.ps1
  .\build.ps1 -SkipWeb      # reuse the last webapp build
#>
param(
    [switch]$SkipWeb,
    [string]$Output = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET SDK non trovato: installa .NET 10 SDK da https://dot.net" }

if (-not $SkipWeb) {
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw "Node.js/npm non trovato: installa Node.js LTS da https://nodejs.org" }
    Step "Webapp: installazione dipendenze"
    Push-Location (Join-Path $root 'webapp')
    try {
        npm install --no-audit --no-fund
        if ($LASTEXITCODE) { throw "npm install non riuscito" }
        Step "Webapp: compilazione (output in server\wwwroot)"
        npm run build
        if ($LASTEXITCODE) { throw "Compilazione della webapp non riuscita" }
    }
    finally { Pop-Location }
}

$running = Get-Process -Name PadNook -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Output*" }
if ($running) { throw "PadNook.exe è in esecuzione da ${Output}: chiudilo (menu dell'icona -> Esci) e riprova." }

Step "Server: pubblicazione exe singolo self-contained"
dotnet publish (Join-Path $root 'server\PadNook.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $Output
if ($LASTEXITCODE) { throw "dotnet publish non riuscito" }

$exe = Join-Path $Output 'PadNook.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nCreato: $exe ($size MB)" -ForegroundColor Green
