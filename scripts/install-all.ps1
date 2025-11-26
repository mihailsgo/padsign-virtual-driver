param(
    [string]$PrinterName = "Padsign",
    [int]$PortNumber = 9100,
    [string]$Company = "",
    [string]$ApiUrl = "",
    [string]$ApiKey = "",
    [string]$GhostscriptPath = "",
    [switch]$StartListener,
    [switch]$Force
)

function Require-Admin {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell."
    }
}

function Ensure-DotNet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK/runtime not found. Install .NET 6 from https://dotnet.microsoft.com/en-us/download/dotnet/6.0"
    }
}

function Update-Config {
    param([string]$ConfigPath, [string]$SamplePath)

    if (-not (Test-Path $ConfigPath)) {
        Copy-Item $SamplePath $ConfigPath -Force
    }

    $cfg = Get-Content $ConfigPath | ConvertFrom-Json
    if ($ApiUrl) { $cfg.ApiUrl = $ApiUrl }
    if ($ApiKey) { $cfg.ApiKey = $ApiKey }
    if ($Company) { $cfg.Company = $Company }
    if ($PortNumber) { $cfg.Port = $PortNumber }
    if ($GhostscriptPath) { $cfg.GhostscriptPath = $GhostscriptPath }
    $cfg | ConvertTo-Json | Set-Content $ConfigPath
}

function Build-App {
    param([string]$ProjectPath, [string]$OutDir)
    dotnet publish $ProjectPath -c Release -o $OutDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

try {
    Require-Admin
    Ensure-DotNet

    $root = Split-Path -Parent $PSScriptRoot
    $configPath = Join-Path $root "config\padsign.json"
    $configSample = Join-Path $root "config\padsign.sample.json"

    Write-Host "Updating config..."
    Update-Config -ConfigPath $configPath -SamplePath $configSample

    Write-Host "Creating printer and port..."
    & (Join-Path $root "scripts\install-printer.ps1") -PrinterName $PrinterName -PortNumber $PortNumber

    Write-Host "Building app..."
    $project = Join-Path $root "src\Padsign.Listener\Padsign.Listener.csproj"
    $outDir = Join-Path $root "out"
    Build-App -ProjectPath $project -OutDir $outDir

    Write-Host "Copying config to output..."
    Copy-Item $configPath (Join-Path $outDir "padsign.json") -Force

    Write-Host "Ensuring firewall rule for localhost:$PortNumber ..."
    $ruleName = "Padsign Listener $PortNumber"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Profile Any -LocalAddress 127.0.0.1 -LocalPort $PortNumber -Protocol TCP | Out-Null
    }

    if ($StartListener) {
        Write-Host "Starting listener..."
        Push-Location $outDir
        Start-Process -FilePath (Join-Path $outDir "Padsign.Listener.exe")
        Pop-Location
    }

    Write-Host "Done. Listener exe is in $outDir. Run scripts\\run-listener.ps1 to start if not already running."
}
catch {
    Write-Error $_
    exit 1
}
