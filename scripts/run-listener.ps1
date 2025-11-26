param(
    [switch]$NoBuild
)

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Padsign.Listener\Padsign.Listener.csproj"
$publishDir = Join-Path $root "out"

if (-not $NoBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error ".NET SDK not found. Install .NET 6 SDK from https://dotnet.microsoft.com/en-us/download/dotnet/6.0"
        exit 1
    }
    dotnet publish $project -c Release -o $publishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$configSource = Join-Path $root "config\padsign.json"
if (-not (Test-Path $configSource)) {
    $configSource = Join-Path $root "config\padsign.sample.json"
    Write-Warning "Using sample config. Copy config\padsign.sample.json to config\padsign.json and edit for production."
}

$configTarget = Join-Path $publishDir "padsign.json"
Copy-Item $configSource $configTarget -Force

$exe = Join-Path $publishDir "Padsign.Listener.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Listener executable not found at $exe. Build failed?"
    exit 1
}

Push-Location $publishDir
try {
    Write-Host "Starting Padsign listener from $publishDir ..."
    & $exe
}
finally {
    Pop-Location
}
