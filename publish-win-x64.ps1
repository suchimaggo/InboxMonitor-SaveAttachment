[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\InboxUtilities-win-x64')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$expectedParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $outputPath.StartsWith($expectedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside '$expectedParent'."
}

$dashboardProject = Join-Path $repositoryRoot '..\SSB-Dashboard-with-3Tiles-Working-\UtilityDashboard\UtilityDashboard.csproj'
$inboxProject = Join-Path $repositoryRoot 'InboxReader.csproj'
$newCaseProject = Join-Path $repositoryRoot '..\SSB-Dashboard-with-3Tiles-Working-\New Case Create\New Case Create.csproj'
$checkerProject = Join-Path $repositoryRoot '..\Loan Application Completeness Checker\LoanApplicationCompletenessChecker.csproj'
$loanProject = Join-Path $repositoryRoot '..\LoanProcessing-Utility\LoanProcessing-Utility.csproj'
$publishRoot = Join-Path $outputPath 'InboxUtilities'

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot | Out-Null

$publishArguments = @('publish', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true')
& dotnet @publishArguments $inboxProject '-o' (Join-Path $publishRoot 'InboxReader')
if ($LASTEXITCODE -ne 0) { throw 'Publishing Inbox Reader failed.' }
& dotnet @publishArguments $dashboardProject '-o' (Join-Path $publishRoot 'Dashboard')
if ($LASTEXITCODE -ne 0) { throw 'Publishing Utility Dashboard failed.' }
& dotnet @publishArguments $newCaseProject '-o' (Join-Path $publishRoot 'NewCase')
if ($LASTEXITCODE -ne 0) { throw 'Publishing New Loan Case failed.' }
Copy-Item (Join-Path (Split-Path -Parent $newCaseProject) 'README.md') `
    (Join-Path $publishRoot 'NewCase\README.md') -Force
& dotnet @publishArguments $checkerProject '-o' (Join-Path $publishRoot 'CompletenessChecker')
if ($LASTEXITCODE -ne 0) { throw 'Publishing the Completeness Checker failed.' }
& dotnet @publishArguments $loanProject '-o' (Join-Path $publishRoot 'LoanProcessing')
if ($LASTEXITCODE -ne 0) { throw 'Publishing Loan Processing failed.' }
Copy-Item (Join-Path $repositoryRoot 'deploy\InboxReader.appsettings.json') `
    (Join-Path $publishRoot 'InboxReader\appsettings.json') -Force
Copy-Item (Join-Path $repositoryRoot 'deploy\UtilityDashboard.appsettings.json') `
    (Join-Path $publishRoot 'Dashboard\appsettings.json') -Force
$templatesPublishPath = Join-Path $publishRoot 'Templates-DoNotDelete'
New-Item -ItemType Directory -Path $templatesPublishPath -Force | Out-Null
foreach ($templateFile in @(
    'loan-types.json',
    'checklist-templates.json',
    'checklist-templates.defaults.json'
)) {
    Copy-Item (Join-Path (Split-Path -Parent $checkerProject) $templateFile) `
        (Join-Path $templatesPublishPath $templateFile) -Force
}

foreach ($file in @('Install.ps1', 'Start-InboxUtilities.ps1', 'Stop-InboxUtilities.ps1', 'README-Install.md')) {
    Copy-Item (Join-Path $repositoryRoot "deploy\$file") (Join-Path $publishRoot $file) -Force
}

$trainingData = Join-Path $publishRoot 'LoanProcessing\tessdata\eng.traineddata'
if (-not (Test-Path -LiteralPath $trainingData)) {
    throw 'The published Loan Processing app is missing tessdata\eng.traineddata.'
}

$archivePath = "$outputPath.zip"
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

$deploymentRoot = 'Z:\General\Deployment'
$deploymentPath = Join-Path $deploymentRoot (Get-Date -Format 'dd-MM-yyyy')
$testKitArchive = Join-Path $repositoryRoot 'artifacts\InboxUtilities-Test-Kit.zip'
if (-not (Test-Path -LiteralPath $deploymentRoot -PathType Container)) {
    throw "Deployment folder is unavailable: $deploymentRoot"
}
if (-not (Test-Path -LiteralPath $testKitArchive -PathType Leaf)) {
    throw "The Inbox Utilities test kit package was not found: $testKitArchive"
}
New-Item -ItemType Directory -Path $deploymentPath -Force | Out-Null
Copy-Item -LiteralPath $archivePath -Destination $deploymentPath -Force
Copy-Item -LiteralPath $testKitArchive -Destination $deploymentPath -Force

Write-Host "Published self-contained Windows x64 package:"
Write-Host "  Folder: $publishRoot"
Write-Host "  ZIP:    $archivePath"
Write-Host "  Deployment folder: $deploymentPath"
Write-Host "  Test kit: $testKitArchive"
Write-Host 'Inbox Reader credentials are blank in the package; configure them after installation.'
