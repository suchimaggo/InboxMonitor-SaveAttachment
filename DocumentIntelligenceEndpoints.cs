using Microsoft.AspNetCore.Http.HttpResults;

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
        IFormFile file,
        DocumentIntelligenceService service,
        CancellationToken cancellationToken)
    {
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

        return Results.Ok(new
        {
            success = true,
            detectedDocumentType = result.DocumentType,
            documentConfidence = result.DocumentConfidence,
            firstName = result.FirstName,
            firstNameConfidence = result.FirstNameConfidence,
            lastName = result.LastName,
            lastNameConfidence = result.LastNameConfidence,
            reviewStatus = result.ReviewStatus
        });
    }
}