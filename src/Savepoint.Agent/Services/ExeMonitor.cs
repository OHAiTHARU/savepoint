using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Savepoint.Core.Api;

namespace Savepoint.Agent.Services;

/// <summary>
/// Polls the process list every few seconds and matches each game's exe names
/// (case-insensitive). Unlike WMI process-trace events this needs no admin rights.
/// </summary>
public sealed class ExeMonitor : IDisposable
{
    private readonly ILogger _log;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private IReadOnlyList<Game> _games = Array.Empty<Game>();
    private HashSet<string> _running = new(StringComparer.Ordinal);
    private int _ticking;

    public ExeMonitor(ILogger log, TimeSpan interval)
    {
        _log = log;
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, interval);
    }

    public event EventHandler<Game>? GameStarted;
    public event EventHandler<Game>? GameExited;

    public void SetGames(IReadOnlyList<Game> games)
    {
        lock (_lock) _games = games;
    }

    public bool IsRunning(string gameId)
    {
        lock (_lock) return _running.Contains(gameId);
    }

    public IReadOnlyList<string> RunningGameIds
    {
        get { lock (_lock) return _running.ToList(); }
    }

    /// <summary>"C:\Games\ER\eldenring.exe" / "eldenring.exe" / "eldenring" → "eldenring".</summary>
    public static string NormalizeExe(string name)
    {
        name = Path.GetFileName(name.Trim().Trim('"'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName.ToLowerInvariant()); }
                catch (InvalidOperationException) { }
                finally { p.Dispose(); }
            }

            IReadOnlyList<Game> games;
            HashSet<string> before;
            lock (_lock)
            {
                games = _games;
                before = _running;
            }
            var now = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in games)
            {
                if (g.ExeNames.Any(e => names.Contains(NormalizeExe(e)))) now.Add(g.Id);
            }
            lock (_lock) _running = now;

            foreach (var g in games)
            {
                var was = before.Contains(g.Id);
                var isNow = now.Contains(g.Id);
                if (!was && isNow)
                {
                    _log.LogInformation("{Game} started", g.Name);
                    GameStarted?.Invoke(this, g);
                }
                else if (was && !isNow)
                {
                    _log.LogInformation("{Game} exited", g.Name);
                    GameExited?.Invoke(this, g);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Process poll failed");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
