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
    double? LastNameConfidence,
    string ReviewStatus);

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
                LastNameConfidence: null,
                ReviewStatus: "Unclassified");
        }

        var firstName = GetStringField(
            document,
            "FirstName");

        var lastName = GetStringField(
            document,
            "LastName");

        string reviewStatus = DetermineReviewStatus(
            document.Confidence,
            firstName.Confidence,
            lastName.Confidence,
            firstName.Value,
            lastName.Value);

        return new IdentityDocumentResult(
            DocumentType: document.DocumentType,
            DocumentConfidence: document.Confidence,
            FirstName: firstName.Value,
            FirstNameConfidence: firstName.Confidence,
            LastName: lastName.Value,
            LastNameConfidence: lastName.Confidence,
            ReviewStatus: reviewStatus);
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

    private static string DetermineReviewStatus(
        double documentConfidence,
        double? firstNameConfidence,
        double? lastNameConfidence,
        string? firstName,
        string? lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) ||
            string.IsNullOrWhiteSpace(lastName))
        {
            return "Unclassified";
        }

        double lowestConfidence = new[]
        {
            documentConfidence,
            firstNameConfidence ?? 0,
            lastNameConfidence ?? 0
        }.Min();

        return lowestConfidence switch
        {
            >= 0.90 => "AutoNameCandidate",
            >= 0.70 => "ManualReview",
            _ => "Unclassified"
        };
    }
}