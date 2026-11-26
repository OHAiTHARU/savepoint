using Savepoint.Core.Api;
using Savepoint.Core.Paths;
using Savepoint.Core.State;
using Savepoint.Core.Sync;

namespace Savepoint.Core.Tests;

/// <summary>A simulated PC: its own temp folders, state DB and engine, talking to a shared server.</summary>
public sealed class TestMachine : IDisposable
{
    public TestMachine(ISavepointApi api, string name)
    {
        Name = name;
        Root = Path.Combine(Path.GetTempPath(), "savepoint-tests", Guid.NewGuid().ToString("N"), name);
        AppData = Path.Combine(Root, "AppData");
        DataDir = Path.Combine(Root, "LocalAppData", "Savepoint");
        Directory.CreateDirectory(AppData);
        Api = api;
        State = new StateStore(Path.Combine(DataDir, "state.db"), new PlainProtector());
        Paths = new DictionaryPathEnvironment(new Dictionary<string, string>
        {
            ["APPDATA"] = AppData,
            ["LOCALAPPDATA"] = Path.Combine(Root, "LocalAppData"),
            ["USERPROFILE"] = Root,
        });
        Engine = new SyncEngine(api, State, Paths, g => RunningGames.Contains(g.Id),
            new SyncEngineOptions { DataDirectory = DataDir, Scan = new() { LockRetries = 1, LockRetryDelay = TimeSpan.FromMilliseconds(10) } });
        Engine.Notification += (_, n) => Notifications.Add(n);
    }

    public string Name { get; }
    public string Root { get; }
    public string AppData { get; }
    public string DataDir { get; }
    public ISavepointApi Api { get; }
    public StateStore State { get; }
    public IPathEnvironment Paths { get; }
    public SyncEngine Engine { get; }
    public HashSet<string> RunningGames { get; } = new();
    public List<SyncNotification> Notifications { get; } = new();

    public string SaveDir(string sub = "Game") => Path.Combine(AppData, sub);

    public string Write(string relative, string content, DateTime? mtimeUtc = null, string sub = "Game")
    {
        var path = Path.Combine(SaveDir(sub), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, mtimeUtc ?? DateTime.UtcNow);
        return path;
    }

    public string? Read(string relative, string sub = "Game")
    {
        var path = Path.Combine(SaveDir(sub), relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Dispose()
    {
        State.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true); } catch (IOException) { }
    }
}
