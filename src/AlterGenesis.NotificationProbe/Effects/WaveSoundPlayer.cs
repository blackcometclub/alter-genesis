using System.IO;
using System.Runtime.InteropServices;

namespace AlterGenesis.NotificationProbe.Effects;

// Used on the UI thread. Pinned buffers stay alive until playback stops at disposal.
public sealed class WaveSoundPlayer : INotificationSoundPlayer, IDisposable
{
    private readonly Dictionary<NotificationSound, GCHandle> _buffers = new();
    private bool _disposed;

    internal static byte[] ReadWave(NotificationSound sound)
    {
        using var stream = typeof(WaveSoundPlayer).Assembly.GetManifestResourceStream(
            $"AlterGenesis.Sounds.{sound}.wav")
            ?? throw new InvalidDataException($"Missing sound resource: {sound}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public bool TryPlay(NotificationSound sound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sound == NotificationSound.None)
        {
            Stop();
            return true;
        }

        if (!_buffers.TryGetValue(sound, out var buffer))
        {
            buffer = GCHandle.Alloc(ReadWave(sound), GCHandleType.Pinned);
            _buffers.Add(sound, buffer);
        }

        // SND_ASYNC | SND_NODEFAULT | SND_MEMORY: no loop, no fallback system beep.
        return PlaySound(buffer.AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004);
    }

    public void Stop() => PlaySound(IntPtr.Zero, IntPtr.Zero, 0);

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        foreach (var buffer in _buffers.Values) buffer.Free();
        _buffers.Clear();
        _disposed = true;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
