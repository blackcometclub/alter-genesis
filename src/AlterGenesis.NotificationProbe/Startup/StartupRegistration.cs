using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;

namespace AlterGenesis.NotificationProbe.Startup;

internal interface IStartupRegistration
{
    bool TryGetEnabled(out bool enabled);
    bool TrySetEnabled(bool enabled);
}

internal sealed record StartupEntry(string ExecutablePath, string Arguments, string WorkingDirectory);

internal interface IStartupEntryStore
{
    StartupEntry? Read();
    void Write(StartupEntry entry);
    void Delete();
}

internal sealed class StartupRegistration(IStartupEntryStore store, string executablePath,
    string? dataDirectory = null) : IStartupRegistration
{
    private readonly StartupEntry _entry = BuildEntry(executablePath, dataDirectory);

    internal static IStartupRegistration ForCurrentProcess(string? dataDirectory = null) =>
        string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? new UnavailableStartupRegistration()
            : new StartupRegistration(new ShortcutStartupEntryStore(), Environment.ProcessPath, dataDirectory);

    public bool TryGetEnabled(out bool enabled)
    {
        try
        {
            var saved = store.Read();
            enabled = saved is not null
                && string.Equals(saved.ExecutablePath, _entry.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(saved.Arguments, _entry.Arguments, StringComparison.OrdinalIgnoreCase)
                && string.Equals(saved.WorkingDirectory, _entry.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
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
            if (enabled) store.Write(_entry);
            else store.Delete();
            return true;
        }
        catch (Exception exception) when (IsRegistrationFailure(exception))
        {
            return false;
        }
    }

    internal static StartupEntry BuildEntry(string executablePath, string? dataDirectory = null)
    {
        var fullExecutablePath = Path.GetFullPath(executablePath);
        var arguments = "--startup";
        if (!string.IsNullOrWhiteSpace(dataDirectory))
            arguments += $" \"--data-dir={Path.GetFullPath(dataDirectory)}\"";
        return new StartupEntry(
            fullExecutablePath,
            arguments,
            Path.GetDirectoryName(fullExecutablePath) ?? string.Empty);
    }

    private static bool IsRegistrationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or COMException;

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

internal sealed class ShortcutStartupEntryStore : IStartupEntryStore
{
    internal const string ShortcutFileName = "ALTER GENESIS.lnk";

    private readonly string _shortcutPath;

    internal ShortcutStartupEntryStore(string? shortcutPath = null)
    {
        _shortcutPath = shortcutPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutFileName);
    }

    public StartupEntry? Read()
    {
        if (!File.Exists(_shortcutPath)) return null;

        var link = CreateShellLink();
        try
        {
            ((IPersistFile)link).Load(_shortcutPath, 0);
            var executablePath = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var workingDirectory = new StringBuilder(32768);
            link.GetPath(executablePath, executablePath.Capacity, IntPtr.Zero, 0);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            return new StartupEntry(executablePath.ToString(), arguments.ToString(), workingDirectory.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public void Write(StartupEntry entry)
    {
        var startupDirectory = Path.GetDirectoryName(_shortcutPath)
            ?? throw new IOException("The Windows startup folder is unavailable.");
        Directory.CreateDirectory(startupDirectory);
        var temporaryPath = Path.Combine(startupDirectory, $"ALTER GENESIS.{Guid.NewGuid():N}.lnk");
        var link = CreateShellLink();
        try
        {
            link.SetPath(entry.ExecutablePath);
            link.SetArguments(entry.Arguments);
            link.SetWorkingDirectory(entry.WorkingDirectory);
            link.SetDescription("ALTER GENESIS");
            link.SetIconLocation(entry.ExecutablePath, 0);
            ((IPersistFile)link).Save(temporaryPath, true);
            File.Move(temporaryPath, _shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Delete() => File.Delete(_shortcutPath);

    private static IShellLinkW CreateShellLink()
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
        return (IShellLinkW)(Activator.CreateInstance(type)
            ?? throw new COMException("Windows couldn't create a startup shortcut."));
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath,
            IntPtr findData, uint flags);
        void GetIdList(out IntPtr itemIdList);
        void SetIdList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCommand(out int showCommand);
        void SetShowCommand(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath,
            out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
