using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using System.Text;
namespace InboxReader;

public static class DocumentIntelligenceEndpoints
{
    public static WebApplication MapDocumentIntelligenceEndpoints(
        this WebApplication app)
    {
        var api = app.MapGroup("/api/document-intelligence");

        api.MapPost("/test", TestIdentityDocument)
            .WithName("TestDocumentIntelligence")
            .WithSummary("Test Document Intelligence")
            .WithDescription(
                "Analyzes one uploaded identity document without saving it.")
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> TestIdentityDocument(
        HttpRequest request,
        DocumentIntelligenceService service,
        CancellationToken cancellationToken)
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        var expectedApplicantName =
            form["expectedApplicantName"].ToString().Trim();

        if (string.IsNullOrWhiteSpace(expectedApplicantName))
        {
            return Results.BadRequest(new
            {
                error = "Expected applicant name is required."
            });
        }

        if (file is null)
        {
            return Results.BadRequest(new
            {
                error = "Document file is required."
            });
        }

        if (file.Length == 0)
            return Results.BadRequest(new { error = "Empty file." });

        const long maxBytes = 4 * 1024 * 1024;

        if (file.Length > maxBytes)
            return Results.BadRequest(new
            {
                error = "Test file exceeds the 4 MB pilot limit."
            });

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        IdentityDocumentResult result =
            await service.AnalyzeIdentityDocumentAsync(
                memory.ToArray(),
                cancellationToken);

        var expectedName = NormalizeName(expectedApplicantName);
        var extractedName = NormalizeName(
            $"{result.FirstName} {result.LastName}");

        var nameMatches =
            !string.IsNullOrWhiteSpace(expectedName) &&
            string.Equals(
                expectedName,
                extractedName,
                StringComparison.OrdinalIgnoreCase);

        var supportedDocumentType =
            result.DocumentType is
                "idDocument.driverLicense" or
                "idDocument.passport";

        var autoNameCandidate =
            supportedDocumentType &&
            result.DocumentConfidence >= 0.90 &&
            result.FirstNameConfidence >= 0.70 &&
            result.LastNameConfidence >= 0.70;

        var reviewStatus = autoNameCandidate
            ? "AutoNameCandidate"
            : "ManualReview";
        string? suggestedFileName = null;

        if (autoNameCandidate)
        {
            suggestedFileName = BuildFriendlyFileName(
                result.FirstName,
                result.LastName,
                result.DocumentType,
                Path.GetExtension(file.FileName));
        }

        return Results.Ok(new
        {
            success = true,
            detectedDocumentType = result.DocumentType,
            documentConfidence = result.DocumentConfidence,
            firstName = result.FirstName,
            firstNameConfidence = result.FirstNameConfidence,
            lastName = result.LastName,
            lastNameConfidence = result.LastNameConfidence,
            expectedApplicantName,
            nameMatches,
            reviewStatus,
            suggestedFileName
        });
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string decomposed =
            value.Normalize(NormalizationForm.FormD);

        var builder = new StringBuilder();

        foreach (char character in decomposed)
        {
            var category =
                CharUnicodeInfo.GetUnicodeCategory(character);

            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToUpperInvariant(character));
        }

        return builder
            .ToString()
            .Normalize(NormalizationForm.FormC);
    }

    private static string? BuildFriendlyFileName(
        string? firstName,
        string? lastName,
        string? documentType,
        string? extension)
    {
        if (string.IsNullOrWhiteSpace(firstName) ||
            string.IsNullOrWhiteSpace(lastName))
        {
            return null;
        }

        var documentLabel = documentType switch
        {
            "idDocument.driverLicense" => "DL",
            "idDocument.passport" => "Passport",
            _ => null
        };

        if (documentLabel is null)
            return null;

        var safeFirstName = SanitizeFilePart(firstName);
        var safeLastName = SanitizeFilePart(lastName);

        if (string.IsNullOrWhiteSpace(safeFirstName) ||
            string.IsNullOrWhiteSpace(safeLastName))
        {
            return null;
        }

        var safeExtension =
            extension?.ToLowerInvariant() switch
            {
                ".pdf" => ".pdf",
                ".jpg" => ".jpg",
                ".jpeg" => ".jpeg",
                ".png" => ".png",
                _ => ".pdf"
            };

        return $"{safeFirstName}_{safeLastName}_{documentLabel}{safeExtension}";
    }

    private static string SanitizeFilePart(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();

        var cleaned = new string(
            value.Trim()
                .Where(c => !invalidChars.Contains(c))
                .ToArray());

        var parts = cleaned.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        var normalizedParts = parts.Select(part =>
            part.Length == 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) +
                  part[1..].ToLowerInvariant());

        return string.Join("_", normalizedParts);
    }
}