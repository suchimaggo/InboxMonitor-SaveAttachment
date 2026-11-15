$ErrorActionPreference = 'Stop'

Write-Host 'Zoho OAuth settings are stored as Windows user environment variables.'
Write-Warning 'These values are available to processes running as your Windows user. Do not run this on a shared account.'

function Read-RequiredValue([string] $Prompt) {
    do {
        $value = Read-Host -Prompt $Prompt
        if ([string]::IsNullOrWhiteSpace($value)) {
            Write-Warning 'A value is required.'
        }
    } while ([string]::IsNullOrWhiteSpace($value))

    return $value.Trim()
}

function Read-HttpsUrl([string] $Prompt, [string] $DefaultValue) {
    $value = Read-Host -Prompt "$Prompt [$DefaultValue]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = $DefaultValue
    }

    $uri = $null
    if (-not [Uri]::TryCreate($value.Trim(), [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -ne 'https') {
        throw "$Prompt must be an absolute HTTPS URL."
    }

    return $uri.AbsoluteUri.TrimEnd('/')
}

function Set-UserEnvironmentValue([string] $Name, [string] $Value) {
    [Environment]::SetEnvironmentVariable($Name, $Value, [EnvironmentVariableTarget]::User)
    [Environment]::SetEnvironmentVariable($Name, $Value, [EnvironmentVariableTarget]::Process)
    Write-Host "Saved $Name for this Windows user."
}

Set-UserEnvironmentValue 'Zoho__AccountId' (Read-RequiredValue 'Zoho Mail account ID')
Set-UserEnvironmentValue 'Zoho__ApiBaseUrl' (Read-HttpsUrl 'Zoho Mail API base URL' 'https://mail.zoho.com')
Set-UserEnvironmentValue 'Zoho__ClientId' (Read-RequiredValue 'Zoho OAuth client ID')

foreach ($name in @('Zoho__ClientSecret', 'Zoho__RefreshToken')) {
    do {
        $secureValue = Read-Host -Prompt $name -AsSecureString
        if ($secureValue.Length -eq 0) {
            Write-Warning 'A value is required.'
        }
    } while ($secureValue.Length -eq 0)

    $pointer = [IntPtr]::Zero
    $plainValue = $null
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
        $plainValue = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        Set-UserEnvironmentValue $name $plainValue
    } finally {
        $plainValue = $null
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
        $secureValue.Dispose()
    }
}

Set-UserEnvironmentValue 'Zoho__FromAddress' (Read-RequiredValue 'Sender email address')
Set-UserEnvironmentValue 'Zoho__TokenUrl' (Read-HttpsUrl 'Zoho OAuth token URL' 'https://accounts.zoho.com/oauth/v2/token')
Set-UserEnvironmentValue 'Zoho__WebMailUrl' (Read-HttpsUrl 'Zoho Mail web URL' 'https://mail.zoho.com')

Write-Host 'Zoho settings saved. Restart Inbox Utilities to use the new values.'
