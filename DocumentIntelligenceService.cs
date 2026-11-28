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

public sealed record PayslipNamingResult(
    bool IsPayslip,
    bool IsAutoNameCandidate,
    string? SuggestedFileName,
    string? EmployeeName,
    string? PeriodLabel,
    double? PayPeriodConfidence,
    string NamingTier);

public sealed record BankStatementAnalysisResult(
    bool LooksLikeBankStatement,
    bool LooksLikeCreditCardStatement,
    string Content,
    IReadOnlyList<DocumentKeyValueResult> KeyValuePairs,
    string? AccountHolderCandidate,
    string? InstitutionCandidate,
    string? IdentifierType,
    string? IdentifierLast4,
    DateOnly? StatementPeriodStart,
    DateOnly? StatementPeriodEnd,
    string? PeriodLabel,
    double? StatementPeriodConfidence);

public sealed record FinancialStatementNamingResult(
    bool IsFinancialStatement,
    bool IsAutoNameCandidate,
    string? SuggestedFileName,
    string? StatementType,
    string? AccountHolder,
    string? Institution,
    string? IdentifierType,
    string? IdentifierLast4,
    string? PeriodLabel,
    double? StatementPeriodConfidence,
    string NamingTier);

public sealed record DocumentProcessingResult(
    string OriginalFileName,
    string DetectedDocumentType,
    bool IsAutoNameCandidate,
    string? SuggestedFileName,
    string NamingTier);


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


public async Task<DocumentProcessingResult>
    AnalyzeDocumentForNamingAsync(
        byte[] documentBytes,
        string originalFileName,
        CancellationToken cancellationToken = default)
{
    // validation...

    // 1. PAYSLIP
    try
    {
        var payslipAnalysis =
            await AnalyzePayslipAsync(
                documentBytes,
                cancellationToken);

        if (payslipAnalysis.LooksLikePayslip)
        {
            var payslipNaming =
                BuildPayslipNamingResult(
                    payslipAnalysis,
                    originalFileName);

            return new DocumentProcessingResult(
                OriginalFileName: originalFileName,
                DetectedDocumentType: "Payslip",
                IsAutoNameCandidate:
                    payslipNaming.IsAutoNameCandidate,
                SuggestedFileName:
                    payslipNaming.SuggestedFileName,
                NamingTier:
                    payslipNaming.NamingTier);
        }
    }
    catch (Azure.RequestFailedException)
    {
        // Continue to identity.
    }

    // 2. IDENTITY
    try
    {
        var identity =
            await AnalyzeForNamingAsync(
                documentBytes,
                originalFileName,
                cancellationToken);

        if (identity.IsAutoNameCandidate &&
            !string.IsNullOrWhiteSpace(
                identity.SuggestedFileName))
        {
            string detectedType =
                identity.DocumentType switch
                {
                    "idDocument.driverLicense" =>
                        "DriverLicense",

                    "idDocument.passport" =>
                        "Passport",

                    _ =>
                        "IdentityDocument"
                };

            return new DocumentProcessingResult(
                OriginalFileName: originalFileName,
                DetectedDocumentType: detectedType,
                IsAutoNameCandidate: true,
                SuggestedFileName:
                    identity.SuggestedFileName,
                NamingTier: "IdentityDocument");
        }
    }
    catch (Azure.RequestFailedException)
    {
        // Continue to financial statement.
    }

    // 3. FINANCIAL STATEMENT
    try
    {
        var financialAnalysis =
            await AnalyzeBankStatementAsync(
                documentBytes,
                cancellationToken);

        if (financialAnalysis.LooksLikeBankStatement ||
            financialAnalysis.LooksLikeCreditCardStatement)
        {
            var financialNaming =
                BuildFinancialStatementNamingResult(
                    financialAnalysis,
                    originalFileName);

            return new DocumentProcessingResult(
                OriginalFileName: originalFileName,

                DetectedDocumentType:
                    financialAnalysis
                        .LooksLikeCreditCardStatement
                            ? "CreditCardStatement"
                            : "BankStatement",

                IsAutoNameCandidate:
                    financialNaming.IsAutoNameCandidate,

                SuggestedFileName:
                    financialNaming.SuggestedFileName,

                NamingTier:
                    financialNaming.NamingTier);
        }
    }
    catch (Azure.RequestFailedException)
    {
        // Fall through to safe fallback.
    }

    return new DocumentProcessingResult(
        OriginalFileName: originalFileName,
        DetectedDocumentType: "Unknown",
        IsAutoNameCandidate: false,
        SuggestedFileName: null,
        NamingTier: "Fallback");
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
        ExtractStructuredEmployeeName(pairs)
        ?? ExtractEmployeeNameCandidate(content);

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


public async Task<PayslipNamingResult>
    AnalyzePayslipForNamingAsync(
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

    var analysis =
        await AnalyzePayslipAsync(
            documentBytes,
            cancellationToken);

    return BuildPayslipNamingResult(
        analysis,
        originalFileName);
}


    // <summary>
    /// Builds a Bank statement analysis result.
    /// </summary>

public async Task<BankStatementAnalysisResult>
    AnalyzeBankStatementAsync(
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

    var operation =
        await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            options,
            cancellationToken);

    AnalyzeResult result =
        operation.Value;

    var pairs = result.KeyValuePairs
        .Where(pair =>
            !string.IsNullOrWhiteSpace(
                pair.Key?.Content))
        .Select(pair =>
            new DocumentKeyValueResult(
                Key:
                    pair.Key.Content.Trim(),

                Value:
                    string.IsNullOrWhiteSpace(
                        pair.Value?.Content)
                        ? null
                        : pair.Value.Content.Trim(),

                Confidence:
                    pair.Confidence))
        .ToArray();

    string content =
        result.Content?.Trim()
        ?? string.Empty;

    bool looksLikeCreditCardStatement =
        ContainsCreditCardStatementIndicators(
            content,
            pairs);

    bool looksLikeBankStatement =
        !looksLikeCreditCardStatement &&
        ContainsBankStatementIndicators(
            content,
            pairs);

    var holder =    ExtractFinancialStatementHolder(
        content,
        pairs);

    var institution =    ExtractFinancialInstitution(
        content);

    var identifier =    ExtractFinancialIdentifier(
        pairs,
        looksLikeCreditCardStatement);

    var statementPeriod =
    ExtractFinancialStatementPeriod(
        content,
        pairs);

    return new BankStatementAnalysisResult(
    LooksLikeBankStatement:     looksLikeBankStatement,
    LooksLikeCreditCardStatement:     looksLikeCreditCardStatement,
    Content:     content,
    KeyValuePairs:       pairs,
    AccountHolderCandidate:        holder,
    InstitutionCandidate:        institution,
    IdentifierType:        identifier.Type,
    IdentifierLast4:        identifier.Last4,
    StatementPeriodStart:        statementPeriod.Start,
    StatementPeriodEnd:        statementPeriod.End,
    PeriodLabel:        statementPeriod.Label,
    StatementPeriodConfidence:        statementPeriod.Confidence);

} // end of AnalyzeBankStatementAsync


public FinancialStatementNamingResult
    BuildFinancialStatementNamingResult(
        BankStatementAnalysisResult result,
        string originalFileName)
{
    ArgumentNullException.ThrowIfNull(result);

    if (string.IsNullOrWhiteSpace(originalFileName))
    {
        throw new ArgumentException(
            "Original filename cannot be empty.",
            nameof(originalFileName));
    }

    bool isFinancialStatement =
        result.LooksLikeBankStatement ||
        result.LooksLikeCreditCardStatement;

    if (!isFinancialStatement)
    {
        return new FinancialStatementNamingResult(
            IsFinancialStatement: false,
            IsAutoNameCandidate: false,
            SuggestedFileName: null,
            StatementType: null,
            AccountHolder: null,
            Institution: null,
            IdentifierType: null,
            IdentifierLast4: null,
            PeriodLabel: null,
            StatementPeriodConfidence: null,
            NamingTier: "NotFinancialStatement");
    }

    string statementType =
        result.LooksLikeCreditCardStatement
            ? "Credit_Card_Statement"
            : "Bank_Statement";

    string extension =
        NormalizeDocumentExtension(
            Path.GetExtension(originalFileName));

    string? safeHolder =
        string.IsNullOrWhiteSpace(
            result.AccountHolderCandidate)
            ? null
            : SanitizeFinancialHolder(
                result.AccountHolderCandidate);

    string? safeInstitution =
    string.IsNullOrWhiteSpace(
        result.InstitutionCandidate)
        ? null
        : SanitizeFinancialInstitution(
            result.InstitutionCandidate);

    bool identifierAvailable =
        !string.IsNullOrWhiteSpace(
            result.IdentifierType) &&
        !string.IsNullOrWhiteSpace(
            result.IdentifierLast4);

    bool reliablePeriod =
        !string.IsNullOrWhiteSpace(
            result.PeriodLabel) &&
        result.StatementPeriodConfidence >= 0.90;

    var parts =
        new List<string>();

    if (!string.IsNullOrWhiteSpace(safeHolder))
    {
        parts.Add(safeHolder);
    }

    if (!string.IsNullOrWhiteSpace(safeInstitution))
    {
        parts.Add(safeInstitution);
    }

    parts.Add(statementType);

    if (identifierAvailable)
    {
        parts.Add(
            $"{result.IdentifierType}_{result.IdentifierLast4}");
    }

    if (reliablePeriod)
    {
        parts.Add(result.PeriodLabel!);
    }

    string suggestedFileName =
        $"{string.Join("_", parts)}{extension}";

    string namingTier =
        !string.IsNullOrWhiteSpace(safeHolder) &&
        !string.IsNullOrWhiteSpace(safeInstitution) &&
        identifierAvailable &&
        reliablePeriod
            ? "HolderInstitutionIdentifierPeriod"
            : !string.IsNullOrWhiteSpace(safeInstitution) &&
              identifierAvailable &&
              reliablePeriod
                ? "InstitutionIdentifierPeriod"
                : reliablePeriod
                    ? "PeriodAvailable"
                    : identifierAvailable
                        ? "IdentifierAvailable"
                        : "StatementOnly";

    return new FinancialStatementNamingResult(
        IsFinancialStatement: true,
        IsAutoNameCandidate: true,
        SuggestedFileName: suggestedFileName,
        StatementType: statementType,
        AccountHolder: result.AccountHolderCandidate,
        Institution: result.InstitutionCandidate,
        IdentifierType: result.IdentifierType,
        IdentifierLast4: result.IdentifierLast4,
        PeriodLabel: result.PeriodLabel,
        StatementPeriodConfidence:
            result.StatementPeriodConfidence,
        NamingTier: namingTier);
}

private static string SanitizeFinancialHolder(
    string value)
{
    var normalized =
        value.Replace(
            "&",
            " And ",
            StringComparison.Ordinal);

    normalized =
        string.Join(
            " ",
            normalized.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    return SanitizeFilePart(normalized);
}

private static string SanitizeFinancialInstitution(
    string value)
{
    return value.Trim().ToUpperInvariant() switch
    {
        "NAB" => "NAB",
        "CBA" => "CBA",
        "ANZ" => "ANZ",
        "WESTPAC" => "Westpac",
        "MACQUARIE" => "Macquarie",
        _ => SanitizeFilePart(value)
    };
}

public PayslipNamingResult
    BuildPayslipNamingResult(
        PayslipAnalysisResult result,
        string originalFileName)
{
    ArgumentNullException.ThrowIfNull(result);

    if (string.IsNullOrWhiteSpace(originalFileName))
    {
        throw new ArgumentException(
            "Original filename cannot be empty.",
            nameof(originalFileName));
    }

    if (!result.LooksLikePayslip)
    {
        return new PayslipNamingResult(
            IsPayslip: false,
            IsAutoNameCandidate: false,
            SuggestedFileName: null,
            EmployeeName: null,
            PeriodLabel: null,
            PayPeriodConfidence: null,
            NamingTier: "NotPayslip");
    }

    bool reliablePeriod =
        !string.IsNullOrWhiteSpace(result.PeriodLabel) &&
        result.PayPeriodConfidence >= 0.90;

    bool employeeAvailable =
        !string.IsNullOrWhiteSpace(
            result.EmployeeNameCandidate);

    string extension =
        NormalizeDocumentExtension(
            Path.GetExtension(originalFileName));

    string? safeEmployee =
        employeeAvailable
            ? SanitizePersonName(
                result.EmployeeNameCandidate!)
            : null;

    string suggestedFileName;
    string namingTier;

    if (reliablePeriod &&
        !string.IsNullOrWhiteSpace(safeEmployee))
    {
        suggestedFileName =
            $"{safeEmployee}_Salary_Slip_{result.PeriodLabel}{extension}";

        namingTier =
            "EmployeeAndPeriod";
    }
    else if (reliablePeriod)
    {
        suggestedFileName =
            $"Salary_Slip_{result.PeriodLabel}{extension}";

        namingTier =
            "PeriodOnly";
    }
    else if (!string.IsNullOrWhiteSpace(safeEmployee))
    {
        suggestedFileName =
            $"{safeEmployee}_Salary_Slip{extension}";

        namingTier =
            "EmployeeOnly";
    }
    else
    {
        suggestedFileName =
            $"Salary_Slip{extension}";

        namingTier =
            "PayslipOnly";
    }

    return new PayslipNamingResult(
        IsPayslip: true,
        IsAutoNameCandidate: true,
        SuggestedFileName: suggestedFileName,
        EmployeeName: result.EmployeeNameCandidate,
        PeriodLabel: result.PeriodLabel,
        PayPeriodConfidence: result.PayPeriodConfidence,
        NamingTier: namingTier);
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
    // Strategy 1:
    // Explicit Pay Period range.
    var payPeriodPair = pairs.FirstOrDefault(pair =>
    {
        var normalizedKey =
            NormalizeKey(pair.Key);

        return normalizedKey.StartsWith(
            "Pay Period",
            StringComparison.OrdinalIgnoreCase);
    });

    if (payPeriodPair is not null &&
        !string.IsNullOrWhiteSpace(payPeriodPair.Value))
    {
        var parsed =
            TryParsePayPeriodRange(
                payPeriodPair.Value,
                payPeriodPair.Confidence);

        if (parsed.Start is not null &&
            parsed.End is not null)
        {
            return parsed;
        }
    }

    // Strategy 2:
    // Separate Start Date / End Date.
        var startKeys = new[]
        {
        "Start Date",
        "Period Starting",
        "Period Start"
        };

        var endKeys = new[]
        {
        "End Date",
        "Period Ending"
        };

        var startPair = pairs.FirstOrDefault(pair =>
        {
        var key = NormalizeKey(pair.Key);

        return startKeys.Any(candidate =>
            key.Equals(
                candidate,
                StringComparison.OrdinalIgnoreCase));
        });

        var endPair = pairs.FirstOrDefault(pair =>
        {
        var key = NormalizeKey(pair.Key);

        return endKeys.Any(candidate =>
            key.Equals(
                candidate,
                StringComparison.OrdinalIgnoreCase));
        });
    if (startPair is not null &&
        endPair is not null &&
        !string.IsNullOrWhiteSpace(startPair.Value) &&
        !string.IsNullOrWhiteSpace(endPair.Value) &&
        TryParsePayslipDate(
            startPair.Value,
            out var start) &&
        TryParsePayslipDate(
            endPair.Value,
            out var end))
    {
        // Use the weaker of the two field confidences.
        var confidence =
            Math.Min(
                startPair.Confidence,
                endPair.Confidence);

        return BuildPayPeriodResult(
            start,
            end,
            confidence);
    }
    // Strategy 3: Period End containing a range

var periodEndPair = pairs.FirstOrDefault(pair =>
    NormalizeKey(pair.Key).Equals(
        "Period End",
        StringComparison.OrdinalIgnoreCase));

if (periodEndPair is not null &&
    !string.IsNullOrWhiteSpace(periodEndPair.Value))
{
    var parsed =
        TryParsePayPeriodRange(
            periodEndPair.Value,
            periodEndPair.Confidence);

    if (parsed.Start is not null &&
        parsed.End is not null)
    {
        return parsed;
    }
}

    return (
        null,
        null,
        null,
        null);
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
    "d/MM/yyyy",

    "dd.MM.yyyy",
    "d.MM.yyyy"
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
                "Date Paid",
                StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(
                "Pay Period",
                StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(
                "Period Starting",
                StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(
                "Period Start",
                StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(
                "Period Ending",
                StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith(
                "Period End",
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


private static string NormalizeDocumentExtension(
    string? extension)
{
    return extension?.ToLowerInvariant() switch
    {
        ".pdf" => ".pdf",
        ".jpg" => ".jpg",
        ".jpeg" => ".jpeg",
        ".png" => ".png",
        _ => ".pdf"
    };
}

private static string SanitizePersonName(
    string value)
{
    return SanitizeFilePart(value);
}

private static (
    string? Name,
    string? Source)?
    ExtractStructuredEmployeeName(
        IReadOnlyList<DocumentKeyValueResult> pairs)
{
    string[] acceptedKeys =
    [
        "Employee",
        "Employee Name",
        "EmployeeName",
        "Staff Member",
        "Payee"
    ];

    foreach (var pair in pairs)
    {
        var normalizedKey =
            pair.Key
                .Trim()
                .Replace("\r", " ")
                .Replace("\n", " ")
                .TrimEnd(':')
                .Trim();

        bool keyMatches =
            acceptedKeys.Any(key =>
                normalizedKey.Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase));

        if (!keyMatches)
            continue;

        if (string.IsNullOrWhiteSpace(pair.Value))
            continue;

        var candidate =
            pair.Value.Trim();

        if (LooksLikeOrganisationHeader(candidate))
            continue;

        var sanitized =
            ExtractLeadingPersonName(candidate);

        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            return (
                sanitized,
                $"Structured:{normalizedKey}");
        }
    }

        return null;
    }

    private static string NormalizeKey(
    string value)
    {
        return value
            .Trim()
            .Replace("\r", " ")
            .Replace("\n", " ")
            .TrimEnd(':')
            .Trim();
    }

    private static (
    DateOnly? Start,
    DateOnly? End,
    string? Label,
    double? Confidence)
    TryParsePayPeriodRange(
        string value,
        double confidence)
    {
    string[] separators =
    [
        " to ",
        " - "
    ];

    foreach (var separator in separators)
    {
        var parts = value.Split(
            separator,
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
            continue;

        if (TryParsePayslipDate(
                parts[0],
                out var start) &&
            TryParsePayslipDate(
                parts[1],
                out var end))
        {
            return BuildPayPeriodResult(
                start,
                end,
                confidence);
        }
    }

    return (
        null,
        null,
        null,
        confidence);
    }

// Beginnig of Bank Statement detector

private static bool ContainsBankStatementIndicators(
    string content,
    IReadOnlyList<DocumentKeyValueResult> pairs)
{
    string searchableText =
        BuildSearchableDocumentText(
            content,
            pairs);

    string[] strongIndicators =
    [
        "bank statement",
        "account statement",
        "statement of account",
        "transaction account",
        "savings account",
        "transaction listing",
        "account transaction listing"
    ];

    string[] supportingIndicators =
    [
        "statement period",
        "account number",
        "opening balance",
        "closing balance",
        "available balance",
        "transaction details",
        "deposits",
        "withdrawals",
        "debits",
        "credits"
    ];

    bool hasStrongIndicator =
        strongIndicators.Any(indicator =>
            searchableText.Contains(
                indicator,
                StringComparison.OrdinalIgnoreCase));

    int supportingMatches =
        supportingIndicators.Count(indicator =>
            searchableText.Contains(
                indicator,
                StringComparison.OrdinalIgnoreCase));

    return hasStrongIndicator
        ? supportingMatches >= 1
        : supportingMatches >= 3;
}

private static bool ContainsCreditCardStatementIndicators(
    string content,
    IReadOnlyList<DocumentKeyValueResult> pairs)
{
    string searchableText =
        BuildSearchableDocumentText(
            content,
            pairs);

    string[] strongIndicators =
    [
        "credit card statement",
        "card statement",
        "credit card account"
    ];

    string[] supportingIndicators =
    [
        "credit limit",
        "minimum payment",
        "payment due",
        "closing balance",
        "available credit",
        "card number"
    ];

    bool hasStrongIndicator =
        strongIndicators.Any(indicator =>
            searchableText.Contains(
                indicator,
                StringComparison.OrdinalIgnoreCase));

    int supportingMatches =
        supportingIndicators.Count(indicator =>
            searchableText.Contains(
                indicator,
                StringComparison.OrdinalIgnoreCase));

    return hasStrongIndicator
        ? supportingMatches >= 1
        : supportingMatches >= 3;
}

private static string BuildSearchableDocumentText(
    string content,
    IReadOnlyList<DocumentKeyValueResult> pairs)
{
    return string.Join(
        " ",
        new[]
        {
            content,

            string.Join(
                " ",
                pairs.Select(pair =>
                    $"{pair.Key} {pair.Value}"))
        });
}

private static string? ExtractFinancialStatementHolder(
    string content,
    IReadOnlyList<DocumentKeyValueResult> pairs)
{
    string[] holderKeys =
    [
        "Account Name",
        "Account Holder",
        "Account Holder Name",
        "Customer Name"
    ];

    foreach (var pair in pairs)
    {
        var key =
            NormalizeKey(pair.Key);

        if (!holderKeys.Any(candidate =>
                key.Equals(
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (!string.IsNullOrWhiteSpace(pair.Value))
        {
            return string.Join( " ",  pair.Value.Split( (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        }
    }

    // Do not guess from arbitrary transactions yet.
    // Header OCR fallback will be added only after
    // testing more institutions.
    return ExtractFinancialStatementHeaderName(
        content);
}


private static string? ExtractPersonNameAfterTitle(
    string line)
{
    if (string.IsNullOrWhiteSpace(line))
        return null;

    var tokens =
        line.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

    string[] titles =
    [
        "MR",
        "MRS",
        "MS",
        "MISS",
        "DR"
    ];

    for (int index = 0;
         index < tokens.Length;
         index++)
    {
        var token =
            tokens[index]
                .Trim('.', ',', ':')
                .ToUpperInvariant();

        if (!titles.Contains(
                token,
                StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }

        var nameTokens =
            new List<string>();

        for (int nameIndex = index + 1;
             nameIndex < tokens.Length;
             nameIndex++)
        {
            var candidate =
                tokens[nameIndex]
                    .Trim(',', '.', ';', ':');

            if (string.IsNullOrWhiteSpace(candidate))
                break;

            // A numeric token normally marks the beginning
            // of an address, customer reference, etc.
            if (candidate.Any(char.IsDigit))
                break;

            if (!candidate.All(character =>
                    char.IsLetter(character) ||
                    character is '-' or '\''))
            {
                break;
            }

            nameTokens.Add(candidate);

            // Conservative upper limit.
            if (nameTokens.Count == 4)
                break;
        }

        if (nameTokens.Count is >= 2 and <= 4)
        {
            return string.Join(
                " ",
                nameTokens);
        }
    }

    return null;
}


private static string? ExtractFinancialStatementHeaderName(
    string content)
{
    if (string.IsNullOrWhiteSpace(content))
        return null;

    var lines = content
        .Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)
        .Take(12)
        .ToArray();

    foreach (var line in lines)
    {
        var titledName =
            ExtractPersonNameAfterTitle(line);

        if (!string.IsNullOrWhiteSpace(titledName))
        {
            return titledName;
        }

        if (line.Any(char.IsDigit))
        {
            var candidate =
                ExtractLeadingPersonName(line);

            if (!string.IsNullOrWhiteSpace(candidate) &&
                !LooksLikeFinancialInstitutionLine(line))
            {
                return candidate;
            }
        }
    }

    return null;
}

private static string? ExtractFinancialInstitution(
    string content)
{
    if (string.IsNullOrWhiteSpace(content))
        return null;

    var mappings = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["Commonwealth Bank"] = "CBA",
        ["CommBank"] = "CBA",
        ["National Australia Bank"] = "NAB",
        ["NAB"] = "NAB",
        ["Australia and New Zealand Banking Group"] = "ANZ",
        ["ANZ"] = "ANZ",
        ["Westpac"] = "Westpac",
        ["Macquarie Bank"] = "Macquarie",
        ["MACQUARIE"] = "Macquarie"
    };

    foreach (var mapping in mappings)
    {
        if (content.Contains(
                mapping.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            return mapping.Value;
        }
    }

    return null;
}

private static bool LooksLikeFinancialInstitutionLine(
    string line)
{
    string[] organisationIndicators =
    [
        "BANK",
        "NAB",
        "COMMBANK",
        "COMMONWEALTH",
        "MACQUARIE",
        "ANZ",
        "WESTPAC",
        "ABN",
        "AFSL",
        "CREDIT LICENCE"
    ];

    return organisationIndicators.Any(indicator =>
        line.Contains(
            indicator,
            StringComparison.OrdinalIgnoreCase));
}

private static (
    string? Type,
    string? Last4)
    ExtractFinancialIdentifier(
        IReadOnlyList<DocumentKeyValueResult> pairs,
        bool isCreditCardStatement)
{
    string[] keys =
        isCreditCardStatement
            ? ["Account Number", "Card Number", "Card No"]
            : ["Account No.", "Account No", "Account Number"];

    foreach (var pair in pairs)
    {
        var key =
            NormalizeKey(pair.Key);

        if (!keys.Any(candidate =>
                key.Equals(
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(pair.Value))
            continue;

        var digits =
            new string(
                pair.Value
                    .Where(char.IsDigit)
                    .ToArray());

        if (digits.Length < 4)
            continue;

        return (
            isCreditCardStatement
                ? "Card"
                : "Account",
            digits[^4..]);
    }

    return (
        isCreditCardStatement
            ? "Card"
            : "Account",
        null);
}

private static (
    DateOnly? Start,
    DateOnly? End,
    string? Label,
    double? Confidence)
    ExtractFinancialStatementPeriod(
        string content,
        IReadOnlyList<DocumentKeyValueResult> pairs)
{
    string[] periodKeys =
    [
        "Statement Period",
        "From"
    ];

    foreach (var pair in pairs)
    {
        var key =
            NormalizeKey(pair.Key);

        if (!periodKeys.Any(candidate =>
                key.Equals(
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(pair.Value))
            continue;

        var parsed =
            TryParseFinancialPeriodRange(
                pair.Value,
                pair.Confidence);

        if (parsed.Start is not null &&
            parsed.End is not null)
        {
            return parsed;
        }
    }

    // OCR fallback for statements where the period
    // is present in the header but not exposed as a key/value pair.
    if (!string.IsNullOrWhiteSpace(content))
    {
        var lines = content
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Take(30);

        foreach (var line in lines)
        {
            if (!line.StartsWith(
                    "From ",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var periodText =
                line["From ".Length..].Trim();

            var parsed =
                TryParseFinancialPeriodRange(
                    periodText,
                    confidence: 0.90);

            if (parsed.Start is not null &&
                parsed.End is not null)
            {
                return parsed;
            }
        }
    }

    return (
        null,
        null,
        null,
        null);
}


private static (
    DateOnly? Start,
    DateOnly? End,
    string? Label,
    double? Confidence)
    TryParseFinancialPeriodRange(
        string value,
        double confidence)
{
    string[] separators =
    [
        " to ",
        " - ",
        "-"
    ];

    foreach (var separator in separators)
    {
        var parts =
            value.Split(
                separator,
                StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
            continue;

        if (TryParseFinancialDate(
                parts[0],
                out var start) &&
            TryParseFinancialDate(
                parts[1],
                out var end))
        {
            string label =
                start.Year == end.Year &&
                start.Month == end.Month
                    ? $"{start:yyyy-MM}"
                    : $"{start:yyyy-MM-dd}_to_{end:yyyy-MM-dd}";

            return (
                start,
                end,
                label,
                confidence);
        }
    }

    return (
        null,
        null,
        null,
        confidence);
}

private static bool TryParseFinancialDate(
    string value,
    out DateOnly date)
{
    string[] formats =
    [
        "d MMMM yyyy",
        "dd MMMM yyyy",
        "d MMM yyyy",
        "dd MMM yyyy",
        "d MMM yy",
        "dd MMM yy",
        "d/MM/yyyy",
        "dd/MM/yyyy"
    ];

    return DateOnly.TryParseExact(
        value.Trim(),
        formats,
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out date);
}


}// end of DocumentIntelligenceService