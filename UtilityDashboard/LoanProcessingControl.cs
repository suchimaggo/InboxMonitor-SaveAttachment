using System.ComponentModel;
using System.Diagnostics;

sealed class LoanProcessingControl(string programPath)
{
    private const int MaxActivityEntries = 250;
    private readonly object _gate = new();
    private readonly Queue<string> _activity = new();
    private bool _isRunning;
    private string? _folderPath;
    private int? _exitCode;
    private string? _error;

    public bool IsConfigured => File.Exists(programPath);

    public LoanProcessingStatusResponse GetStatus()
    {
        lock (_gate)
            return GetStatusLocked();
    }

    public LoanProcessingStartResult TryStart(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return new(LoanProcessingStartOutcome.InvalidFolder, "Enter a folder path.", GetStatus());

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folderPath.Trim().Trim('"'));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(LoanProcessingStartOutcome.InvalidFolder, "The folder path is invalid.", GetStatus());
        }

        if (!Directory.Exists(fullPath))
            return new(LoanProcessingStartOutcome.InvalidFolder, "The folder does not exist or cannot be accessed.", GetStatus());

        lock (_gate)
        {
            if (!File.Exists(programPath))
                return new(LoanProcessingStartOutcome.NotConfigured, "The Loan Processing project was not found.", GetStatusLocked());

            if (_isRunning)
                return new(LoanProcessingStartOutcome.AlreadyRunning, null, GetStatusLocked());

            _isRunning = true;
            _folderPath = fullPath;
            _exitCode = null;
            _error = null;
            _activity.Clear();
            AddActivityLocked($"Starting processing for: {fullPath}");
            _ = Task.Run(() => RunProcessAsync(fullPath));

            return new(LoanProcessingStartOutcome.Started, null, GetStatusLocked());
        }
    }

    private async Task RunProcessAsync(string folderPath)
    {
        int? exitCode = null;
        string? error = null;

        try
        {
            var isProject = string.Equals(Path.GetExtension(programPath), ".csproj", StringComparison.OrdinalIgnoreCase);
            var startInfo = new ProcessStartInfo(isProject ? "dotnet" : programPath)
            {
                WorkingDirectory = Path.GetDirectoryName(programPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (isProject)
            {
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("--project");
                startInfo.ArgumentList.Add(programPath);
                startInfo.ArgumentList.Add("--");
            }
            startInfo.ArgumentList.Add("--process-folder");
            startInfo.ArgumentList.Add(folderPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("The Loan Processing process could not be started.");

            var stdoutTask = CaptureOutputAsync(process.StandardOutput);
            var stderrTask = CaptureOutputAsync(process.StandardError);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            exitCode = process.ExitCode;
            if (exitCode != 0)
                error = $"Loan Processing exited with code {exitCode}.";
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
                _exitCode = exitCode;
                _error = error;
                AddActivityLocked(error is null
                    ? "Processing completed."
                    : $"Processing failed: {error}");
            }
        }
    }

    private async Task CaptureOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_gate)
                AddActivityLocked(line);
        }
    }

    private LoanProcessingStatusResponse GetStatusLocked() =>
        new(IsConfigured, _isRunning, _folderPath, _exitCode, _error, _activity.ToArray());

    private void AddActivityLocked(string message)
    {
        _activity.Enqueue(message);
        while (_activity.Count > MaxActivityEntries)
            _activity.Dequeue();
    }
}

sealed record StartLoanProcessingRequest(string? FolderPath);

sealed record LoanProcessingStatusResponse(
    bool IsConfigured,
    bool IsRunning,
    string? FolderPath,
    int? ExitCode,
    string? Error,
    IReadOnlyList<string> Activity);

sealed record LoanProcessingStartResult(
    LoanProcessingStartOutcome Outcome,
    string? Error,
    LoanProcessingStatusResponse Status);

enum LoanProcessingStartOutcome
{
    Started,
    AlreadyRunning,
    NotConfigured,
    InvalidFolder
}
