using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace InboxReader;

public sealed class InboxMonitor(
    MailSettings settings,
    Action<string>? log = null,
    Func<CancellationToken, Task>? pollCompleted = null)
{
    private static readonly Regex CaseIdPattern = new(
        @"\b(?:FHB|REFI|PUR|INV|CONST|OTHER)-[23456789A-HJ-NP-Z]{6}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonDocumentOptions CaseJsonOptions = new() { MaxDepth = 16 };
    private readonly OutlookTokenProvider? _tokenProvider =
        settings.Provider == MailProvider.Outlook ? new OutlookTokenProvider(settings, log) : null;
    private readonly Action<string> _log = log ?? Console.WriteLine;

    public async Task RunAsync(CancellationToken ct)
    {
        ValidateSettings();

        Directory.CreateDirectory(settings.OutputFolder);
        if (!string.IsNullOrWhiteSpace(settings.CaseRootFolder))
            Directory.CreateDirectory(ResolveCaseRootFolder());
        _log($"Monitoring {settings.Provider} inbox for messages delivered on or after {settings.ScanFromDate:yyyy-MM-dd}, matching case subjects in the format Client Name-CASE-ID.");
        _log($"Unmatched email attachments will be saved to: {settings.OutputFolder}");
        if (!string.IsNullOrWhiteSpace(settings.CaseRootFolder))
            _log($"Case attachment tracking is enabled: {ResolveCaseRootFolder()}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckInboxAsync(ct);
                if (pollCompleted is not null)
                {
                    try
                    {
                        await pollCompleted(ct);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                    {
                        _log($"[{DateTime.Now:T}] Case tracking refresh failed: {exception.Message}");
                    }
                }
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

        var uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(settings.ScanFromDate.Date), ct);
        var saved = 0;

        if (uids.Count > 0)
        {
            var summaries = await inbox.FetchAsync(uids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure, ct);

            foreach (var summary in summaries)
            {
                var subject = summary.Envelope?.Subject ?? "";
                if (!summary.Attachments.Any())
                    continue;

                var date = summary.Envelope?.Date?.LocalDateTime ?? DateTime.Now;
                var folderName = $"{date:yyyyMMdd_HHmmss}_{Sanitize(subject, 60)}_{summary.UniqueId.Id}";
                string? caseFolder = null;
                string? expectedCaseSubject = null;
                if (!string.IsNullOrWhiteSpace(settings.CaseRootFolder))
                {
                    var caseIdMatches = CaseIdPattern.Matches(subject);
                    if (caseIdMatches.Count == 1 &&
                        TryResolveCaseFolder(caseIdMatches[0].Value, out caseFolder, out expectedCaseSubject))
                    {
                        if (!subject.Contains(expectedCaseSubject!, StringComparison.OrdinalIgnoreCase))
                        {
                            _log($"[{DateTime.Now:T}] Subject \"{subject}\" contains case ID {caseIdMatches[0].Value} but not the matching client-case reference \"{expectedCaseSubject}\"; saving attachments to the unmatched inbox folder.");
                            caseFolder = null;
                        }
                    }
                    else if (caseIdMatches.Count > 0)
                    {
                        _log($"[{DateTime.Now:T}] Subject \"{subject}\" contains an ambiguous or unknown case ID; saving attachments to the unmatched inbox folder.");
                    }
                }

                var folderPath = caseFolder is null
                    ? Path.Combine(settings.OutputFolder, folderName)
                    : Path.Combine(caseFolder, "Received", folderName);

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

    private string ResolveCaseRootFolder() =>
        Path.GetFullPath(settings.CaseRootFolder!, AppContext.BaseDirectory);

    private bool TryResolveCaseFolder(string caseId, out string? caseFolder, out string? expectedCaseSubject)
    {
        caseFolder = null;
        expectedCaseSubject = null;
        var root = ResolveCaseRootFolder();
        if (!Directory.Exists(root))
            return false;

        var matchingRecords = Directory.EnumerateFiles(root, "case.json", SearchOption.AllDirectories)
            .Where(path => string.Equals(
                Path.GetFileName(Path.GetDirectoryName(path)),
                caseId,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matchingRecords.Length != 1)
            return false;

        var recordPath = matchingRecords[0];
        var candidate = Path.GetDirectoryName(recordPath);
        if (candidate is null || !File.Exists(recordPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(recordPath), CaseJsonOptions);
            if (!document.RootElement.TryGetProperty("caseId", out var storedId) ||
                storedId.ValueKind != JsonValueKind.String ||
                !string.Equals(storedId.GetString(), caseId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!document.RootElement.TryGetProperty("clientName", out var storedClientName) ||
                storedClientName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(storedClientName.GetString()))
                return false;
            expectedCaseSubject = $"{storedClientName.GetString()!.Trim()}-{caseId}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"[{DateTime.Now:T}] Could not validate case record for {caseId}: {exception.Message}");
            return false;
        }

        caseFolder = candidate;
        return true;
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
