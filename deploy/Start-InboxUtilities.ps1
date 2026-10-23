$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$zohoEnvironmentVariables = @(
    'Zoho__ClientId',
    'Zoho__ClientSecret',
    'Zoho__RefreshToken',
    'Zoho__AccountId',
    'Zoho__FromAddress',
    'Zoho__TokenUrl',
    'Zoho__ApiBaseUrl',
    'Zoho__WebMailUrl'
)
foreach ($name in $zohoEnvironmentVariables) {
    $value = [Environment]::GetEnvironmentVariable($name, 'User')
    if ($null -ne $value) {
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}
$requiredZohoEnvironmentVariables = @(
    'Zoho__ClientId',
    'Zoho__ClientSecret',
    'Zoho__RefreshToken',
    'Zoho__AccountId',
    'Zoho__FromAddress'
)
$missingZohoEnvironmentVariables = @(
    $requiredZohoEnvironmentVariables | Where-Object {
        [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_, 'Process'))
    }
)
if ($missingZohoEnvironmentVariables.Count -gt 0) {
    $missingNames = $missingZohoEnvironmentVariables -join ', '
    Write-Warning "Zoho draft credentials are not available to New Loan Case: $missingNames. Cases can still be saved, but Zoho drafts will fail."
}

$readerPath = Join-Path $root 'InboxReader\InboxReader.exe'
$dashboardPath = Join-Path $root 'Dashboard\UtilityDashboard.exe'
$newCasePath = Join-Path $root 'NewCase\New Case Create.exe'
$checkerPath = Join-Path $root 'CompletenessChecker\LoanApplicationCompletenessChecker.exe'
$readerSettings = Join-Path $root 'InboxReader\appsettings.json'

if (-not (Test-Path -LiteralPath $readerSettings)) {
    throw "Inbox Reader settings were not found: $readerSettings"
}
$settings = Get-Content -LiteralPath $readerSettings -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($settings.InboxReader.Email)) {
    throw "Configure the InboxReader.Email value in '$readerSettings' before starting."
}
$mailExchangeServer = $settings.InboxReader.MailExchangeServer
if ([string]::IsNullOrWhiteSpace($mailExchangeServer)) {
    $mailExchangeServer = $settings.InboxReader.Provider
}
switch -Regex ($mailExchangeServer) {
    '^(?i:Zoho)$' {
        if ([string]::IsNullOrWhiteSpace($settings.InboxReader.Password)) {
            throw "Configure the Zoho app password in '$readerSettings' before starting."
        }
        break
    }
    '^(?i:Outlook)$' {
        if ([string]::IsNullOrWhiteSpace($settings.InboxReader.OutlookClientId)) {
            throw "Configure OutlookClientId in '$readerSettings' before starting."
        }
        break
    }
    default {
        throw "MailExchangeServer must be 'Zoho' or 'Outlook' in '$readerSettings'."
    }
}

$dataRoot = 'Z:\LoanProcessing-AutoMation'
if (-not (Test-Path -LiteralPath 'Z:\' -PathType Container)) {
    throw "The Z: drive is unavailable. Connect it before starting Inbox Utilities; application data is not saved locally as a fallback."
}

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
$storageProbe = Join-Path $dataRoot ('.write-test-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($storageProbe, 'storage check')
} catch {
    throw "The shared data folder is not writable: $dataRoot. $($_.Exception.Message)"
} finally {
    if (Test-Path -LiteralPath $storageProbe) {
        Remove-Item -LiteralPath $storageProbe -Force
    }
}

$casesPath = Join-Path $dataRoot 'Cases'
$clientCasesPath = Join-Path $dataRoot 'Clients'
$inboxAttachmentsPath = Join-Path $dataRoot 'InboxAttachments'
$newCaseDataPath = Join-Path $dataRoot 'NewCase'
$checkerDataPath = Join-Path $dataRoot 'CompletenessChecker'
$templatesPath = Join-Path $dataRoot 'Templates-DoNotDelete'
foreach ($path in @(
    $casesPath,
    $clientCasesPath,
    $inboxAttachmentsPath,
    $newCaseDataPath,
    $templatesPath,
    (Join-Path $checkerDataPath 'ApplicationStates')
)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

$templateFiles = @(
    'loan-types.json',
    'checklist-templates.json',
    'checklist-templates.defaults.json',
    'checklist-templates.backup.json'
)
foreach ($fileName in $templateFiles) {
    $sharedFile = Join-Path $templatesPath $fileName
    $legacyFile = Join-Path $dataRoot $fileName
    if (-not (Test-Path -LiteralPath $sharedFile) -and (Test-Path -LiteralPath $legacyFile)) {
        Move-Item -LiteralPath $legacyFile -Destination $sharedFile
    }

    if (-not (Test-Path -LiteralPath $sharedFile) -and $fileName -ne 'checklist-templates.backup.json') {
        $packagedCandidates = @(
            (Join-Path $root "Templates-DoNotDelete\$fileName"),
            (Join-Path $root "CompletenessChecker\$fileName")
        )
        $packagedFile = $packagedCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $packagedFile) {
            throw "The required shared template file is missing: $fileName"
        }
        Copy-Item -LiteralPath $packagedFile -Destination $sharedFile
    }
}
$loanTypeCatalog = Join-Path $templatesPath 'loan-types.json'
try {
    $catalog = Get-Content -LiteralPath $loanTypeCatalog -Raw | ConvertFrom-Json
    if ($catalog.loanTypes.Count -lt 1) {
        throw 'The catalogue does not contain any loan types.'
    }
} catch {
    throw "The shared loan type catalogue is invalid: $($_.Exception.Message)"
}

function Move-DataFiles([string] $Source, [string] $Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        return 0
    }

    $sourceFullPath = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
    $destinationFullPath = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if ([string]::Equals($sourceFullPath, $destinationFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        return 0
    }

    $moved = 0
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse -Force) {
        $relativePath = $file.FullName.Substring($sourceFullPath.Length)
        $destinationFile = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationFile
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        if (Test-Path -LiteralPath $destinationFile -PathType Leaf) {
            continue
        }

        Move-Item -LiteralPath $file.FullName -Destination $destinationFile -Force
        $moved++
    }

    foreach ($directory in Get-ChildItem -LiteralPath $Source -Directory -Recurse -Force | Sort-Object FullName -Descending) {
        if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    return $moved
}

$legacyCasesPath = Join-Path $root 'NewCase\Cases'
$movedCases = Move-DataFiles $legacyCasesPath $casesPath
$legacyNewCaseTemplates = Join-Path $root 'NewCase\document-request-templates.json'
if (Test-Path -LiteralPath $legacyNewCaseTemplates -PathType Leaf) {
    $targetTemplate = Join-Path $newCaseDataPath 'document-request-templates.json'
    if (-not (Test-Path -LiteralPath $targetTemplate)) {
        Move-Item -LiteralPath $legacyNewCaseTemplates -Destination $targetTemplate -Force
        $movedCases++
    }
}

$legacyInboxPath = $settings.InboxReader.OutputFolder
if ([string]::IsNullOrWhiteSpace($legacyInboxPath)) {
    $legacyInboxPath = 'C:\InboxAttachments'
}
$movedInboxFiles = Move-DataFiles $legacyInboxPath $inboxAttachmentsPath

$legacyCheckerPath = Join-Path $root 'CompletenessChecker\ApplicationStates'
$movedCheckerFiles = Move-DataFiles $legacyCheckerPath $checkerDataPath
if ($movedCases -gt 0 -or $movedInboxFiles -gt 0 -or $movedCheckerFiles -gt 0) {
    Write-Host "Moved $movedCases case-data file(s), $movedInboxFiles inbox attachment file(s), and $movedCheckerFiles checklist data file(s) to $dataRoot."
}

[Environment]::SetEnvironmentVariable('DataStorage__RootPath', $dataRoot, 'Process')
[Environment]::SetEnvironmentVariable('CaseTracking__CasesPath', $dataRoot, 'Process')
[Environment]::SetEnvironmentVariable('InboxReader__OutputFolder', $inboxAttachmentsPath, 'Process')
[Environment]::SetEnvironmentVariable('InboxReader__CaseRootFolder', $dataRoot, 'Process')

$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$runningPaths = @{}
Get-Process -Name InboxReader, UtilityDashboard, 'New Case Create', LoanApplicationCompletenessChecker -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        if ($_.Path) {
            $runningPaths[[IO.Path]::GetFullPath($_.Path)] = $true
        }
    } catch {
        Write-Warning "Could not inspect process $($_.Id): $($_.Exception.Message)"
    }
}

foreach ($spec in @(
    @{ Path = $checkerPath; Url = 'http://127.0.0.1:5089/api/health'; Name = 'Completeness Checker' },
    @{ Path = $readerPath; Url = 'http://127.0.0.1:5087/api/status'; Name = 'Inbox Reader' },
    @{ Path = $newCasePath; Url = 'http://127.0.0.1:5088/api/config'; Name = 'New Loan Case' },
    @{ Path = $dashboardPath; Url = 'http://127.0.0.1:5086/'; Name = 'Utility Dashboard' }
)) {
    if (-not (Test-Path -LiteralPath $spec.Path)) {
        throw "$($spec.Name) executable was not found: $($spec.Path)"
    }
    if (-not $runningPaths.ContainsKey([IO.Path]::GetFullPath($spec.Path))) {
        $logName = switch ($spec.Name) {
            'Completeness Checker' { 'CompletenessChecker' }
            'Inbox Reader' { 'InboxReader' }
            'New Loan Case' { 'NewCase' }
            default { 'Dashboard' }
        }
        Start-Process -FilePath $spec.Path `
            -WorkingDirectory (Split-Path -Parent $spec.Path) `
            -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $logs "$logName.log") `
            -RedirectStandardError (Join-Path $logs "$logName-error.log")
    }
}

$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:5086/' -TimeoutSec 2 | Out-Null
        Invoke-RestMethod -Uri 'http://127.0.0.1:5089/api/health' -TimeoutSec 2 | Out-Null
        Invoke-RestMethod -Uri 'http://127.0.0.1:5087/api/status' -TimeoutSec 2 | Out-Null
        Invoke-RestMethod -Uri 'http://127.0.0.1:5088/api/config' -TimeoutSec 2 | Out-Null
        $ready = $true
        break
    } catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $ready) {
    throw "The services did not become ready. Check the log files in '$logs'."
}

Start-Process 'http://127.0.0.1:5086/'
