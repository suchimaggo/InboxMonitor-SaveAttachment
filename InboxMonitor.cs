using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using Azure.Core;

namespace InboxReader;

public sealed class InboxMonitor(
    MailSettings settings,
    MicrosoftGraphInboxClient graph,
    Action<string>? log = null)
{
    internal static readonly Regex CaseIdPattern = new(
        @"\b[A-Z0-9]{2,10}-[23456789A-HJ-NP-Z]{6}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly Action<string> _log = log ?? Console.WriteLine;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ValidateSettings();
        _log($"Monitoring Microsoft Graph mailbox {settings.Mailbox} for messages delivered on or after {settings.ScanFromDate:yyyy-MM-dd}.");
        _log("Case and unmatched attachments are stored in the configured SharePoint document library.");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var saved = await graph.ProcessInboxAsync(settings, _log, cancellationToken);
                _log($"[{DateTime.Now:T}] Check complete. {saved} new attachment(s) saved.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
            {
                _log($"[{DateTime.Now:T}] Microsoft Graph polling failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(settings.Mailbox))
            throw new InvalidOperationException("InboxReader:Mailbox must be configured.");
        if (!System.Net.Mail.MailAddress.TryCreate(settings.Mailbox, out _))
            throw new InvalidOperationException("InboxReader:Mailbox must be a valid mailbox address.");
        if (settings.ScanFromDate == default)
            throw new InvalidOperationException("InboxReader:ScanFromDate must be configured.");
        if (settings.PollIntervalSeconds is < 30 or > 3600)
            throw new InvalidOperationException("InboxReader:PollIntervalSeconds must be between 30 and 3600.");
    }
}

public sealed class MicrosoftGraphInboxClient(
    HttpClient httpClient,
    IConfiguration configuration,
    TokenCredential credential,
    DocumentIntelligenceService documentIntelligence)
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const long MaximumAttachmentBytes = 100L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> ProcessInboxAsync(
        MailSettings settings,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        var mailbox = Uri.EscapeDataString(settings.Mailbox);
        var date = Uri.EscapeDataString(settings.ScanFromDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var query =
            $"{GraphBaseUrl}/users/{mailbox}/mailFolders/inbox/messages" +
            $"?$select=id,subject,receivedDateTime,hasAttachments&$filter=receivedDateTime ge {date} and hasAttachments eq true&$top=100";
        Uri? nextUri = new(query);
        var saved = 0;
        var cases = new Dictionary<string, CaseFolder?>(StringComparer.OrdinalIgnoreCase);

        while (nextUri is not null)
        {
            using var page = await SendGraphAsync(HttpMethod.Get, nextUri, accessToken, cancellationToken);
            if (!page.RootElement.TryGetProperty("value", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Microsoft Graph did not return the mailbox message list.");

            foreach (var message in messages.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var subject = GetString(message, "subject") ?? "";
                var messageId = GetRequiredString(message, "id");
                var receivedAt = message.TryGetProperty("receivedDateTime", out var receivedDate) &&
                                 receivedDate.TryGetDateTimeOffset(out var parsedDate)
                    ? parsedDate
                    : DateTimeOffset.UtcNow;
                var destination = await ResolveDestinationAsync(
                    subject,
                    receivedAt,
                    cases,
                    accessToken,
                    cancellationToken);
                var attachmentCount = await SaveMessageAttachmentsAsync(
                    mailbox,
                    messageId,
                    receivedAt,
                    destination,
                    accessToken,
                    log,
                    cancellationToken);
                saved += attachmentCount;
                if (attachmentCount > 0)
                    log($"[{DateTime.Now:T}] Saved {attachmentCount} attachment(s) from \"{subject}\" to SharePoint.");
            }

            nextUri = page.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) &&
                      Uri.TryCreate(nextLink.GetString(), UriKind.Absolute, out var parsedNextLink) &&
                      string.Equals(parsedNextLink.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                ? parsedNextLink
                : null;
        }

        return saved;
    }

    private async Task<AttachmentDestination> ResolveDestinationAsync(
        string subject,
        DateTimeOffset receivedAt,
        IDictionary<string, CaseFolder?> cases,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var matches = InboxMonitor.CaseIdPattern.Matches(subject);
        if (matches.Count == 1)
        {
            var caseId = matches[0].Value;
            if (!cases.TryGetValue(caseId, out var caseFolder))
            {
                caseFolder = await FindCaseAsync(caseId, accessToken, cancellationToken);
                cases[caseId] = caseFolder;
            }

            if (caseFolder is not null)
            {
                var expectedReference = $"{caseFolder.ClientName}-{caseId}";
                if (subject.Contains(expectedReference, StringComparison.OrdinalIgnoreCase))
                {
                    var receivedPath = $"{GetParentPath()}/{caseFolder.FolderName}/Received";
                    await EnsureFolderPathAsync(receivedPath, accessToken, cancellationToken);
                    return new AttachmentDestination(receivedPath);
                }
            }
        }

        var unmatchedPath = configuration["SharePoint:UnmatchedFolderPath"] ?? "InboxAttachments";
        await EnsureFolderPathAsync(unmatchedPath, accessToken, cancellationToken);
        return new AttachmentDestination(unmatchedPath);
    }

    private async Task<int> SaveMessageAttachmentsAsync(
        string mailbox,
        string messageId,
        DateTimeOffset receivedAt,
        AttachmentDestination destination,
        string accessToken,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"{GraphBaseUrl}/users/{mailbox}/messages/{Uri.EscapeDataString(messageId)}/attachments" +
            "?$select=id,name,size,isInline&$top=100");
        using var response = await SendGraphAsync(HttpMethod.Get, uri, accessToken, cancellationToken);
        if (!response.RootElement.TryGetProperty("value", out var attachments) ||
            attachments.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Microsoft Graph did not return the message attachments.");

        var messageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..12];
        var saved = 0;
        foreach (var attachment in attachments.EnumerateArray())
        {
            if (!GetString(attachment, "@odata.type")?.EndsWith(
                    "fileAttachment", StringComparison.OrdinalIgnoreCase) ?? true)
                continue;
            if (attachment.TryGetProperty("isInline", out var isInline) && isInline.ValueKind == JsonValueKind.True)
                continue;

            var name = Sanitize(GetString(attachment, "name") ?? "attachment", 140);
            var attachmentId = GetRequiredString(attachment, "id");
            if (!attachment.TryGetProperty("size", out var sizeElement) ||
                !sizeElement.TryGetInt64(out var size) ||
                size is < 0 or > MaximumAttachmentBytes)
            {
                log($"[{DateTime.Now:T}] Skipped attachment '{name}' because it is larger than 100 MB or its size was not reported.");
                continue;
            }

           var attachmentKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(attachmentId)))[..12];

            if (await DestinationContainsAttachmentKeyAsync(
                destination.FolderPath,
                attachmentKey,
                accessToken,
                cancellationToken))
            {
                continue;
            }
            // Preserve the existing deterministic technical filename as the fallback.
var technicalFileName = Sanitize(
    $"{receivedAt.UtcDateTime:yyyyMMdd_HHmmss}_{messageKey}_{attachmentKey}_{name}",
    180);

var technicalFilePath =
    $"{destination.FolderPath}/{technicalFileName}";

// Preserve the existing duplicate-message protection.
// If this attachment was previously saved using the technical filename,
// do not download/process it again.
if (await DriveItemExistsAsync(
        technicalFilePath,
        accessToken,
        cancellationToken))
{
    continue;
}

var contentUri = new Uri(
    $"{GraphBaseUrl}/users/{mailbox}/messages/{Uri.EscapeDataString(messageId)}" +
    $"/attachments/{Uri.EscapeDataString(attachmentId)}/$value");

using var download = await SendGraphContentAsync(
    HttpMethod.Get,
    contentUri,
    accessToken,
    cancellationToken);

var bytes =
    await download.Content.ReadAsByteArrayAsync(
        cancellationToken);

if (bytes.LongLength > MaximumAttachmentBytes)
{
    log(
        $"[{DateTime.Now:T}] Skipped attachment '{name}' because it exceeds 100 MB.");

    continue;
}

// Default to the existing technical filename.
// Document Intelligence is an enhancement, not a requirement.
var finalFileName = technicalFileName;

const long maximumDocumentIntelligenceBytes =
    4L * 1024 * 1024;

var extension =
    Path.GetExtension(name);

var documentIntelligenceEligible =
    bytes.LongLength > 0 &&
    bytes.LongLength <= maximumDocumentIntelligenceBytes &&
    extension is not null &&
    (
        extension.Equals(
            ".pdf",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".jpg",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".jpeg",
            StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(
            ".png",
            StringComparison.OrdinalIgnoreCase)
    );

if (documentIntelligenceEligible)
{
    try
    {
        var namingResult =
            await documentIntelligence.AnalyzeForNamingAsync(
                bytes,
                name,
                cancellationToken);

        if (namingResult.IsAutoNameCandidate &&
            !string.IsNullOrWhiteSpace(
                namingResult.SuggestedFileName))
        {
           var suggestedExtension =
    Path.GetExtension(
        namingResult.SuggestedFileName);

var suggestedBaseName =
    Path.GetFileNameWithoutExtension(
        namingResult.SuggestedFileName);

var requiredSuffix =
    $"_{attachmentKey}{suggestedExtension}";

var maximumBaseNameLength =
    180 - requiredSuffix.Length;

suggestedBaseName = Sanitize(
    suggestedBaseName,
    maximumBaseNameLength);

finalFileName =
    $"{suggestedBaseName}{requiredSuffix}";

            log(
                $"[{DateTime.Now:T}] Document Intelligence suggested friendly filename '{finalFileName}'.");
        }
    }
    catch (OperationCanceledException)
        when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        log(
            $"[{DateTime.Now:T}] Document Intelligence naming failed for '{name}'. Using existing technical filename. {exception.Message}");

        finalFileName = technicalFileName;
    }
}

var finalFilePath =
    $"{destination.FolderPath}/{finalFileName}";

// A final collision means this exact target already exists.
if (await DriveItemExistsAsync(
        finalFilePath,
        accessToken,
        cancellationToken))
{
    continue;
}

await UploadFileAsync(
    finalFilePath,
    bytes,
    accessToken,
    cancellationToken);

saved++;
        }

        return saved;
    }

    private async Task<CaseFolder?> FindCaseAsync(
        string caseId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var siteId = RequiredSetting("SharePoint:SiteId");
        var listId = RequiredSetting("SharePoint:ListId");
        var filter = Uri.EscapeDataString($"fields/CaseID eq '{caseId.Replace("'", "''", StringComparison.Ordinal)}'");
        using var items = await SendGraphAsync(
            HttpMethod.Get,
            new Uri($"{GraphBaseUrl}/sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}" +
                    $"/items?$expand=fields&$filter={filter}"),
            accessToken,
            cancellationToken);
        if (!items.RootElement.TryGetProperty("value", out var values) ||
            values.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Microsoft Graph did not return the Case Register item list.");

        var matches = values.EnumerateArray().ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"Case Register contains duplicate CaseID values for {caseId}.");
        if (matches.Length == 0)
            return null;

        var fields = matches[0].GetProperty("fields");
        var name = GetRequiredString(fields, "ClientName");
        var folder = GetRequiredString(fields, "FolderName");
        return new CaseFolder(name, folder);
    }

    private async Task EnsureFolderPathAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var driveId = Uri.EscapeDataString(RequiredSetting("SharePoint:DriveId"));
        using var root = await SendGraphAsync(
            HttpMethod.Get,
            new Uri($"{GraphBaseUrl}/drives/{driveId}/root"),
            accessToken,
            cancellationToken);
        var parentId = GetRequiredString(root.RootElement, "id");
        var currentPath = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}/{segment}";
            var existing = await TryGetDriveItemAsync(currentPath, accessToken, cancellationToken);
            if (existing is not null)
            {
                parentId = GetRequiredString(existing.RootElement, "id");
                continue;
            }

            using var created = await SendGraphAsync(
                HttpMethod.Post,
                new Uri($"{GraphBaseUrl}/drives/{driveId}/items/{Uri.EscapeDataString(parentId)}/children"),
                accessToken,
                cancellationToken,
                new Dictionary<string, object?>
                {
                    ["name"] = segment,
                    ["folder"] = new { },
                    ["@microsoft.graph.conflictBehavior"] = "fail"
                });
            parentId = GetRequiredString(created.RootElement, "id");
        }
    }

    private async Task<bool> DriveItemExistsAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var item = await TryGetDriveItemAsync(path, accessToken, cancellationToken);
        return item is not null;
    }
    private async Task<bool>
    DestinationContainsAttachmentKeyAsync(
        string folderPath,
        string attachmentKey,
        string accessToken,
        CancellationToken cancellationToken)
{
    var driveId =
        Uri.EscapeDataString(
            RequiredSetting("SharePoint:DriveId"));

    var escapedFolderPath =
        EscapePath(folderPath);

    Uri? nextUri = new(
        $"{GraphBaseUrl}/drives/{driveId}/root:/{escapedFolderPath}:/children" +
        "?$select=name&$top=200");

    while (nextUri is not null)
    {
        using var page = await SendGraphAsync(
            HttpMethod.Get,
            nextUri,
            accessToken,
            cancellationToken);

        if (!page.RootElement.TryGetProperty(
                "value",
                out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Microsoft Graph did not return the destination folder contents.");
        }

        foreach (var item in items.EnumerateArray())
        {
            var existingName =
                GetString(item, "name");

            if (!string.IsNullOrWhiteSpace(existingName) &&
                existingName.Contains(
                    attachmentKey,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        nextUri =
            page.RootElement.TryGetProperty(
                "@odata.nextLink",
                out var nextLink) &&
            Uri.TryCreate(
                nextLink.GetString(),
                UriKind.Absolute,
                out var parsedNextLink) &&
            string.Equals(
                parsedNextLink.Host,
                "graph.microsoft.com",
                StringComparison.OrdinalIgnoreCase)
                ? parsedNextLink
                : null;
    }

    return false;
}
    private async Task<JsonDocument?> TryGetDriveItemAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var escapedPath = EscapePath(path);
        try
        {
            return await SendGraphAsync(
                HttpMethod.Get,
                new Uri($"{GraphBaseUrl}/drives/{Uri.EscapeDataString(RequiredSetting("SharePoint:DriveId"))}/root:/{escapedPath}"),
                accessToken,
                cancellationToken);
        }
        catch (GraphRequestException exception) when (exception.GraphStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task UploadFileAsync(
        string path,
        byte[] contents,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{GraphBaseUrl}/drives/{Uri.EscapeDataString(RequiredSetting("SharePoint:DriveId"))}/root:/{EscapePath(path)}:/content"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new ByteArrayContent(contents);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateGraphExceptionAsync(response, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]),
            cancellationToken);
        return token.Token;
    }

    private async Task<JsonDocument> SendGraphAsync(
        HttpMethod method,
        Uri uri,
        string accessToken,
        CancellationToken cancellationToken,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateGraphExceptionAsync(response, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(content);
    }

    private async Task<HttpResponseMessage> SendGraphContentAsync(
        HttpMethod method,
        Uri uri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var exception = await CreateGraphExceptionAsync(response, cancellationToken);
            response.Dispose();
            throw exception;
        }

        return response;
    }

    private static async Task<GraphRequestException> CreateGraphExceptionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return new GraphRequestException(
            response.StatusCode,
            $"Microsoft Graph request failed ({(int)response.StatusCode}): {Limit(content)}");
    }

    private string GetParentPath()
    {
        var path = configuration["SharePoint:ParentFolderPath"]?.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(path) ? "" : path;
    }

    private string RequiredSetting(string key) =>
        !string.IsNullOrWhiteSpace(configuration[key])
            ? configuration[key]!
            : throw new InvalidOperationException($"Configure '{key}' for the Microsoft Graph inbox integration.");

    private static string EscapePath(string path) =>
        string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        GetString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Microsoft Graph response did not contain '{propertyName}'.");

    private static string Sanitize(string value, int maxLength)
    {
        var invalid = "<>:\"/\\|?*".ToHashSet();
        var safe = new string(value.Select(character =>
            char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (safe.Length > maxLength)
            safe = safe[..maxLength].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "attachment" : safe;
    }

    private static string Limit(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 500 ? text : text[..500];
    }

    private sealed record CaseFolder(string ClientName, string FolderName);
    private sealed record AttachmentDestination(string FolderPath);
    private sealed class GraphRequestException(HttpStatusCode statusCode, string message) : HttpRequestException(message)
    {
        public HttpStatusCode GraphStatusCode { get; } = statusCode;
    }
}
