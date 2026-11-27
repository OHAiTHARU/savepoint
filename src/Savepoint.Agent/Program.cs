using Microsoft.Extensions.Logging;
using Savepoint.Agent.Infrastructure;
using Savepoint.Agent.Services;
using Savepoint.Agent.UI;
using Savepoint.Core.State;

namespace Savepoint.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Savepoint.Agent", out var first);
        if (!first && args.Contains(UpdateService.AfterUpdateArg, StringComparer.OrdinalIgnoreCase))
        {
            // Started by the previous version while it shuts down: wait for it to exit.
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            MessageBox.Show("Savepoint is already running. Look for its icon in the system tray.", "Savepoint",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Savepoint");
        Directory.CreateDirectory(dataDir);

        using var logProvider = new FileLoggerProvider(Path.Combine(dataDir, "logs"));
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Information));
        var log = loggerFactory.CreateLogger("Program");
        log.LogInformation("Savepoint {Version} starting", typeof(Program).Assembly.GetName().Version);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.LogError(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        using var state = new StateStore(Path.Combine(dataDir, "state.db"), SecretProtectors.ForPlatform());
        using var agent = new AgentService(state, loggerFactory, dataDir);
        using var ipc = new IpcServer(agent, loggerFactory.CreateLogger("Ipc"));
        using var updates = new UpdateService(loggerFactory.CreateLogger("Updates"), dataDir, () => agent.Config.CheckForUpdates);
        updates.CleanUpAfterUpdate();
        var startedWithWindows = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var justUpdated = args.Contains(UpdateService.AfterUpdateArg, StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayContext(agent, ipc, updates, loggerFactory.CreateLogger("Tray"), startedWithWindows, justUpdated));
        log.LogInformation("Savepoint exiting");
        return 0;
    }
}
