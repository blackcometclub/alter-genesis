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
        var expected = $"\"{Path.GetFullPath(executable)}\" --startup";
        Check("Startup command quotes the full executable path", () =>
            Require(StartupRegistration.BuildCommand(executable) == expected));

        Check("Missing registration is disabled", () =>
        {
            var registration = new StartupRegistration(new MemoryValueStore(), executable);
            Require(registration.TryGetEnabled(out var enabled) && !enabled);
        });
        Check("Enable writes only the current executable command", () =>
        {
            var store = new MemoryValueStore();
            var registration = new StartupRegistration(store, executable);
            Require(registration.TrySetEnabled(true) && store.Value == expected);
            Require(registration.TryGetEnabled(out var enabled) && enabled);
        });
        Check("A moved executable is disabled until it replaces the old command", () =>
        {
            var store = new MemoryValueStore { Value = "\"C:\\Old\\AlterGenesis.NotificationProbe.exe\" --startup" };
            var registration = new StartupRegistration(store, executable);
            Require(registration.TryGetEnabled(out var enabled) && !enabled);
            Require(registration.TrySetEnabled(true) && store.Value == expected);
        });
        Check("Disable removes the application registration", () =>
        {
            var store = new MemoryValueStore { Value = expected };
            var registration = new StartupRegistration(store, executable);
            Require(registration.TrySetEnabled(false) && store.Value is null && store.Deletes == 1);
        });
        Check("Read failure is reported without changing the registration", () =>
        {
            var store = new MemoryValueStore { Value = expected, FailRead = true };
            var registration = new StartupRegistration(store, executable);
            Require(!registration.TryGetEnabled(out var enabled) && !enabled && store.Value == expected);
        });
        Check("Write and delete failures are reported without changing the registration", () =>
        {
            var store = new MemoryValueStore { Value = expected, FailWrite = true };
            var registration = new StartupRegistration(store, executable);
            Require(!registration.TrySetEnabled(true) && store.Value == expected);
            store.FailWrite = false;
            store.FailDelete = true;
            Require(!registration.TrySetEnabled(false) && store.Value == expected);
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = passed, Checks = results },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Startup registration verification failed.");
    }

    private sealed class MemoryValueStore : IStartupValueStore
    {
        internal string? Value { get; set; }
        internal int Deletes { get; private set; }
        internal bool FailRead { get; set; }
        internal bool FailWrite { get; set; }
        internal bool FailDelete { get; set; }

        public string? Read()
        {
            if (FailRead) throw new IOException("read");
            return Value;
        }

        public void Write(string command)
        {
            if (FailWrite) throw new UnauthorizedAccessException("write");
            Value = command;
        }

        public void Delete()
        {
            if (FailDelete) throw new SecurityException("delete");
            Value = null;
            Deletes++;
        }
    }
}
