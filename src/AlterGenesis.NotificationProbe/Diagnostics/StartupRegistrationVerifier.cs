using System.IO;
using System.Security;
using System.Text.Json;
using AlterGenesis.NotificationProbe.Startup;

namespace AlterGenesis.NotificationProbe.Diagnostics;

internal static class StartupRegistrationVerifier
{
    internal static bool Run(string reportPath)
    {
        var results = new List<object>();
        var passed = true;
        void Check(string name, Action test)
        {
            try { test(); results.Add(new { Name = name, Passed = true }); }
            catch (Exception error)
            {
                passed = false;
                results.Add(new { Name = name, Passed = false, Error = error.Message });
            }
        }

        var executable = Path.Combine(Path.GetTempPath(), "ALTER GENESIS test", "AlterGenesis.NotificationProbe.exe");
        var expected = new StartupEntry(
            Path.GetFullPath(executable),
            "--startup",
            Path.GetDirectoryName(Path.GetFullPath(executable))!);
        Check("Startup shortcut targets the full executable path", () =>
            Require(StartupRegistration.BuildEntry(executable) == expected));
        Check("Explicit data directory is normalized and quoted in shortcut arguments", () =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), "ALTER GENESIS data", "live");
            var entry = StartupRegistration.BuildEntry(executable, dataDirectory);
            Require(entry == expected with
            {
                Arguments = $"--startup \"--data-dir={Path.GetFullPath(dataDirectory)}\""
            });
        });

        Check("Missing shortcut is disabled", () =>
        {
            var registration = new StartupRegistration(new MemoryEntryStore(), executable);
            Require(registration.TryGetEnabled(out var enabled) && !enabled);
        });
        Check("Enable writes only the current executable shortcut", () =>
        {
            var store = new MemoryEntryStore();
            var registration = new StartupRegistration(store, executable);
            Require(registration.TrySetEnabled(true) && store.Value == expected);
            Require(registration.TryGetEnabled(out var enabled) && enabled);
        });
        Check("A moved executable is disabled until it replaces the old shortcut", () =>
        {
            var store = new MemoryEntryStore
            {
                Value = new StartupEntry("C:\\Old\\AlterGenesis.NotificationProbe.exe", "--startup", "C:\\Old")
            };
            var registration = new StartupRegistration(store, executable);
            Require(registration.TryGetEnabled(out var enabled) && !enabled);
            Require(registration.TrySetEnabled(true) && store.Value == expected);
        });
        Check("Disable removes the application shortcut", () =>
        {
            var store = new MemoryEntryStore { Value = expected };
            var registration = new StartupRegistration(store, executable);
            Require(registration.TrySetEnabled(false) && store.Value is null && store.Deletes == 1);
        });
        Check("Read failure is reported without changing the shortcut", () =>
        {
            var store = new MemoryEntryStore { Value = expected, FailRead = true };
            var registration = new StartupRegistration(store, executable);
            Require(!registration.TryGetEnabled(out var enabled) && !enabled && store.Value == expected);
        });
        Check("Write and delete failures are reported without changing the shortcut", () =>
        {
            var store = new MemoryEntryStore { Value = expected, FailWrite = true };
            var registration = new StartupRegistration(store, executable);
            Require(!registration.TrySetEnabled(true) && store.Value == expected);
            store.FailWrite = false;
            store.FailDelete = true;
            Require(!registration.TrySetEnabled(false) && store.Value == expected);
        });
        Check("Windows shortcut can be written, read and removed in an isolated folder", () =>
        {
            var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))
                ?? throw new IOException("The startup verification folder is unavailable.");
            var directory = Path.Combine(reportDirectory, $".startup-shortcut-{Guid.NewGuid():N}");
            var shortcutPath = Path.Combine(directory, ShortcutStartupEntryStore.ShortcutFileName);
            var store = new ShortcutStartupEntryStore(shortcutPath);
            try
            {
                store.Write(expected);
                Require(File.Exists(shortcutPath));
                Require(store.Read() == expected);
                store.Delete();
                Require(!File.Exists(shortcutPath));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = passed, Checks = results },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Startup shortcut verification failed.");
    }

    private sealed class MemoryEntryStore : IStartupEntryStore
    {
        internal StartupEntry? Value { get; set; }
        internal int Deletes { get; private set; }
        internal bool FailRead { get; set; }
        internal bool FailWrite { get; set; }
        internal bool FailDelete { get; set; }

        public StartupEntry? Read()
        {
            if (FailRead) throw new IOException("read");
            return Value;
        }

        public void Write(StartupEntry entry)
        {
            if (FailWrite) throw new UnauthorizedAccessException("write");
            Value = entry;
        }

        public void Delete()
        {
            if (FailDelete) throw new SecurityException("delete");
            Value = null;
            Deletes++;
        }
    }
}
