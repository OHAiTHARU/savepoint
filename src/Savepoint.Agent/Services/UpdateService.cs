using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Savepoint.Core.Updates;

namespace Savepoint.Agent.Services;

/// <summary>
/// Checks GitHub Releases for a newer Savepoint.exe (at startup and daily, if enabled)
/// and installs it by swapping the running exe and restarting.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string AfterUpdateArg = "--after-update";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly ILogger _log;
    private readonly string _downloadDir;
    private readonly UpdateChecker _checker = new();
    private readonly Func<bool> _autoCheckEnabled;
    private System.Threading.Timer? _timer;
    private string? _notifiedTag;

    public UpdateService(ILogger log, string dataDir, Func<bool> autoCheckEnabled)
    {
        _log = log;
        _downloadDir = Path.Combine(dataDir, "updates");
        _autoCheckEnabled = autoCheckEnabled;
        CurrentVersion = UpdateChecker.Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));
    }

    public Version CurrentVersion { get; }

    /// <summary>The newer release found by the last check, if any.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised once per new version found by an automatic check.</summary>
    public event EventHandler<UpdateInfo>? UpdateFound;

    public void Start() =>
        _timer = new System.Threading.Timer(_ => _ = AutoCheckAsync(), null, FirstCheckDelay, CheckInterval);

    private async Task AutoCheckAsync()
    {
        if (!_autoCheckEnabled()) return;
        try
        {
            var update = await CheckAsync();
            if (update is not null && update.Tag != _notifiedTag)
            {
                _notifiedTag = update.Tag;
                UpdateFound?.Invoke(this, update);
            }
        }
        catch (UpdateException ex)
        {
            _log.LogInformation("Update check failed: {Message}", ex.Message);
        }
    }

    /// <summary>Check now. Returns the newer release, or null if up to date.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        Available = await _checker.CheckAsync(CurrentVersion, ct);
        _log.LogInformation("Update check: current {Current}, latest newer release {Latest}",
            CurrentVersion, Available?.Version.ToString() ?? "none");
        return Available;
    }

    /// <summary>Whether the folder holding Savepoint.exe is writable, so the exe can be swapped.</summary>
    public static bool CanSelfUpdate
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                var probe = Path.Combine(Path.GetDirectoryName(exe)!, $".savepoint-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default) =>
        _checker.DownloadAsync(update, _downloadDir, progress, ct);

    /// <summary>
    /// Replace the running exe with <paramref name="newExe"/> and start it. Windows lets a
    /// running exe be renamed but not overwritten, so: current → .old, new → current.
    /// The caller must then exit; the new process waits for this one to release the
    /// single-instance mutex, and deletes the .old file on startup.
    /// </summary>
    public void ApplyAndRestart(string newExe)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path");
        var old = current + ".old";
        TryDelete(old);
        File.Move(current, old);
        try
        {
            File.Copy(newExe, current, overwrite: false);
        }
        catch
        {
            File.Move(old, current); // put things back
            throw;
        }
        _log.LogInformation("Installed update from {New}; restarting", newExe);
        Process.Start(new ProcessStartInfo(current, AfterUpdateArg) { UseShellExecute = false });
    }

    /// <summary>Remove leftovers from a previous update (the old exe and downloads).</summary>
    public void CleanUpAfterUpdate()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        _ = Task.Run(async () =>
        {
            // The previous process may still be exiting; retry for a little while.
            for (var i = 0; i < 20 && File.Exists(exe + ".old"); i++)
            {
                if (TryDelete(exe + ".old")) break;
                await Task.Delay(500);
            }
            try
            {
                if (Directory.Exists(_downloadDir))
                {
                    foreach (var f in Directory.GetFiles(_downloadDir)) TryDelete(f);
                }
            }
            catch (IOException) { }
        });
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _checker.Dispose();
    }
}
