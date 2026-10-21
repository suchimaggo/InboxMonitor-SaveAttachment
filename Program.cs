using InboxReader;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Serialization;

var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "wwwroot");
var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory(),
    WebRootPath = Directory.Exists(webRoot) ? webRoot : null
};

var builder = WebApplication.CreateBuilder(options);
builder.WebHost.UseUrls("http://127.0.0.1:5087");
builder.Services.AddHttpClient("case-tracking", client =>
    client.Timeout = TimeSpan.FromMinutes(10));

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var settings = config.GetSection("InboxReader").Get<MailSettings>()
               ?? throw new InvalidOperationException("Missing 'InboxReader' configuration section.");

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IInboxMonitorControl, InboxMonitorControl>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers.Origin != "http://127.0.0.1:5087")
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

app.MapInboxMonitorEndpoints();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("Inbox Reader is available at http://127.0.0.1:5087"));

await app.RunAsync();
