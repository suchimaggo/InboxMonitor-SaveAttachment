namespace InboxReader;

public enum MailProvider
{
    Zoho,
    Outlook
}

public sealed class MailSettings
{
    public MailProvider Provider { get; set; } = MailProvider.Zoho;
    public string Email { get; set; } = "";

    // Zoho only: use an app-specific password. Prefer setting via env var InboxReader__Password.
    public string? Password { get; set; }

    // Optional override; defaults depend on Provider.
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;

    // Outlook only: Azure app registration (public client) with IMAP.AccessAsUser.All permission.
    public string? OutlookClientId { get; set; }
    public string OutlookTenantId { get; set; } = "consumers";

    public string SubjectFilter { get; set; } = "Welcome";
    public string OutputFolder { get; set; } = @"C:\Personal\InboxAttachments";
    public int PollIntervalSeconds { get; set; } = 60;

    public string ResolveHost()
    {
        if (!string.IsNullOrWhiteSpace(ImapHost))
            return ImapHost;

        return Provider switch
        {
            MailProvider.Outlook => "outlook.office365.com",
            _ => "imap.zoho.com"
        };
    }
}
