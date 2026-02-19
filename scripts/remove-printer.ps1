param(
    [string]$PrinterName = "Padsign",
    [string]$PortName = "PADPORT"
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

    $printer = Get-Printer -Name $PrinterName -ErrorAction SilentlyContinue
    if ($printer) {
        Write-Host "Removing printer '$PrinterName' ..."
        Remove-Printer -Name $PrinterName -ErrorAction Stop
    } else {
        Write-Host "Printer '$PrinterName' not found."
    }

    $port = Get-PrinterPort -Name $PortName -ErrorAction SilentlyContinue
    if ($port) {
        $portInUse = Get-Printer | Where-Object { $_.PortName -eq $PortName } | Select-Object -First 1
        if ($portInUse) {
            Write-Host "Port '$PortName' is still in use by printer '$($portInUse.Name)'; keeping port."
        } else {
            Write-Host "Removing printer port '$PortName' ..."
            Remove-PrinterPort -Name $PortName -ErrorAction Stop
        }
    } else {
        Write-Host "Port '$PortName' not found."
    }

    Write-Host "Padsign printer removal complete."
}
catch {
    Write-Error $_
    exit 1
}
