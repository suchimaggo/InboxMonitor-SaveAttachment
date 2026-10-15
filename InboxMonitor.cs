using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace InboxReader;

public sealed class InboxMonitor(MailSettings settings, Action<string>? log = null)
{
    private readonly OutlookTokenProvider? _tokenProvider =
        settings.Provider == MailProvider.Outlook ? new OutlookTokenProvider(settings, log) : null;
    private readonly Action<string> _log = log ?? Console.WriteLine;

    public async Task RunAsync(CancellationToken ct)
    {
        ValidateSettings();

        Directory.CreateDirectory(settings.OutputFolder);
        _log($"Monitoring {settings.Provider} inbox for '{settings.Email}' (subject contains '{settings.SubjectFilter}').");
        _log($"Saving attachments to: {settings.OutputFolder}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckInboxAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[{DateTime.Now:T}] Error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(settings.Email))
            throw new InvalidOperationException("Email is required.");

        if (settings.Provider == MailProvider.Outlook && string.IsNullOrWhiteSpace(settings.OutlookClientId))
            throw new InvalidOperationException("OutlookClientId must be configured for the Outlook provider.");

        if (settings.Provider != MailProvider.Outlook && string.IsNullOrWhiteSpace(settings.Password))
            throw new InvalidOperationException("Password must be configured for the Zoho provider.");

        if (string.IsNullOrWhiteSpace(settings.ResolveHost()))
            throw new InvalidOperationException("IMAP host is not configured.");
    }

    private async Task CheckInboxAsync(CancellationToken ct)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(settings.ResolveHost(), settings.ImapPort, SecureSocketOptions.SslOnConnect, ct);

        if (_tokenProvider is not null)
        {
            var token = await _tokenProvider.GetAccessTokenAsync(ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.Email, token), ct);
        }
        else
        {
            if (string.IsNullOrEmpty(settings.Password))
                throw new InvalidOperationException("Password must be configured for the Zoho provider.");
            await client.AuthenticateAsync(settings.Email, settings.Password, ct);
        }

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = await inbox.SearchAsync(SearchQuery.SubjectContains(settings.SubjectFilter), ct);
        var saved = 0;

        if (uids.Count > 0)
        {
            var summaries = await inbox.FetchAsync(uids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, ct);

            foreach (var summary in summaries)
            {
                // Server-side SEARCH may be fuzzy; enforce the filter locally too.
                var subject = summary.Envelope?.Subject ?? "";
                if (!subject.Contains(settings.SubjectFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!summary.Attachments.Any())
                    continue;

                var date = summary.Envelope?.Date?.LocalDateTime ?? DateTime.Now;
                var folderName = $"{date:yyyyMMdd_HHmmss}_{Sanitize(subject, 60)}_{summary.UniqueId.Id}";
                var folderPath = Path.Combine(settings.OutputFolder, folderName);

                if (Directory.Exists(folderPath))
                    continue;

                var message = await inbox.GetMessageAsync(summary.UniqueId, ct);
                var count = await SaveAttachmentsAsync(message, folderPath, ct);
                if (count > 0)
                {
                    saved++;
                    _log($"[{DateTime.Now:T}] Saved {count} attachment(s) from \"{subject}\" -> {folderPath}");
                }
            }
        }

        _log($"[{DateTime.Now:T}] Check complete. {saved} new email(s) processed.");
        await client.DisconnectAsync(true, ct);
    }

    private static async Task<int> SaveAttachmentsAsync(MimeMessage message, string folderPath, CancellationToken ct)
    {
        // Write to a temp folder first so a partial failure doesn't mark the email as processed.
        var tempPath = folderPath + ".partial";
        if (Directory.Exists(tempPath))
            Directory.Delete(tempPath, true);
        Directory.CreateDirectory(tempPath);

        var count = 0;
        foreach (var attachment in message.Attachments)
        {
            var fileName = attachment switch
            {
                MimePart part => part.FileName,
                MessagePart msgPart => (msgPart.Message?.Subject ?? "attached-message") + ".eml",
                _ => null
            };

            var safeName = Sanitize(string.IsNullOrWhiteSpace(fileName) ? $"attachment_{count + 1}" : fileName, 150);
            var filePath = GetUniquePath(tempPath, safeName);

            await using var stream = File.Create(filePath);
            if (attachment is MessagePart { Message: not null } rfc822)
                await rfc822.Message.WriteToAsync(stream, ct);
            else if (attachment is MimePart { Content: not null } part)
                await part.Content.DecodeToAsync(stream, ct);

            count++;
        }

        if (count == 0)
        {
            Directory.Delete(tempPath, true);
            return 0;
        }

        Directory.Move(tempPath, folderPath);
        return count;
    }

    private static string GetUniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}){ext}");
        return path;
    }

    // Replaces path separators and invalid characters to prevent path traversal from untrusted names.
    private static string Sanitize(string value, int maxLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(invalid.Contains(c) || c is '/' or '\\' ? '_' : c);

        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length > maxLength)
            result = result[..maxLength].TrimEnd();
        return string.IsNullOrEmpty(result) || result is "." or ".." ? "untitled" : result;
    }
}
