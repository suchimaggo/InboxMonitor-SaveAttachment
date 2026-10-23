[CmdletBinding()]
param(
    [string] $InstallPath = (Join-Path $env:LOCALAPPDATA 'Programs\InboxUtilities')
)

$ErrorActionPreference = 'Stop'
$sourcePath = $PSScriptRoot
$destinationPath = [IO.Path]::GetFullPath($InstallPath)
if ($destinationPath -eq [IO.Path]::GetPathRoot($destinationPath)) {
    throw 'InstallPath cannot be a drive root.'
}

New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
& robocopy $sourcePath $destinationPath /E /R:2 /W:1 /XF appsettings.json
if ($LASTEXITCODE -ge 8) {
    throw "Copying application files failed (robocopy exit code $LASTEXITCODE)."
}

foreach ($relativeSettings in @(
    'InboxReader\appsettings.json',
    'Dashboard\appsettings.json',
    'NewCase\appsettings.json'
)) {
    $settingsPath = Join-Path $destinationPath $relativeSettings
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        $sourceSettingsPath = Join-Path $sourcePath $relativeSettings
        if (-not (Test-Path -LiteralPath $sourceSettingsPath)) {
            throw "The package is missing '$relativeSettings'."
        }
        Copy-Item -LiteralPath $sourceSettingsPath -Destination $settingsPath
    }
}

$startScript = Join-Path $destinationPath 'Start-InboxUtilities.ps1'
$stopScript = Join-Path $destinationPath 'Stop-InboxUtilities.ps1'
foreach ($requiredPath in @(
    (Join-Path $destinationPath 'InboxReader\InboxReader.exe'),
    (Join-Path $destinationPath 'Dashboard\UtilityDashboard.exe'),
    (Join-Path $destinationPath 'NewCase\New Case Create.exe'),
    (Join-Path $destinationPath 'CompletenessChecker\LoanApplicationCompletenessChecker.exe'),
    (Join-Path $destinationPath 'LoanProcessing\LoanProcessing-Utility.exe'),
    $startScript,
    $stopScript
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The package is incomplete; missing '$requiredPath'."
    }
}

$shell = New-Object -ComObject WScript.Shell
$powershell = (Get-Command powershell.exe).Source
$shortcutArguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $startScript
$shortcutLocations = @(
    [Environment]::GetFolderPath('Desktop'),
    [Environment]::GetFolderPath('Programs')
)

foreach ($location in $shortcutLocations) {
    $shortcut = $shell.CreateShortcut((Join-Path $location 'Inbox Utilities.lnk'))
    $shortcut.TargetPath = $powershell
    $shortcut.Arguments = $shortcutArguments
    $shortcut.WorkingDirectory = $destinationPath
    $shortcut.Description = 'Start the local Inbox Utilities dashboard and services.'
    $shortcut.Save()
}

$stopShortcut = $shell.CreateShortcut((Join-Path $shortcutLocations[1] 'Stop Inbox Utilities.lnk'))
$stopShortcut.TargetPath = $powershell
$stopShortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $stopScript
$stopShortcut.WorkingDirectory = $destinationPath
$stopShortcut.Description = 'Stop the local Inbox Utilities services.'
$stopShortcut.Save()

Write-Host "Installed Inbox Utilities to: $destinationPath"
Write-Host 'Before launching, configure InboxReader\appsettings.json with your mailbox and app password.'
Write-Host 'Use the "Inbox Utilities" desktop shortcut to start the apps.'
