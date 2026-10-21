[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\InboxUtilities-macOS-arm64')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$expectedParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $outputPath.StartsWith($expectedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside '$expectedParent'."
}

$dashboardProject = Join-Path $repositoryRoot 'UtilityDashboard\UtilityDashboard.csproj'
$inboxProject = Join-Path $repositoryRoot 'InboxReader.csproj'
$newCaseProject = Join-Path $repositoryRoot '..\New Case Create\New Case Create.csproj'
$loanProject = Join-Path $repositoryRoot '..\LoanProcessing-Utility\LoanProcessing-Utility.csproj'
$publishRoot = Join-Path $outputPath 'InboxUtilities'

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot | Out-Null

$publishArguments = @('publish', '-c', 'Release', '-r', 'osx-arm64', '--self-contained', 'true')
& dotnet @publishArguments $inboxProject '-o' (Join-Path $publishRoot 'InboxReader')
if ($LASTEXITCODE -ne 0) { throw 'Publishing Inbox Reader for macOS failed.' }
& dotnet @publishArguments $dashboardProject '-o' (Join-Path $publishRoot 'Dashboard')
if ($LASTEXITCODE -ne 0) { throw 'Publishing Utility Dashboard for macOS failed.' }
& dotnet @publishArguments $newCaseProject '-o' (Join-Path $publishRoot 'NewCase')
if ($LASTEXITCODE -ne 0) { throw 'Publishing New Loan Case for macOS failed.' }
Copy-Item (Join-Path (Split-Path -Parent $newCaseProject) 'README.md') `
    (Join-Path $publishRoot 'NewCase\README.md') -Force
& dotnet @publishArguments $loanProject '-o' (Join-Path $publishRoot 'LoanProcessing')
if ($LASTEXITCODE -ne 0) { throw 'Publishing Loan Processing for macOS failed.' }
Copy-Item (Join-Path $repositoryRoot 'deploy\macOS\InboxReader.appsettings.json') `
    (Join-Path $publishRoot 'InboxReader\appsettings.json') -Force
Copy-Item (Join-Path $repositoryRoot 'deploy\macOS\UtilityDashboard.appsettings.json') `
    (Join-Path $publishRoot 'Dashboard\appsettings.json') -Force

foreach ($file in @('Install.command', 'Start.command', 'Stop.command', 'README-Install.md')) {
    Copy-Item (Join-Path $repositoryRoot "deploy\macOS\$file") (Join-Path $publishRoot $file) -Force
}

$trainingData = Join-Path $publishRoot 'LoanProcessing\tessdata\eng.traineddata'
if (-not (Test-Path -LiteralPath $trainingData)) {
    throw 'The macOS Loan Processing publish is missing tessdata\eng.traineddata.'
}

$archivePath = "$outputPath.zip"
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host 'Published Apple silicon macOS package:'
Write-Host "  Folder: $publishRoot"
Write-Host "  ZIP:    $archivePath"
Write-Host 'The macOS package includes no mailbox credentials.'
