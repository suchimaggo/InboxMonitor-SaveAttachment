$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$ownedExecutables = @(
    [IO.Path]::GetFullPath((Join-Path $root 'InboxReader\InboxReader.exe')),
    [IO.Path]::GetFullPath((Join-Path $root 'Dashboard\UtilityDashboard.exe')),
    [IO.Path]::GetFullPath((Join-Path $root 'NewCase\New Case Create.exe')),
    [IO.Path]::GetFullPath((Join-Path $root 'CompletenessChecker\LoanApplicationCompletenessChecker.exe'))
)

foreach ($process in Get-Process -Name InboxReader, UtilityDashboard, 'New Case Create', LoanApplicationCompletenessChecker -ErrorAction SilentlyContinue) {
    try {
        if ($ownedExecutables -contains [IO.Path]::GetFullPath($process.Path)) {
            Stop-Process -Id $process.Id
            Write-Host "Stopped $($process.ProcessName) (PID $($process.Id))."
        }
    } catch {
        Write-Warning "Could not stop process $($process.Id): $($_.Exception.Message)"
    }
}
