using InboxReader;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;

var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "wwwroot");
var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory(),
    WebRootPath = Directory.Exists(webRoot) ? webRoot : null
};

var builder = WebApplication.CreateBuilder(options);
var settings = builder.Configuration.GetSection("InboxReader").Get<MailSettings>()
               ?? throw new InvalidOperationException("Missing 'InboxReader' configuration section.");

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<TokenCredential>(_ =>
{
    var managedIdentityClientId = builder.Configuration["Azure:ManagedIdentityClientId"];
    if (builder.Environment.IsProduction())
        return string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(managedIdentityClientId);
    return new DefaultAzureCredential();
});
builder.Services.AddSingleton<DocumentIntelligenceService>();
builder.Services.AddHttpClient<MicrosoftGraphInboxClient>(client =>
    client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<InboxMonitorControl>();
builder.Services.AddSingleton<IInboxMonitorControl>(provider =>
    provider.GetRequiredService<InboxMonitorControl>());
builder.Services.AddHostedService<InboxMonitorControl>(provider =>
    provider.GetRequiredService<InboxMonitorControl>());
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
         HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)) &&
        context.Request.Headers.Origin.Count > 0 &&
        (!Uri.TryCreate(context.Request.Headers.Origin, UriKind.Absolute, out var origin) ||
         !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) ||
         (!app.Environment.IsDevelopment() && origin.Scheme != Uri.UriSchemeHttps)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (!app.Environment.IsDevelopment() &&
        !context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
        !context.Request.Headers.ContainsKey("X-MS-CLIENT-PRINCIPAL"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api") &&
        HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers.Origin.Count == 0 &&
        !app.Environment.IsDevelopment())
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapInboxMonitorEndpoints();
app.MapDocumentIntelligenceEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("Inbox Reader service started."));

await app.RunAsync();
