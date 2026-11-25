using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using System.Text;
namespace InboxReader;
using Azure;

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

        api.MapPost("/payslip-test", TestPayslip)
            .WithName("TestPayslipDocumentIntelligence")
            .WithSummary("Test Australian payslip extraction")
            .WithDescription(
                "Analyzes one uploaded payslip without saving it.")
            .DisableAntiforgery();

        api.MapPost("/upload-check", UploadCheck)
            .WithName("DocumentIntelligenceUploadCheck")
            .WithSummary("Test multipart file upload")
            .WithDescription(
                "Confirms that an uploaded file reaches the application without running Document Intelligence.")
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

    private static async Task<IResult> TestPayslip(
    HttpRequest request,
    DocumentIntelligenceService service,
    CancellationToken cancellationToken)
{
    var form =
        await request.ReadFormAsync(cancellationToken);

    var file =
        form.Files.GetFile("file");

    if (file is null)
    {
        return Results.BadRequest(new
        {
            error = "Payslip file is required."
        });
    }

    if (file.Length == 0)
    {
        return Results.BadRequest(new
        {
            error = "Payslip file is empty."
        });
    }

    const long maxBytes =
        4L * 1024 * 1024;

    if (file.Length > maxBytes)
    {
        return Results.BadRequest(new
        {
            error = "Payslip exceeds the 4 MB pilot limit."
        });
    }

    var extension =
        Path.GetExtension(file.FileName);

    bool supportedFile =
        extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase);

    if (!supportedFile)
    {
        return Results.BadRequest(new
        {
            error = "Use PDF, JPG, JPEG or PNG for the payslip test."
        });
    }

    await using var stream =
        file.OpenReadStream();

    using var memory =
        new MemoryStream();

    await stream.CopyToAsync(
        memory,
        cancellationToken);

    PayslipAnalysisResult result;

try
{
    result =
        await service.AnalyzePayslipAsync(
            memory.ToArray(),
            cancellationToken);
}
catch (OperationCanceledException)
    when (cancellationToken.IsCancellationRequested)
{
    return Results.Json(
        new
        {
            success = false,
            stage = "PayslipAnalysis",
            message = "The payslip request was cancelled."
        },
        statusCode: StatusCodes.Status408RequestTimeout);
}
catch (Azure.RequestFailedException exception)
{
    return Results.Json(
        new
        {
            success = false,
            stage = "DocumentIntelligence",
            status = exception.Status,
            errorCode = exception.ErrorCode,
            message = exception.Message
        },
        statusCode: StatusCodes.Status502BadGateway);
}
catch (Exception exception)
{
    return Results.Json(
        new
        {
            success = false,
            stage = "PayslipAnalysis",
            errorType = exception.GetType().Name,
            message = exception.Message
        },
        statusCode:
            StatusCodes.Status500InternalServerError);
}

    var namingResult =
    service.BuildPayslipNamingResult(
        result,
        file.FileName);


    var keyValuePairs =
        result.KeyValuePairs
            .Select(pair => new
            {
                key = pair.Key,
                value = pair.Value,
                confidence = pair.Confidence
            })
            .ToArray();

    const int maximumPreviewCharacters = 3000;

    string contentPreview =
        result.Content.Length <= maximumPreviewCharacters
            ? result.Content
            : result.Content[..maximumPreviewCharacters];

    return Results.Ok(new
    {
        success = true,
        looksLikePayslip =   result.LooksLikePayslip,
        isAutoNameCandidate = namingResult.IsAutoNameCandidate,
        namingTier =   namingResult.NamingTier,
        suggestedFileName =   namingResult.SuggestedFileName,
        payPeriodStart =  result.PayPeriodStart,
        payPeriodEnd = result.PayPeriodEnd,
        periodLabel =   result.PeriodLabel,
        employeeNameCandidate =  result.EmployeeNameCandidate,
        employeeNameSource =   result.EmployeeNameSource,
        payPeriodConfidence =  result.PayPeriodConfidence,
        keyValuePairs,
        contentPreview
});
}


private static async Task<IResult> UploadCheck(
    HttpRequest request,
    CancellationToken cancellationToken)
{
    try
    {
        var contentType =
            request.ContentType ?? string.Empty;

        if (!request.HasFormContentType)
        {
            return Results.Json(
                new
                {
                    success = false,
                    stage = "ContentTypeCheck",
                    contentType,
                    error = "Request is not multipart form data."
                },
                statusCode:
                    StatusCodes.Status400BadRequest);
        }

        var form =
            await request.ReadFormAsync(
                cancellationToken);

        var file =
            form.Files.GetFile("file");

        if (file is null)
        {
            return Results.Json(
                new
                {
                    success = false,
                    stage = "GetFile",
                    fileCount = form.Files.Count,
                    error = "No file received."
                },
                statusCode:
                    StatusCodes.Status400BadRequest);
        }

        return Results.Json(
            new
            {
                success = true,
                stage = "UploadCheck",
                fileReceived = true,
                fileCount = form.Files.Count,
                fileSize = file.Length,
                extension =
                    Path.GetExtension(file.FileName)
            },
            statusCode:
                StatusCodes.Status200OK);
    }
    catch (OperationCanceledException exception)
    {
        return Results.Json(
            new
            {
                success = false,
                stage = "ReadFormAsync",
                errorType =
                    exception.GetType().Name,
                message =
                    "Upload request was cancelled."
            },
            statusCode:
                StatusCodes.Status408RequestTimeout);
    }
    catch (Exception exception)
    {
        return Results.Json(
            new
            {
                success = false,
                stage = "UploadCheck",
                errorType =
                    exception.GetType().FullName,
                message =
                    exception.Message
            },
            statusCode:
                StatusCodes.Status500InternalServerError);
    }
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