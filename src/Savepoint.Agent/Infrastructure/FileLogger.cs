using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Savepoint.Agent.Infrastructure;

/// <summary>Daily log files in %LOCALAPPDATA%\Savepoint\logs, keeping the last 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string dir, LogLevel minLevel = LogLevel.Information)
    {
        _dir = dir;
        _minLevel = minLevel;
        Directory.CreateDirectory(dir);
        Prune();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "log-writer" };
        _writer.Start();
    }

    public string Directory_ => _dir;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    internal bool IsEnabled(LogLevel level) => level >= _minLevel;

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_dir, $"agent-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { /* never let logging crash the agent */ }
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "agent-*.log"))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
            }
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _category;

        public FileLogger(FileLoggerProvider p, string category)
        {
            _p = p;
            _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => _p.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {_category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            _p.Enqueue(line);
        }
    }
}
