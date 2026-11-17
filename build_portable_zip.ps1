<#
.SYNOPSIS
  Genera LocalLLM-Portable-<version>-win-x64.zip listo para copiar y ejecutar en cualquier PC sin Admin.

.DESCRIPTION
  1. Publica el Gateway .NET 8 como un único .exe self-contained (no requiere .NET instalado).
  2. Arma la estructura portable (bin\, models\, env\, repo\, data\) con rutas relativas.
  3. Opcional (-IncludeTools): pre-descarga uv, ollama y MinGit verificando SHA256 (paquete "offline").
     Sin este switch, el Gateway los descarga en el primer arranque.
  4. Comprime en .zip y genera <zip>.sha256.

  Funciona con un SDK .NET 8 instalado o portable (no necesita Admin: dotnet-install.ps1 -InstallDir).

.EXAMPLE
  .\build_portable_zip.ps1
  .\build_portable_zip.ps1 -IncludeTools -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$IncludeTools,
    [switch]$IncludeRepo,
    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest

# En Windows PowerShell 5.1 $PSScriptRoot esta vacio dentro de param(): se resuelve aqui.
$root      = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutputDir) { $OutputDir = Join-Path $root "dist" }
$project   = Join-Path $root "src\LocalLLM.Gateway\LocalLLM.Gateway.csproj"
$pkgName   = "LocalLLM-Portable"
$staging   = Join-Path $OutputDir "staging\$pkgName"
$publish   = Join-Path $OutputDir "publish"
$zipPath   = Join-Path $OutputDir "$pkgName-$Version-win-x64.zip"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 0. SDK
Write-Step "Verificando SDK .NET"
$sdks = & $Dotnet --list-sdks 2>$null
if (-not ($sdks -match '^(8|9|1\d)\.')) {
    throw "Se requiere SDK .NET 8+. Instalación sin Admin: iwr https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; .\dotnet-install.ps1 -Channel 8.0 -InstallDir .\.dotnet -NoPath; luego -Dotnet .\.dotnet\dotnet.exe"
}

# ---------------------------------------------------------------- 1. publish
Write-Step "Publicando Gateway ($Configuration, win-x64, self-contained, single-file)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $Dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

# ---------------------------------------------------------------- 2. estructura portable
Write-Step "Armando estructura portable en $staging"
if (Test-Path (Split-Path $staging)) { Remove-Item (Split-Path $staging) -Recurse -Force }
foreach ($d in "bin\uv", "bin\ollama", "bin\git", "models", "env", "repo", "data\logs", "data\cache") {
    New-Item -ItemType Directory -Force -Path (Join-Path $staging $d) | Out-Null
}

Copy-Item (Join-Path $publish "LocalLLM-Gateway.exe") $staging
Copy-Item (Join-Path $root "profiles.json") $staging
Copy-Item (Join-Path $root "Iniciar-LocalLLM.bat") $staging
Copy-Item (Join-Path $root "README.md") $staging -ErrorAction SilentlyContinue

# Carpetas vacías: el .zip no conserva directorios vacíos en todas las herramientas → marcador.
foreach ($d in "bin\uv", "bin\ollama", "bin\git", "models", "env", "repo") {
    $p = Join-Path $staging $d
    if (-not (Get-ChildItem $p -Force)) { Set-Content -Path (Join-Path $p ".keep") -Value "" }
}

if ($IncludeRepo) {
    Write-Step "Incluyendo clon Git del repositorio en repo\"
    Remove-Item (Join-Path $staging "repo") -Recurse -Force
    git clone --branch main (git -C $root remote get-url origin) (Join-Path $staging "repo")
    if ($LASTEXITCODE -ne 0) { throw "git clone falló" }
}

# ---------------------------------------------------------------- 3. herramientas offline (opcional)
function Get-VerifiedRelease {
    param([string]$Repo, [string]$Pattern, [string]$Dest)
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent" = "LocalLLM-Portable-Build" }
    $asset = $rel.assets | Where-Object { $_.name -match $Pattern } | Select-Object -First 1
    if (-not $asset) { throw "Ningún asset de $Repo coincide con $Pattern" }

    $expected = $null
    if ($asset.PSObject.Properties.Name -contains "digest" -and $asset.digest -like "sha256:*") { $expected = $asset.digest.Substring(7) }
    if (-not $expected) {
        $side = $rel.assets | Where-Object { $_.name -eq "$($asset.name).sha256" -or $_.name -eq "sha256sum.txt" } | Select-Object -First 1
        if ($side) {
            $txt = (Invoke-WebRequest -Uri $side.browser_download_url -UseBasicParsing).Content
            if ($txt -is [byte[]]) { $txt = [Text.Encoding]::UTF8.GetString($txt) }
            $line = ($txt -split "`n") | Where-Object { $_ -match [regex]::Escape($asset.name) -or ($txt -split "`n").Count -eq 1 } | Select-Object -First 1
            if ($line -match '([0-9a-fA-F]{64})') { $expected = $Matches[1] }
        }
    }
    if (-not $expected) {
        $line = ($rel.body -split "`n") | Where-Object { $_ -match [regex]::Escape($asset.name) } | Select-Object -First 1
        if ($line -match '([0-9a-fA-F]{64})') { $expected = $Matches[1] }
    }
    if (-not $expected) { throw "Sin SHA256 confiable para $($asset.name)" }

    $tmp = Join-Path $OutputDir "downloads"
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $file = Join-Path $tmp $asset.name
    Write-Host "    descargando $($asset.name) ($([math]::Round($asset.size/1MB)) MB)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) { throw "SHA256 inválido para $($asset.name): esperado $expected, obtenido $actual" }
    Write-Host "    SHA256 OK $actual"

    $x = "$Dest.extracting"
    if (Test-Path $x) { Remove-Item $x -Recurse -Force }
    Expand-Archive -Path $file -DestinationPath $x -Force
    $items = @(Get-ChildItem $x -Force)
    $src = if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $items[0].FullName } else { $x }
    if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
    Move-Item $src $Dest
    if (Test-Path $x) { Remove-Item $x -Recurse -Force }
}

if ($IncludeTools) {
    Write-Step "Pre-descargando herramientas portables (verificación SHA256)"
    Get-VerifiedRelease "astral-sh/uv"        '^uv-x86_64-pc-windows-msvc\.zip$'                       (Join-Path $staging "bin\uv")
    Get-VerifiedRelease "ollama/ollama"       '^ollama-windows-amd64\.zip$'                            (Join-Path $staging "bin\ollama")
    Get-VerifiedRelease "git-for-windows/git" '^MinGit-[0-9.]+(\.windows\.[0-9]+)?-64-bit\.zip$'        (Join-Path $staging "bin\git")
}

# ---------------------------------------------------------------- 4. zip + hash
Write-Step "Comprimiendo $zipPath"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Split-Path $staging), $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zipPath.sha256" -Value "$hash  $(Split-Path $zipPath -Leaf)" -Encoding ascii

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "Paquete listo: $zipPath ($sizeMb MB)" -ForegroundColor Green
Write-Host "SHA256:        $hash"
Write-Host "Uso: descomprimir en cualquier carpeta del usuario (Escritorio, Documentos, USB) y ejecutar Iniciar-LocalLLM.bat"
