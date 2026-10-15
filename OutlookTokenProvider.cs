using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace InboxReader;

public sealed class OutlookTokenProvider
{
    private static readonly string[] Scopes =
    [
        "https://outlook.office.com/IMAP.AccessAsUser.All",
        "offline_access"
    ];

    private readonly IPublicClientApplication _app;
    private readonly string _email;
    private readonly Action<string> _log;
    private bool _cacheRegistered;

    public OutlookTokenProvider(MailSettings settings, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(settings.OutlookClientId))
            throw new InvalidOperationException("OutlookClientId must be configured for the Outlook provider.");

        _email = settings.Email;
        _log = log ?? Console.WriteLine;
        _app = PublicClientApplicationBuilder.Create(settings.OutlookClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, settings.OutlookTenantId)
            .WithDefaultRedirectUri()
            .Build();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!_cacheRegistered)
        {
            // Persist tokens (DPAPI-encrypted on Windows) so sign-in is only needed once.
            var storage = new StorageCreationPropertiesBuilder(
                    "msal_token_cache.bin",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InboxReader"))
                .Build();
            var helper = await MsalCacheHelper.CreateAsync(storage);
            helper.RegisterCache(_app.UserTokenCache);
            _cacheRegistered = true;
        }

        var accounts = await _app.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => string.Equals(a.Username, _email, StringComparison.OrdinalIgnoreCase))
                      ?? accounts.FirstOrDefault();

        if (account is not null)
        {
            try
            {
                var silent = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
            }
        }

        var result = await _app.AcquireTokenWithDeviceCode(Scopes, code =>
        {
            _log(code.Message);
            return Task.CompletedTask;
        }).ExecuteAsync(ct);

        return result.AccessToken;
    }
}
