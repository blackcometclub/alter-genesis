using System.IO;
using System.Security;
using Microsoft.Win32;

namespace AlterGenesis.NotificationProbe.Startup;

internal interface IStartupRegistration
{
    bool TryGetEnabled(out bool enabled);
    bool TrySetEnabled(bool enabled);
}

internal interface IStartupValueStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class StartupRegistration(IStartupValueStore store, string executablePath) : IStartupRegistration
{
    private readonly string _command = BuildCommand(executablePath);

    internal static IStartupRegistration ForCurrentProcess() =>
        string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? new UnavailableStartupRegistration()
            : new StartupRegistration(new RegistryStartupValueStore(), Environment.ProcessPath);

    public bool TryGetEnabled(out bool enabled)
    {
        try
        {
            enabled = string.Equals(store.Read(), _command, StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception exception) when (IsRegistrationFailure(exception))
        {
            enabled = false;
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled)
    {
        try
        {
            if (enabled) store.Write(_command);
            else store.Delete();
            return true;
        }
        catch (Exception exception) when (IsRegistrationFailure(exception))
        {
            return false;
        }
    }

    internal static string BuildCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" --startup";

    private static bool IsRegistrationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private sealed class UnavailableStartupRegistration : IStartupRegistration
    {
        public bool TryGetEnabled(out bool enabled)
        {
            enabled = false;
            return false;
        }

        public bool TrySetEnabled(bool enabled) => false;
    }
}

internal sealed class RegistryStartupValueStore : IStartupValueStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TINMOON ALTER GENESIS";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("The Windows startup registry key is unavailable.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
