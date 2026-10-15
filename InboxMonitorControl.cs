namespace InboxReader;

public interface IInboxMonitorControl
{
    MonitorStatusResponse GetStatus();
    bool Start();
    Task<bool> StopAsync(CancellationToken cancellationToken);
}

public sealed class InboxMonitorControl(MailSettings settings) : IInboxMonitorControl, IAsyncDisposable
{
    private const int MaxActivityEntries = 200;
    private readonly object _gate = new();
    private readonly Queue<MonitorLogEntryResponse> _activity = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _isRunning;

    public MonitorStatusResponse GetStatus()
    {
        lock (_gate)
        {
            return new MonitorStatusResponse(
                _isRunning,
                settings.Provider.ToString(),
                settings.Email,
                settings.SubjectFilter,
                settings.OutputFolder,
                settings.PollIntervalSeconds,
                _activity.ToArray());
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_isRunning)
                return false;

            _isRunning = true;
            _monitorCts = new CancellationTokenSource();
            var cancellationToken = _monitorCts.Token;
            AddActivityLocked("Monitor started.");
            _monitorTask = Task.Run(() => RunMonitorAsync(cancellationToken));
            return true;
        }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        Task? monitorTask;
        lock (_gate)
        {
            if (!_isRunning)
                return false;

            _monitorCts?.Cancel();
            monitorTask = _monitorTask;
        }

        if (monitorTask is not null)
            await monitorTask.WaitAsync(cancellationToken);
        return true;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private async Task RunMonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await new InboxMonitor(settings, AddActivity).RunAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            AddActivity($"Monitor stopped unexpectedly: {exception.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
                _monitorCts?.Dispose();
                _monitorCts = null;
                _monitorTask = null;
                AddActivityLocked("Monitor stopped.");
            }
        }
    }

    private void AddActivity(string message)
    {
        lock (_gate)
            AddActivityLocked(message);
    }

    private void AddActivityLocked(string message)
    {
        _activity.Enqueue(new MonitorLogEntryResponse(DateTimeOffset.Now, message));
        while (_activity.Count > MaxActivityEntries)
            _activity.Dequeue();
    }
}

/// <summary>One timestamped event from the monitor activity feed.</summary>
public sealed record MonitorLogEntryResponse(DateTimeOffset Timestamp, string Message);

/// <summary>The current monitor state and the settings used by the local utility.</summary>
public sealed record MonitorStatusResponse(
    bool IsRunning,
    string Provider,
    string Email,
    string SubjectFilter,
    string OutputFolder,
    int PollIntervalSeconds,
    IReadOnlyList<MonitorLogEntryResponse> Activity);