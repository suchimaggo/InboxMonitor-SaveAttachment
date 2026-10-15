using Microsoft.AspNetCore.Http.HttpResults;

namespace InboxReader;

public static class InboxMonitorEndpoints
{
    public static WebApplication MapInboxMonitorEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", static (IInboxMonitorControl monitor) =>
                TypedResults.Ok(monitor.GetStatus()))
            .WithName("GetInboxMonitorStatus")
            .WithSummary("Get inbox monitor status")
            .WithDescription("Returns the current monitor state, configured filters, output folder, and recent activity.")
            .Produces<MonitorStatusResponse>(StatusCodes.Status200OK);

        api.MapPost("/start", Start)
            .WithName("StartInboxMonitor")
            .WithSummary("Start inbox monitoring")
            .WithDescription("Starts polling the configured inbox for matching messages with attachments.")
            .Produces<MonitorStatusResponse>(StatusCodes.Status202Accepted)
            .Produces<MonitorStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapPost("/stop", Stop)
            .WithName("StopInboxMonitor")
            .WithSummary("Stop inbox monitoring")
            .WithDescription("Stops the active inbox polling loop.")
            .Produces<MonitorStatusResponse>(StatusCodes.Status202Accepted)
            .Produces<MonitorStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static Results<Accepted<MonitorStatusResponse>, Ok<MonitorStatusResponse>> Start(
        IInboxMonitorControl monitor)
    {
        var started = monitor.Start();
        var response = monitor.GetStatus();
        return started
            ? TypedResults.Accepted("/api/status", response)
            : TypedResults.Ok(response);
    }

    private static async Task<Results<Accepted<MonitorStatusResponse>, Ok<MonitorStatusResponse>>> Stop(
        IInboxMonitorControl monitor,
        CancellationToken cancellationToken)
    {
        var stopped = await monitor.StopAsync(cancellationToken);
        var response = monitor.GetStatus();
        return stopped
            ? TypedResults.Accepted("/api/status", response)
            : TypedResults.Ok(response);
    }
}