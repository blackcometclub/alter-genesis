using System.IO;
using System.Text;
using System.Text.Json;
using AlterGenesis.NotificationProbe.Effects;
using AlterGenesis.NotificationProbe.Models;
using AlterGenesis.NotificationProbe.Settings;

namespace AlterGenesis.NotificationProbe.Diagnostics;

// Runs without audio playback or real settings. Fixtures remain beside the report.
internal static class SoundVerifier
{
    internal static bool Run(string reportPath)
    {
        var reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        var fixtures = Path.Combine(reportDirectory, "sound-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
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

        Check("New settings default to Koron without creating a file", () =>
        {
            var store = new AppSettingsStore(Path.Combine(fixtures, "new"));
            Require(store.Load() == AppSettings.Default && !File.Exists(store.FilePath));
        });
        Check("All four choices survive save and reopen; temporary files are removed", () =>
        {
            var directory = Path.Combine(fixtures, "roundtrip");
            foreach (var sound in Enum.GetValues<NotificationSound>())
            {
                var store = new AppSettingsStore(directory);
                store.Load();
                Require(store.TrySave(AppSettings.Default with { Sound = sound }));
                Require(new AppSettingsStore(directory).Load().Sound == sound);
            }
            Require(Directory.GetFiles(directory).Length == 1);
        });
        var invalidCases = new[] { "{broken", "null", "{}",
            "{\"Version\":2,\"Sound\":\"Koron\"}",
            "{\"Version\":1,\"Sound\":\"Unknown\"}",
            "{\"Version\":1,\"Sound\":99}",
            "{\"Version\":1,\"Sound\":\"Koron\",\"CardSize\":\"Unknown\"}",
            "{\"Version\":1,\"Sound\":\"Koron\",\"CardSize\":99}" };
        for (var index = 0; index < invalidCases.Length; index++)
        {
            var caseIndex = index;
            Check($"Invalid settings {index} are preserved", () =>
            {
                var directory = Path.Combine(fixtures, $"invalid-{caseIndex}");
                Directory.CreateDirectory(directory);
                var store = new AppSettingsStore(directory);
                File.WriteAllText(store.FilePath, invalidCases[caseIndex]);
                Require(store.Load().Sound == NotificationSound.None && store.LoadFailed);
                Require(!store.TrySave(AppSettings.Default with { Sound = NotificationSound.Pikon }));
                Require(File.ReadAllText(store.FilePath) == invalidCases[caseIndex]);
            });
        }
        Check("Failed replacement preserves previous settings and removes temporary files", () =>
        {
            var directory = Path.Combine(fixtures, "locked");
            var store = new AppSettingsStore(directory);
            Require(store.TrySave(AppSettings.Default with { Sound = NotificationSound.Kirari }));
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(!store.TrySave(AppSettings.Default with { Sound = NotificationSound.Pikon }));
            Require(new AppSettingsStore(directory).Load().Sound == NotificationSound.Kirari);
            Require(Directory.GetFiles(directory).Length == 1);
        });
        Check("Legacy sound settings preserve sound and the large layout without rewriting", () =>
        {
            var directory = Path.Combine(fixtures, "legacy");
            Directory.CreateDirectory(directory);
            var store = new AppSettingsStore(directory);
            const string original = "{\"Version\":1,\"Sound\":\"Pikon\"}";
            File.WriteAllText(store.FilePath, original);
            var settings = store.Load();
            Require(settings.Sound == NotificationSound.Pikon && settings.CardSize == NotificationCardSize.Large);
            Require(File.ReadAllText(store.FilePath) == original);
            Require(store.TrySave(settings with { CardSize = NotificationCardSize.Small }));
            Require(new AppSettingsStore(directory).Load() == (settings with { CardSize = NotificationCardSize.Small }));
        });
        Check("Both sizes survive reopening while preserving sound", () =>
        {
            var directory = Path.Combine(fixtures, "sizes");
            foreach (var size in Enum.GetValues<NotificationCardSize>())
            {
                var settings = AppSettings.Default with { Sound = NotificationSound.Kirari, CardSize = size };
                Require(new AppSettingsStore(directory).TrySave(settings));
                Require(new AppSettingsStore(directory).Load() == settings);
            }
        });
        Check("Every embedded sound is 48 kHz / 16-bit / mono PCM with silent endpoints", () =>
        {
            foreach (var sound in new[] { NotificationSound.Koron, NotificationSound.Kirari, NotificationSound.Pikon })
            {
                var wave = WaveSoundPlayer.ReadWave(sound);
                Require(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF"
                    && Encoding.ASCII.GetString(wave, 8, 4) == "WAVE"
                    && BitConverter.ToInt16(wave, 20) == 1
                    && BitConverter.ToInt16(wave, 22) == 1
                    && BitConverter.ToInt32(wave, 24) == 48000
                    && BitConverter.ToInt16(wave, 34) == 16
                    && BitConverter.ToInt16(wave, 44) == 0
                    && BitConverter.ToInt16(wave, wave.Length - 2) == 0);
            }
        });
        Check("Six notifications make one playback request; None, empty and canceled batches are silent", () =>
        {
            var player = new RecordingPlayer();
            var sound = NotificationSound.Kirari;
            var effect = new SoundNotificationEffect(player, () => sound);
            var notifications = SampleNotifications.Create(["--many"]);
            effect.OnNotificationPresentedAsync(notifications).GetAwaiter().GetResult();
            Require(player.Requests == 1 && player.LastSound == sound);
            sound = NotificationSound.None;
            effect.OnNotificationPresentedAsync(notifications).GetAwaiter().GetResult();
            sound = NotificationSound.Pikon;
            effect.OnNotificationPresentedAsync([]).GetAwaiter().GetResult();
            effect.OnNotificationPresentedAsync(notifications, new CancellationToken(true)).GetAwaiter().GetResult();
            Require(player.Requests == 1);
        });
        Check("Playback rejection and exceptions do not escape into notification presentation", () =>
        {
            var player = new RecordingPlayer { Reject = true };
            var effect = new SoundNotificationEffect(player, () => NotificationSound.Koron);
            var notifications = SampleNotifications.Create([]);
            effect.OnNotificationPresentedAsync(notifications).GetAwaiter().GetResult();
            player.Throw = true;
            effect.OnNotificationPresentedAsync(notifications).GetAwaiter().GetResult();
            Require(player.Requests == 2 && notifications.Count == 3);
        });
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Passed = passed, Fixtures = fixtures, Checks = results },
            new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Check failed.");
    }

    private sealed class RecordingPlayer : INotificationSoundPlayer
    {
        internal int Requests;
        internal NotificationSound LastSound;
        internal bool Reject;
        internal bool Throw;
        public bool TryPlay(NotificationSound sound)
        {
            Requests++;
            LastSound = sound;
            if (Throw) throw new IOException("Simulated audio failure");
            return !Reject;
        }
        public void Stop() { }
    }
}
