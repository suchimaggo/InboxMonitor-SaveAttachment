namespace InboxReader;

public enum MailProvider
{
    Zoho,
    Outlook
}

public sealed class MailSettings
{
    // MailExchangeServer takes precedence; Provider remains for older configurations.
    public MailProvider Provider { get; set; } = MailProvider.Zoho;
    public MailProvider? MailExchangeServer { get; set; }
    public string Email { get; set; } = "";

    // Zoho only: use an app-specific password. Prefer setting via env var InboxReader__Password.
    public string? Password { get; set; }

    // Zoho-only host override. Outlook uses the Exchange Online IMAP endpoint.
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;

    // Outlook only: Azure app registration (public client) with IMAP.AccessAsUser.All permission.
    public string? OutlookClientId { get; set; }
    public string OutlookTenantId { get; set; } = "consumers";

    public string SubjectFilter { get; set; } = "";
    public DateTime ScanFromDate { get; set; }
    public string OutputFolder { get; set; } = @"Z:\LoanProcessing-AutoMation\InboxAttachments";
    public int PollIntervalSeconds { get; set; } = 60;
    public string? CaseRootFolder { get; set; } = @"Z:\LoanProcessing-AutoMation";
    public string? TrackingRefreshUrl { get; set; }

    public MailProvider SelectedProvider => MailExchangeServer ?? Provider;

    public string ResolveHost()
    {
        return SelectedProvider switch
        {
            MailProvider.Outlook => "outlook.office365.com",
            MailProvider.Zoho => string.IsNullOrWhiteSpace(ImapHost) ? "imap.zoho.com" : ImapHost,
            _ => throw new InvalidOperationException($"Unsupported mail exchange: {SelectedProvider}.")
        };
    }
}
