param(
    [string]$PrinterName = "Padsign",
    [string]$PortName = "PADPORT",
    [int]$PortNumber = 9100,
    [string]$DriverName = "Microsoft Print To PDF"
)

function Require-Admin {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell."
    }
}

try {
    Require-Admin

    $existingPort = Get-PrinterPort -Name $PortName -ErrorAction SilentlyContinue
    if (-not $existingPort) {
        Write-Host "Creating RAW TCP port $PortName on 127.0.0.1:$PortNumber ..."
        # Some Windows Server builds lack -Protocol; default is RAW (port 9100 style).
        Add-PrinterPort -Name $PortName -PrinterHostAddress "127.0.0.1" -PortNumber $PortNumber -ErrorAction Stop
        if (Get-Command Set-PrinterPort -ErrorAction SilentlyContinue) {
            Set-PrinterPort -Name $PortName -SNMPEnabled:$false -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host "Port $PortName already exists."
    }

    $driver = Get-PrinterDriver -Name $DriverName -ErrorAction SilentlyContinue
    if (-not $driver) {
        throw "Driver '$DriverName' not found. Install it or adjust -DriverName (e.g., 'MS Publisher Color Printer')."
    }

    $printer = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
    if (-not $printer) {
        Write-Host "Adding printer '$PrinterName' with driver '$DriverName' on port '$PortName' ..."
        Add-Printer -Name $PrinterName -DriverName $DriverName -PortName $PortName -Shared:$false -ErrorAction Stop
    } else {
        Write-Host "Printer $PrinterName already exists."
    }

    Write-Host "Padsign printer setup complete."
    Write-Host "Reminder: ensure your listener is running and firewall allows localhost:$PortNumber."
}
catch {
    Write-Error $_
    exit 1
}
