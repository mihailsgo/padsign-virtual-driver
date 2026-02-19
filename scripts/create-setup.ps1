param(
    [string]$OutputPath = "",
    [int]$PortNumber = 9100,
    [switch]$SkipBuild,
    [ValidateSet("cmd", "exe")]
    [string]$PackageFormat = "cmd"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $defaultExt = if ($PackageFormat -eq "exe") { "exe" } else { "cmd" }
    $OutputPath = Join-Path $root "out\installer\Padsign-Setup.$defaultExt"
}

$installerDir = Split-Path -Parent $OutputPath
$workRoot = "C:\PadsignInstallerBuild"
$stagingRoot = Join-Path $workRoot "staging"
$managerOut = Join-Path $root "out\manager"
$listenerOut = Join-Path $root "out"

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
Remove-Item -Recurse -Force $stagingRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stagingRoot "manager") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stagingRoot "listener") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stagingRoot "config") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stagingRoot "scripts") | Out-Null

if (-not $SkipBuild) {
    Write-Host "Publishing listener..."
    dotnet publish (Join-Path $root "src\Padsign.Listener\Padsign.Listener.csproj") -c Release -o $listenerOut
    if ($LASTEXITCODE -ne 0) { throw "Listener publish failed." }

    Write-Host "Publishing manager..."
    dotnet publish (Join-Path $root "src\Padsign.Manager\Padsign.Manager.csproj") -c Release -o $managerOut
    if ($LASTEXITCODE -ne 0) { throw "Manager publish failed." }
}

Write-Host "Staging files..."
Copy-Item (Join-Path $managerOut "*") (Join-Path $stagingRoot "manager") -Force
Copy-Item (Join-Path $listenerOut "Padsign.Listener.exe") (Join-Path $stagingRoot "listener") -Force
Copy-Item (Join-Path $listenerOut "Padsign.Listener.dll") (Join-Path $stagingRoot "listener") -Force
Copy-Item (Join-Path $listenerOut "Padsign.Listener.pdb") (Join-Path $stagingRoot "listener") -Force
Copy-Item (Join-Path $listenerOut "Padsign.Listener.deps.json") (Join-Path $stagingRoot "listener") -Force
Copy-Item (Join-Path $listenerOut "Padsign.Listener.runtimeconfig.json") (Join-Path $stagingRoot "listener") -Force
Copy-Item (Join-Path $root "config\padsign.sample.json") (Join-Path $stagingRoot "config") -Force
Copy-Item (Join-Path $root "scripts\install-printer.ps1") (Join-Path $stagingRoot "scripts") -Force
Copy-Item (Join-Path $root "scripts\remove-printer.ps1") (Join-Path $stagingRoot "scripts") -Force

$installScript = @"
`$ErrorActionPreference = "Stop"

function Test-Admin {
    `$current = [Security.Principal.WindowsIdentity]::GetCurrent()
    `$principal = New-Object Security.Principal.WindowsPrincipal(`$current)
    return `$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-IfRunning([string]`$processName) {
    try {
        `$running = Get-Process -Name `$processName -ErrorAction SilentlyContinue
        if (`$running) {
            Write-Host "Stopping running process: `$processName"
            `$running | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 800
        }
    }
    catch {
        Write-Warning ("Could not stop process {0}: {1}" -f `$processName, `$_.Exception.Message)
    }
}

if (-not (Test-Admin)) {
    `$elevatedArgs = @(
        "-STA",
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", `$PSCommandPath
    )
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList `$elevatedArgs
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
`$defaultInstallRoot = Join-Path `$env:ProgramFiles "Padsign"
`$installRoot = `$defaultInstallRoot
`$createDesktopShortcut = `$true

try {
    [System.Windows.Forms.Application]::EnableVisualStyles()

    `$form = New-Object System.Windows.Forms.Form
    `$form.Text = "Padsign Setup Options"
    `$form.StartPosition = "CenterScreen"
    `$form.Size = New-Object System.Drawing.Size(620, 210)
    `$form.FormBorderStyle = "FixedDialog"
    `$form.MaximizeBox = `$false
    `$form.MinimizeBox = `$false
    `$form.TopMost = `$true

    `$label = New-Object System.Windows.Forms.Label
    `$label.Text = "Installation folder:"
    `$label.AutoSize = `$true
    `$label.Location = New-Object System.Drawing.Point(14, 20)
    `$form.Controls.Add(`$label)

    `$pathBox = New-Object System.Windows.Forms.TextBox
    `$pathBox.Size = New-Object System.Drawing.Size(470, 24)
    `$pathBox.Location = New-Object System.Drawing.Point(14, 44)
    `$pathBox.Text = `$defaultInstallRoot
    `$form.Controls.Add(`$pathBox)

    `$browseButton = New-Object System.Windows.Forms.Button
    `$browseButton.Text = "Browse..."
    `$browseButton.Size = New-Object System.Drawing.Size(96, 26)
    `$browseButton.Location = New-Object System.Drawing.Point(496, 42)
    `$browseButton.Add_Click({
        `$folder = New-Object System.Windows.Forms.FolderBrowserDialog
        `$folder.Description = "Choose installation folder for Padsign client"
        `$folder.ShowNewFolderButton = `$true
        `$folder.SelectedPath = if ([string]::IsNullOrWhiteSpace(`$pathBox.Text)) { `$defaultInstallRoot } else { `$pathBox.Text }
        if (`$folder.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK -and -not [string]::IsNullOrWhiteSpace(`$folder.SelectedPath)) {
            `$pathBox.Text = `$folder.SelectedPath
        }
    })
    `$form.Controls.Add(`$browseButton)

    `$shortcutCheckBox = New-Object System.Windows.Forms.CheckBox
    `$shortcutCheckBox.Text = "Create desktop shortcut"
    `$shortcutCheckBox.AutoSize = `$true
    `$shortcutCheckBox.Checked = `$true
    `$shortcutCheckBox.Location = New-Object System.Drawing.Point(14, 84)
    `$form.Controls.Add(`$shortcutCheckBox)

    `$installButton = New-Object System.Windows.Forms.Button
    `$installButton.Text = "Install"
    `$installButton.Size = New-Object System.Drawing.Size(96, 30)
    `$installButton.Location = New-Object System.Drawing.Point(392, 126)
    `$installButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    `$form.Controls.Add(`$installButton)

    `$cancelButton = New-Object System.Windows.Forms.Button
    `$cancelButton.Text = "Cancel"
    `$cancelButton.Size = New-Object System.Drawing.Size(96, 30)
    `$cancelButton.Location = New-Object System.Drawing.Point(496, 126)
    `$cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    `$form.Controls.Add(`$cancelButton)

    `$form.AcceptButton = `$installButton
    `$form.CancelButton = `$cancelButton

    Write-Host "Showing installer options window..."
    `$result = `$form.ShowDialog()
    if (`$result -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Host "Installation cancelled by user."
        exit 0
    }

    if (-not [string]::IsNullOrWhiteSpace(`$pathBox.Text)) {
        `$installRoot = `$pathBox.Text.Trim()
    }
    `$createDesktopShortcut = `$shortcutCheckBox.Checked
}
catch {
    Write-Warning "Installer options window failed: `$(`$_.Exception.Message)"
    Write-Host "Falling back to console prompts."

    `$inputPath = Read-Host "Installation folder [`$defaultInstallRoot]"
    if ([string]::IsNullOrWhiteSpace(`$inputPath)) {
        `$installRoot = `$defaultInstallRoot
    } else {
        `$installRoot = `$inputPath.Trim()
    }

    `$shortcutInput = Read-Host "Create desktop shortcut? (Y/n)"
    `$createDesktopShortcut = -not (`$shortcutInput -match "^(n|no)$")
}

if ([string]::IsNullOrWhiteSpace(`$installRoot)) {
    `$installRoot = `$defaultInstallRoot
}

`$managerDir = Join-Path `$installRoot "manager"
`$listenerDir = Join-Path `$installRoot "listener"
`$configDir = Join-Path `$installRoot "config"
`$scriptsDir = Join-Path `$installRoot "scripts"

New-Item -ItemType Directory -Force -Path `$managerDir | Out-Null
New-Item -ItemType Directory -Force -Path `$listenerDir | Out-Null
New-Item -ItemType Directory -Force -Path `$configDir | Out-Null
New-Item -ItemType Directory -Force -Path `$scriptsDir | Out-Null

Stop-IfRunning "Padsign.Listener"
Stop-IfRunning "Padsign.Manager"

Copy-Item (Join-Path `$PSScriptRoot "manager\*") `$managerDir -Recurse -Force
Copy-Item (Join-Path `$PSScriptRoot "listener\*") `$listenerDir -Recurse -Force
Copy-Item (Join-Path `$PSScriptRoot "scripts\install-printer.ps1") `$scriptsDir -Force
Copy-Item (Join-Path `$PSScriptRoot "scripts\remove-printer.ps1") `$scriptsDir -Force

if (-not (Test-Path (Join-Path `$configDir "padsign.json"))) {
    Copy-Item (Join-Path `$PSScriptRoot "config\padsign.sample.json") (Join-Path `$configDir "padsign.json") -Force
}

Copy-Item (Join-Path `$configDir "padsign.json") (Join-Path `$listenerDir "padsign.json") -Force

`$listenerLogsDir = Join-Path `$listenerDir "logs"
if (Test-Path `$listenerLogsDir) {
    Remove-Item -Path `$listenerLogsDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path `$listenerLogsDir | Out-Null

& (Join-Path `$scriptsDir "install-printer.ps1") -PrinterName "Padsign" -PortNumber $PortNumber

if (`$createDesktopShortcut) {
    `$shortcutPath = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Padsign Manager.lnk"
    `$wsh = New-Object -ComObject WScript.Shell
    `$shortcut = `$wsh.CreateShortcut(`$shortcutPath)
    `$shortcut.TargetPath = Join-Path `$managerDir "Padsign.Manager.exe"
    `$shortcut.WorkingDirectory = `$managerDir
    `$shortcut.Description = "Padsign Manager"
    `$shortcut.Save()
}

Start-Process (Join-Path `$managerDir "Padsign.Manager.exe")
Write-Host "Padsign installation complete."
"@

$installScriptPath = Join-Path $stagingRoot "install.ps1"
Set-Content -Path $installScriptPath -Value $installScript -Encoding UTF8

$installCmd = @"
@echo off
powershell -STA -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
exit /b %errorlevel%
"@
Set-Content -Path (Join-Path $stagingRoot "install.cmd") -Value $installCmd -Encoding ASCII

$zipPath = Join-Path $installerDir "payload.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force
$zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
$zipBase64 = [Convert]::ToBase64String($zipBytes)
$cmdOutput = if ($PackageFormat -eq "cmd") { $OutputPath } else { [System.IO.Path]::ChangeExtension($OutputPath, ".cmd") }

$cmdHeader = @'
@echo off
setlocal enabledelayedexpansion
set "_TMP=%TEMP%\PadsignSetup_%RANDOM%%RANDOM%"
if exist "%_TMP%" rd /s /q "%_TMP%"
mkdir "%_TMP%"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$c=Get-Content -Raw '%~f0'; $m='###PAYLOAD_START###'; $i=$c.LastIndexOf($m); if($i -lt 0){ throw 'payload marker not found'; }; $b=$c.Substring($i + $m.Length).Trim(); [IO.File]::WriteAllBytes('%_TMP%\payload.zip',[Convert]::FromBase64String($b));"
if errorlevel 1 goto :fail

powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Path '%_TMP%\payload.zip' -DestinationPath '%_TMP%' -Force"
if errorlevel 1 goto :fail

powershell -STA -NoProfile -ExecutionPolicy Bypass -File "%_TMP%\install.ps1"
set "_RC=%ERRORLEVEL%"
if exist "%_TMP%" rd /s /q "%_TMP%"
exit /b %_RC%

:fail
if exist "%_TMP%" rd /s /q "%_TMP%"
echo Installation failed.
exit /b 1
###PAYLOAD_START###
'@

[System.IO.File]::WriteAllText($cmdOutput, $cmdHeader + [Environment]::NewLine + $zipBase64, [System.Text.Encoding]::ASCII)
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Write-Host "Setup created: $cmdOutput"

if ($PackageFormat -eq "exe") {
    $allFiles = Get-ChildItem -Path $stagingRoot -Recurse -File | Sort-Object FullName

    $strings = New-Object System.Collections.Generic.List[string]
    $sourceGroupMap = [ordered]@{}
    $sourceSectionLines = New-Object System.Collections.Generic.List[string]
    $sourceEntriesMap = @{}
    for ($i = 0; $i -lt $allFiles.Count; $i++) {
        $index = $i + 1
        $file = $allFiles[$i]
        $relative = $file.FullName.Substring($stagingRoot.Length + 1)
        $relativeDir = Split-Path -Parent $relative
        if ([string]::IsNullOrWhiteSpace($relativeDir)) { $relativeDir = "." }
        $sourceDirFull = if ($relativeDir -eq ".") { $stagingRoot } else { Join-Path $stagingRoot $relativeDir }

        if (-not $sourceGroupMap.Contains($sourceDirFull)) {
            $groupId = $sourceGroupMap.Count
            $sourceGroupMap[$sourceDirFull] = $groupId
            $sourceEntriesMap[$groupId] = New-Object System.Collections.Generic.List[string]
            $sourceSectionLines.Add("SourceFiles$groupId=$($sourceDirFull.Replace('\', '\\'))")
        }

        $groupIndex = $sourceGroupMap[$sourceDirFull]
        $strings.Add("FILE$index=$($file.Name)")
        $sourceEntriesMap[$groupIndex].Add("`%FILE$index`%=")
    }

    $escapedOutput = $OutputPath.Replace("\", "\\")

    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=$escapedOutput
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=Padsign installation completed.
FriendlyName=Padsign Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
$(($strings -join "`r`n"))
[SourceFiles]
$(($sourceSectionLines -join "`r`n"))
"@

    $sourceBlocks = New-Object System.Text.StringBuilder
    foreach ($entry in $sourceEntriesMap.GetEnumerator() | Sort-Object Name) {
        [void]$sourceBlocks.AppendLine("[SourceFiles$($entry.Key)]")
        [void]$sourceBlocks.AppendLine(($entry.Value -join "`r`n"))
    }
    $sed += "`r`n" + $sourceBlocks.ToString()

    $sedPath = Join-Path $installerDir "padsign-setup.sed"
    Set-Content -Path $sedPath -Value $sed -Encoding ASCII

    Write-Host "Creating setup EXE..."
    $proc = Start-Process -FilePath iexpress.exe -ArgumentList "/N /Q `"$sedPath`"" -Wait -PassThru
    if ($proc.ExitCode -eq 0 -and (Test-Path $OutputPath)) {
        Write-Host "Setup EXE created: $OutputPath"
    }
    else {
        Write-Warning "IExpress packaging failed (exit code: $($proc.ExitCode)). Use CMD installer: $cmdOutput"
    }
}
