namespace InboxReader;

public sealed class MailSettings
{
    public string Mailbox { get; set; } = "";
    public DateTime ScanFromDate { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
}
