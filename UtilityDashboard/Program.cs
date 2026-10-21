var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5086");
builder.Services.AddHttpClient("utility-health", client =>
    client.Timeout = TimeSpan.FromSeconds(3));

var loanExecutableSetting = builder.Configuration["LoanProcessing:ExecutablePath"];
var loanProjectSetting = builder.Configuration["LoanProcessing:ProjectPath"];
var loanProgramSetting = loanExecutableSetting ?? loanProjectSetting
    ?? throw new InvalidOperationException("Configure 'LoanProcessing:ExecutablePath' or 'LoanProcessing:ProjectPath'.");
var loanProgramPath = Path.GetFullPath(loanProgramSetting, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new LoanProcessingControl(loanProgramPath));

var utilities = builder.Configuration.GetSection("Utilities").Get<List<UtilityDefinition>>()
                ?? throw new InvalidOperationException("The 'Utilities' configuration section is required.");

foreach (var utility in utilities)
{
    if (string.IsNullOrWhiteSpace(utility.Name) ||
        string.IsNullOrWhiteSpace(utility.Description) ||
        !IsHttpUrl(utility.Url) ||
        (utility.HealthUrl is not null && !IsHttpUrl(utility.HealthUrl)))
    {
        throw new InvalidOperationException(
            $"Utility '{utility.Name}' must have a name, description, valid HTTP(S) URL, and optional valid health URL.");
    }
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers.Origin != "http://127.0.0.1:5086")
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

app.MapGet("/api/utilities", async (IHttpClientFactory clientFactory, CancellationToken cancellationToken) =>
{
    var client = clientFactory.CreateClient("utility-health");
    var results = await Task.WhenAll(utilities.Select(async utility =>
    {
        var availability = utility.HealthUrl is null
            ? "not-configured"
            : await CheckAvailabilityAsync(client, utility.HealthUrl, cancellationToken);

        return new UtilityStatus(utility.Name, utility.Description, utility.Url, availability);
    }));

    return Results.Ok(results);
});

app.MapGet("/api/loan-processing/status", (LoanProcessingControl control) =>
    Results.Ok(control.GetStatus()));

app.MapGet("/api/loan-processing/health", (LoanProcessingControl control) =>
    control.IsConfigured ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPost("/api/loan-processing/start", (StartLoanProcessingRequest request, LoanProcessingControl control) =>
{
    var result = control.TryStart(request.FolderPath);
    return result.Outcome switch
    {
        LoanProcessingStartOutcome.Started =>
            Results.Accepted("/api/loan-processing/status", result.Status),
        LoanProcessingStartOutcome.AlreadyRunning =>
            Results.Conflict(result.Status),
        LoanProcessingStartOutcome.NotConfigured =>
            Results.Problem("The Loan Processing project could not be found. Check its configured project path.",
                statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(new { error = result.Error })
    };
});

app.Run();

static bool IsHttpUrl(string value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

static async Task<string> CheckAvailabilityAsync(
    HttpClient client,
    string healthUrl,
    CancellationToken cancellationToken)
{
    try
    {
        using var response = await client.GetAsync(
            healthUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.IsSuccessStatusCode ? "online" : "offline";
    }
    catch (HttpRequestException)
    {
        return "offline";
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return "offline";
    }
}

sealed class UtilityDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string? HealthUrl { get; set; }
}

sealed record UtilityStatus(string Name, string Description, string Url, string Availability);
