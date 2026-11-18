using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Configuration;

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
    IReadOnlyList<DocumentKeyValueResult> KeyValuePairs);


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

    return new PayslipAnalysisResult(
        LooksLikePayslip: looksLikePayslip,
        Content: content,
        KeyValuePairs: pairs);
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
}