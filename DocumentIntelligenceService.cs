using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using System.Globalization;

public sealed record IdentityDocumentResult(
    string? DocumentType,
    double? DocumentConfidence,
    string? FirstName,
    double? FirstNameConfidence,
    string? LastName,
    double? LastNameConfidence);

public sealed record IdentityDocumentNamingResult(
    bool IsAutoNameCandidate,
    string? SuggestedFileName,
    string? DocumentType,
    double? DocumentConfidence,
    string? FirstName,
    double? FirstNameConfidence,
    string? LastName,
    double? LastNameConfidence);

public sealed record DocumentKeyValueResult(
    string Key,
    string? Value,
    double Confidence);

public sealed record PayslipAnalysisResult(
    bool LooksLikePayslip,
    string Content,
    IReadOnlyList<DocumentKeyValueResult> KeyValuePairs,
    DateOnly? PayPeriodStart,
    DateOnly? PayPeriodEnd,
    string? PeriodLabel,
    double? PayPeriodConfidence,
    string? EmployeeNameCandidate,
    string? EmployeeNameSource);


public sealed class DocumentIntelligenceService
{
    private readonly DocumentIntelligenceClient _client;

    public DocumentIntelligenceService(
        IConfiguration configuration,
        TokenCredential credential)
    {
        var endpoint =
            configuration["DocumentIntelligence:Endpoint"];

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                "Missing DocumentIntelligence:Endpoint configuration.");
        }

        _client = new DocumentIntelligenceClient(
            new Uri(endpoint),
            credential);
    }

    public async Task<IdentityDocumentResult>
        AnalyzeIdentityDocumentAsync(
            byte[] documentBytes,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);

        if (documentBytes.Length == 0)
        {
            throw new ArgumentException(
                "Document cannot be empty.",
                nameof(documentBytes));
        }

        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            "prebuilt-idDocument",
            BinaryData.FromBytes(documentBytes),
            cancellationToken);

        AnalyzeResult result = operation.Value;

        AnalyzedDocument? document =
            result.Documents.FirstOrDefault();

        if (document is null)
        {
            return new IdentityDocumentResult(
                DocumentType: null,
                DocumentConfidence: null,
                FirstName: null,
                FirstNameConfidence: null,
                LastName: null,
                LastNameConfidence: null);
        }

        var firstName = GetStringField(
            document,
            "FirstName");

        var lastName = GetStringField(
            document,
            "LastName");

        return new IdentityDocumentResult(
            DocumentType: document.DocumentType,
            DocumentConfidence: document.Confidence,
            FirstName: firstName.Value,
            FirstNameConfidence: firstName.Confidence,
            LastName: lastName.Value,
            LastNameConfidence: lastName.Confidence);
    }

    public async Task<IdentityDocumentNamingResult>
        AnalyzeForNamingAsync(
            byte[] documentBytes,
            string originalFileName,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);

        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new ArgumentException(
                "Original filename cannot be empty.",
                nameof(originalFileName));
        }

        var result = await AnalyzeIdentityDocumentAsync(
            documentBytes,
            cancellationToken);

        bool supportedDocumentType =
            result.DocumentType is
                "idDocument.driverLicense" or
                "idDocument.passport";

        bool autoNameCandidate =
            supportedDocumentType &&
            result.DocumentConfidence >= 0.90 &&
            result.FirstNameConfidence >= 0.70 &&
            result.LastNameConfidence >= 0.70 &&
            !string.IsNullOrWhiteSpace(result.FirstName) &&
            !string.IsNullOrWhiteSpace(result.LastName);

        string? suggestedFileName = null;

        if (autoNameCandidate)
        {
            suggestedFileName = BuildFriendlyFileName(
                result.FirstName!,
                result.LastName!,
                result.DocumentType!,
                Path.GetExtension(originalFileName));
        }

        return new IdentityDocumentNamingResult(
            IsAutoNameCandidate:
                autoNameCandidate &&
                !string.IsNullOrWhiteSpace(suggestedFileName),

            SuggestedFileName:
                suggestedFileName,

            DocumentType:
                result.DocumentType,

            DocumentConfidence:
                result.DocumentConfidence,

            FirstName:
                result.FirstName,

            FirstNameConfidence:
                result.FirstNameConfidence,

            LastName:
                result.LastName,

            LastNameConfidence:
                result.LastNameConfidence);
    }

public async Task<PayslipAnalysisResult>
    AnalyzePayslipAsync(
        byte[] documentBytes,
        CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(documentBytes);

    if (documentBytes.Length == 0)
    {
        throw new ArgumentException(
            "Document cannot be empty.",
            nameof(documentBytes));
    }

    var options = new AnalyzeDocumentOptions(
        "prebuilt-layout",
        BinaryData.FromBytes(documentBytes));

    options.Features.Add(
        DocumentAnalysisFeature.KeyValuePairs);

    var operation = await _client.AnalyzeDocumentAsync(
        WaitUntil.Completed,
        options,
        cancellationToken);

    AnalyzeResult result = operation.Value;

    var pairs = result.KeyValuePairs
        .Where(pair =>
            !string.IsNullOrWhiteSpace(
                pair.Key?.Content))
        .Select(pair =>
            new DocumentKeyValueResult(
                Key: pair.Key.Content.Trim(),
                Value:
                    string.IsNullOrWhiteSpace(
                        pair.Value?.Content)
                        ? null
                        : pair.Value.Content.Trim(),
                Confidence: pair.Confidence))
        .ToArray();

    string content =
        result.Content?.Trim() ?? string.Empty;

    bool looksLikePayslip =
        ContainsPayslipIndicators(
            content,
            pairs);

    var payPeriod =
        ExtractPayPeriod(pairs);

    var employeeName =
        ExtractEmployeeNameCandidate(content);

    return new PayslipAnalysisResult(
        LooksLikePayslip: looksLikePayslip,
        Content: content,
        KeyValuePairs: pairs,
        PayPeriodStart: payPeriod.Start,
        PayPeriodEnd: payPeriod.End,
        PeriodLabel: payPeriod.Label,
        PayPeriodConfidence: payPeriod.Confidence,
        EmployeeNameCandidate: employeeName.Name,
        EmployeeNameSource: employeeName.Source);
}


    private static (string? Value, double? Confidence)
        GetStringField(
            AnalyzedDocument document,
            string fieldName)
    {
        if (!document.Fields.TryGetValue(
                fieldName,
                out DocumentField? field))
        {
            return (null, null);
        }

        string? value = field.ValueString;

        if (string.IsNullOrWhiteSpace(value))
        {
            value = field.Content;
        }

        return (
            string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim(),
            field.Confidence);
    }

    private static string? BuildFriendlyFileName(
        string firstName,
        string lastName,
        string documentType,
        string? extension)
    {
        string? documentLabel = documentType switch
        {
            "idDocument.driverLicense" => "DL",
            "idDocument.passport" => "Passport",
            _ => null
        };

        if (documentLabel is null)
            return null;

        string safeFirstName =
            SanitizeFilePart(firstName);

        string safeLastName =
            SanitizeFilePart(lastName);

        if (string.IsNullOrWhiteSpace(safeFirstName) ||
            string.IsNullOrWhiteSpace(safeLastName))
        {
            return null;
        }

        string safeExtension =
            extension?.ToLowerInvariant() switch
            {
                ".pdf" => ".pdf",
                ".jpg" => ".jpg",
                ".jpeg" => ".jpeg",
                ".png" => ".png",
                _ => ".pdf"
            };

        return
            $"{safeFirstName}_{safeLastName}_{documentLabel}{safeExtension}";
    }

    private static string SanitizeFilePart(
        string value)
    {
        var invalidCharacters =
            Path.GetInvalidFileNameChars();

        var cleaned = new string(
            value.Trim()
                .Where(character =>
                    !invalidCharacters.Contains(character))
                .ToArray());

        var parts = cleaned.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        var normalizedParts = parts.Select(part =>
            part.Length == 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) +
                  part[1..].ToLowerInvariant());

        return string.Join(
            "_",
            normalizedParts);
    }
    private static bool ContainsPayslipIndicators(
    string content,
    IReadOnlyList<DocumentKeyValueResult> pairs)
{
    string searchableText = string.Join(
        " ",
        new[]
        {
            content,
            string.Join(
                " ",
                pairs.Select(pair =>
                    $"{pair.Key} {pair.Value}"))
        });

    string[] indicators =
    [
        "payslip",
        "pay slip",
        "pay period",
        "pay date",
        "pay day",
        "gross pay",
        "net pay",
        "ordinary earnings",
        "payg withholding",
        "superannuation"
    ];

    int matches = indicators.Count(indicator =>
        searchableText.Contains(
            indicator,
            StringComparison.OrdinalIgnoreCase));

    return matches >= 2;
}
private static (
    DateOnly? Start,
    DateOnly? End,
    string? Label,
    double? Confidence)
    ExtractPayPeriod(
        IReadOnlyList<DocumentKeyValueResult> pairs)
{
    var pair = pairs.FirstOrDefault(pair =>
    {
        var normalizedKey =
            pair.Key
                .Trim()
                .Replace("\r", " ")
                .Replace("\n", " ")
                .TrimEnd(':');

        return normalizedKey.StartsWith(
            "Pay Period",
            StringComparison.OrdinalIgnoreCase);
    });

    if (pair is null ||
        string.IsNullOrWhiteSpace(pair.Value))
    {
        return (null, null, null, null);
    }

    var value =
        pair.Value.Trim();

    DateOnly start;
    DateOnly end;

    // Format 1:
    // 01-Sep-2026 to 30-Sep-2026
    var textParts = value.Split(
        " to ",
        StringSplitOptions.TrimEntries |
        StringSplitOptions.RemoveEmptyEntries);

    if (textParts.Length == 2 &&
        TryParsePayslipDate(textParts[0], out start) &&
        TryParsePayslipDate(textParts[1], out end))
    {
        return BuildPayPeriodResult(
            start,
            end,
            pair.Confidence);
    }

    // Format 2:
    // 27/09/2026 - 10/10/2026
    var dashParts = value.Split(
        " - ",
        StringSplitOptions.TrimEntries |
        StringSplitOptions.RemoveEmptyEntries);

    if (dashParts.Length == 2 &&
        TryParsePayslipDate(dashParts[0], out start) &&
        TryParsePayslipDate(dashParts[1], out end))
    {
        return BuildPayPeriodResult(
            start,
            end,
            pair.Confidence);
    }

    return (
        null,
        null,
        null,
        pair.Confidence);
}


private static bool TryParsePayslipDate(
    string value,
    out DateOnly date)
{
    string[] formats =
    [
        "dd-MMM-yyyy",
        "d-MMM-yyyy",
        "dd/MM/yyyy",
        "d/MM/yyyy"
    ];

    return DateOnly.TryParseExact(
        value.Trim(),
        formats,
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out date);
}

private static (
    DateOnly? Start,
    DateOnly? End,
    string? Label,
    double? Confidence)
    BuildPayPeriodResult(
        DateOnly start,
        DateOnly end,
        double confidence)
{
    string label;

    if (start.Year == end.Year &&
        start.Month == end.Month)
    {
        label =
            $"{start:yyyy-MM}";
    }
    else
    {
        label =
            $"{start:yyyy-MM-dd}_to_{end:yyyy-MM-dd}";
    }

    return (
        start,
        end,
        label,
        confidence);
}

private static (
    string? Name,
    string? Source)
    ExtractEmployeeNameCandidate(
        string content)
{
    if (string.IsNullOrWhiteSpace(content))
    {
        return (null, null);
    }

    var lines = content
        .Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)
        .Where(line =>
            !string.IsNullOrWhiteSpace(line))
        .ToArray();

    if (lines.Length < 2)
    {
        return (null, null);
    }

    var payMarkerIndex =
        Array.FindIndex(
            lines,
            line =>
                line.StartsWith(
                    "Pay Day",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "Pay Date",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "Payment Date",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "Pay Period",
                    StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(
                    "Employment Details",
                    StringComparison.OrdinalIgnoreCase));

    if (payMarkerIndex <= 0)
    {
        return (null, null);
    }

    // Inspect only the document header before payroll details.
    var headerLines = lines
        .Take(payMarkerIndex)
        .Take(8)
        .ToArray();

    foreach (var line in headerLines)
    {
        if (line.StartsWith(
                "Payslip",
                StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (LooksLikeOrganisationHeader(line))
        {
            continue;
        }

        var candidate =
            ExtractLeadingPersonName(line);

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return (
                candidate,
                "HeaderBeforePayPeriod");
        }
    }

    return (null, null);
}

private static bool LooksLikeOrganisationHeader(
    string line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        return true;
    }

    string normalized =
        $" {line.ToUpperInvariant()} ";

    string[] indicators =
    [
        "PAID BY",
        " PTY ",
        " LTD ",
        " LIMITED ",
        " ABN ",
        " PAYROLL ",
        " EMPLOYER "
    ];

    return indicators.Any(indicator =>
        normalized.Contains(
            indicator,
            StringComparison.OrdinalIgnoreCase));
}

private static string?
    ExtractLeadingPersonName(
        string line)
{
    if (string.IsNullOrWhiteSpace(line))
        return null;

    var tokens =
        line.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

    if (tokens.Length < 2)
        return null;

    var nameTokens =
        new List<string>();

    foreach (var token in tokens)
    {
        var cleaned =
            token.Trim(',', '.', ';', ':');

        if (string.IsNullOrWhiteSpace(cleaned))
            break;

        // Numbers normally indicate the start of an address,
        // postcode, employee number, etc.
        if (cleaned.Any(char.IsDigit))
            break;

        // Don't consume obvious payroll/header terminology.
        if (cleaned.Equals("Pay", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("Payslip", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("Payroll", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("Employee", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        // Name tokens should be predominantly alphabetic.
        if (!cleaned.All(character =>
                char.IsLetter(character) ||
                character is '-' or '\''))
        {
            break;
        }

        nameTokens.Add(cleaned);

        // Conservative maximum for this pilot.
        if (nameTokens.Count == 4)
            break;
    }

    if (nameTokens.Count is < 2 or > 4)
        return null;

    return string.Join(" ", nameTokens);
}
}