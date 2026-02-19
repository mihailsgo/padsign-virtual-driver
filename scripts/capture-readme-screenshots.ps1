Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$code = @"
using System;
using System.Runtime.InteropServices;
public static class WinApi {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
Add-Type -TypeDefinition $code

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "out\manager\Padsign.Manager.exe"
$imgDir = Join-Path $root "docs\images"

if (-not (Test-Path $exe)) {
    throw "Manager executable not found: $exe"
}
New-Item -ItemType Directory -Force -Path $imgDir | Out-Null

$proc = Start-Process -FilePath $exe -PassThru
try {
    $maxWait = [DateTime]::Now.AddSeconds(20)
    while ($proc.MainWindowHandle -eq 0 -and [DateTime]::Now -lt $maxWait) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
    }
    if ($proc.MainWindowHandle -eq 0) { throw "Could not find Manager window handle." }

    [WinApi]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 700

    function Get-Rect([IntPtr]$hWnd) {
        $r = New-Object WinApi+RECT
        [WinApi]::GetWindowRect($hWnd, [ref]$r) | Out-Null
        return $r
    }

    function Save-WindowShot([IntPtr]$hWnd, [string]$path) {
        $r = Get-Rect $hWnd
        $w = [Math]::Max(1, $r.Right - $r.Left)
        $h = [Math]::Max(1, $r.Bottom - $r.Top)
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hDc = $g.GetHdc()
        [WinApi]::PrintWindow($hWnd, $hDc, 0) | Out-Null
        $g.ReleaseHdc($hDc)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
    }

    function Click-At([int]$x, [int]$y) {
        [WinApi]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 120
        [WinApi]::mouse_event([WinApi]::MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [WinApi]::mouse_event([WinApi]::MOUSEEVENTF_LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    }

    $rect = Get-Rect $proc.MainWindowHandle

    Write-Host "Capturing process id=$($proc.Id) title='$($proc.MainWindowTitle)' handle=$($proc.MainWindowHandle)"
    Save-WindowShot $proc.MainWindowHandle (Join-Path $imgDir "setup-tab.png")
    Start-Sleep -Milliseconds 500

    Click-At ($rect.Left + 122) ($rect.Top + 182)   # Operations tab
    Start-Sleep -Milliseconds 600
    Save-WindowShot $proc.MainWindowHandle (Join-Path $imgDir "operations-tab.png")

    Click-At ($rect.Left + 188) ($rect.Top + 182)   # Monitoring tab
    Start-Sleep -Milliseconds 600
    Save-WindowShot $proc.MainWindowHandle (Join-Path $imgDir "monitoring-tab.png")

    Click-At ($rect.Right - 38) ($rect.Top + 98)    # Help button
    Start-Sleep -Milliseconds 700
    Save-WindowShot $proc.MainWindowHandle (Join-Path $imgDir "help-window.png")
}
finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 500
        if (-not $proc.HasExited) { $proc.Kill() }
    }
}

Write-Host "Screenshots captured to: $imgDir"
