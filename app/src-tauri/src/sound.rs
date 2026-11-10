use crate::settings::{AppSettings, NotificationSound, SoundRepeat};
use std::sync::Mutex;

fn wave(sound: &NotificationSound) -> Option<&'static [u8]> {
    match sound {
        NotificationSound::None => None,
        NotificationSound::Koron => Some(include_bytes!(
            "../../../audio/candidates/2026-10-28/01-koron.wav"
        )),
        NotificationSound::Kirari => Some(include_bytes!(
            "../../../audio/candidates/2026-10-28/02-kirari.wav"
        )),
        NotificationSound::Pikon => Some(include_bytes!(
            "../../../audio/candidates/2026-10-28/03-pikon.wav"
        )),
    }
}

#[cfg(windows)]
fn play(sound: &NotificationSound) -> Result<(), String> {
    use windows_sys::Win32::Media::Audio::{PlaySoundW, SND_ASYNC, SND_MEMORY, SND_NODEFAULT};
    let Some(bytes) = wave(sound) else {
        stop();
        return Ok(());
    };
    // SND_MEMORY interprets the pointer as WAV bytes, not a UTF-16 string.
    // include_bytes! keeps this immutable buffer alive for the entire async playback.
    let success = unsafe {
        PlaySoundW(
            bytes.as_ptr().cast(),
            std::ptr::null_mut(),
            SND_ASYNC | SND_MEMORY | SND_NODEFAULT,
        )
    };
    if success == 0 {
        Err("sound_playback_failed".into())
    } else {
        eprintln!("sound_playback_started: {sound:?}");
        Ok(())
    }
}

#[cfg(windows)]
fn stop() {
    // NULL stops only this process's PlaySound playback; no buffer is dereferenced.
    unsafe {
        windows_sys::Win32::Media::Audio::PlaySoundW(std::ptr::null(), std::ptr::null_mut(), 0);
    }
}

#[cfg(not(windows))]
fn play(sound: &NotificationSound) -> Result<(), String> {
    if wave(sound).is_none() {
        Ok(())
    } else {
        Err("sound_playback_failed".into())
    }
}

#[cfg(not(windows))]
fn stop() {}

#[derive(Clone, PartialEq, Eq)]
struct SoundOptions {
    sound: NotificationSound,
    repeat: SoundRepeat,
}

impl SoundOptions {
    fn from_settings(settings: &AppSettings) -> Self {
        Self {
            sound: settings.notification_sound.clone(),
            repeat: settings.sound_repeat.clone(),
        }
    }

    fn delay_ms(&self) -> Option<u64> {
        if self.sound == NotificationSound::None {
            return None;
        }
        let minutes = match self.repeat {
            SoundRepeat::None => return None,
            SoundRepeat::OneMinute => 1,
            SoundRepeat::ThreeMinutes => 3,
            SoundRepeat::FiveMinutes => 5,
            SoundRepeat::TenMinutes => 10,
        };
        Some(minutes * 60_000)
    }
}

// Owns only the repeat deadline. NotificationState remains the visibility authority.
#[derive(Default)]
struct RepeatSchedule {
    next_due_ms: Option<u64>,
}

impl RepeatSchedule {
    fn reset(&mut self, options: &SoundOptions, visible: bool, now: u64) {
        self.next_due_ms = if visible {
            options.delay_ms().map(|delay| now.saturating_add(delay))
        } else {
            None
        };
    }

    fn tick(&mut self, options: &SoundOptions, visible: bool, now: u64) -> bool {
        if !visible || options.delay_ms().is_none() {
            self.next_due_ms = None;
            return false;
        }
        if self.next_due_ms.is_some_and(|due| now >= due) {
            // After sleep/clock jumps, play once; do not catch up missed intervals.
            self.reset(options, true, now);
            true
        } else {
            false
        }
    }
}

struct SoundSession {
    options: SoundOptions,
    schedule: RepeatSchedule,
}

pub struct SoundState {
    session: Mutex<SoundSession>,
}

impl SoundState {
    pub fn new(settings: &AppSettings) -> Self {
        Self {
            session: Mutex::new(SoundSession {
                options: SoundOptions::from_settings(settings),
                schedule: RepeatSchedule::default(),
            }),
        }
    }

    pub fn presented(&self, now: u64, play_initial: bool) -> Result<(), String> {
        let mut session = self.session.lock().map_err(|_| "sound_state_unavailable")?;
        let options = session.options.clone();
        session.schedule.reset(&options, true, now);
        if play_initial {
            play(&options.sound)?;
        }
        Ok(())
    }

    pub fn configure(&self, settings: &AppSettings, visible: impl FnOnce() -> bool, now: u64) {
        if let Ok(mut session) = self.session.lock() {
            let options = SoundOptions::from_settings(settings);
            if options != session.options {
                stop();
                session.schedule.reset(&options, visible(), now);
                session.options = options;
            }
        }
    }

    pub fn tick(&self, visible: impl FnOnce() -> bool, now: u64) -> Result<(), String> {
        let mut session = self.session.lock().map_err(|_| "sound_state_unavailable")?;
        // Read visibility under the sound lock so a stale tick cannot outlive pause().
        let visible = visible();
        let options = session.options.clone();
        if session.schedule.tick(&options, visible, now) {
            play(&options.sound)?;
        }
        Ok(())
    }

    pub fn pause(&self) {
        if let Ok(mut session) = self.session.lock() {
            session.schedule.next_due_ms = None;
            stop();
        }
    }

    pub fn preview(&self) -> Result<(), String> {
        let session = self.session.lock().map_err(|_| "sound_state_unavailable")?;
        // Preview does not start or postpone the notification repeat schedule.
        play(&session.options.sound)
    }
}

impl Drop for SoundState {
    fn drop(&mut self) {
        stop();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn options() -> SoundOptions {
        SoundOptions {
            sound: NotificationSound::Koron,
            repeat: SoundRepeat::OneMinute,
        }
    }

    #[test]
    fn repeats_only_at_the_deadline_and_once_after_a_long_pause() {
        let options = options();
        let mut schedule = RepeatSchedule::default();
        schedule.reset(&options, true, 1_000);
        assert!(!schedule.tick(&options, true, 60_999));
        assert!(schedule.tick(&options, true, 61_000));
        assert!(!schedule.tick(&options, true, 61_000));
        assert!(schedule.tick(&options, true, 1_000_000));
        assert_eq!(schedule.next_due_ms, Some(1_060_000));
    }

    #[test]
    fn hidden_dismissed_and_muted_notifications_cancel_the_repeat() {
        let mut options = options();
        let mut schedule = RepeatSchedule::default();
        schedule.reset(&options, true, 0);
        assert!(!schedule.tick(&options, false, 70_000));
        assert!(!schedule.tick(&options, true, 100_000));
        options.sound = NotificationSound::None;
        schedule.reset(&options, true, 100_000);
        assert_eq!(schedule.next_due_ms, None);
        assert!(!schedule.tick(&options, true, 900_000));
    }

    #[test]
    fn settings_changes_and_new_presentations_reset_from_now() {
        let mut options = options();
        let mut schedule = RepeatSchedule::default();
        schedule.reset(&options, true, 0);
        options.repeat = SoundRepeat::ThreeMinutes;
        schedule.reset(&options, true, 50_000);
        assert!(!schedule.tick(&options, true, 60_000));
        assert_eq!(schedule.next_due_ms, Some(230_000));
        schedule.reset(&options, false, 100_000);
        schedule.reset(&options, true, 400_000);
        assert_eq!(schedule.next_due_ms, Some(580_000));
        assert!(!schedule.tick(&options, true, 300_000));
    }

    #[test]
    fn all_repeat_choices_and_no_repeat_are_supported() {
        for (repeat, expected) in [
            (SoundRepeat::None, None),
            (SoundRepeat::OneMinute, Some(60_000)),
            (SoundRepeat::ThreeMinutes, Some(180_000)),
            (SoundRepeat::FiveMinutes, Some(300_000)),
            (SoundRepeat::TenMinutes, Some(600_000)),
        ] {
            assert_eq!(
                SoundOptions {
                    repeat,
                    ..options()
                }
                .delay_ms(),
                expected
            );
        }
    }

    #[test]
    fn embedded_sounds_are_the_existing_pcm_wave_assets() {
        assert!(wave(&NotificationSound::None).is_none());
        for (sound, size) in [
            (NotificationSound::Koron, 107564),
            (NotificationSound::Kirari, 136364),
            (NotificationSound::Pikon, 92204),
        ] {
            let bytes = wave(&sound).unwrap();
            assert_eq!(bytes.len(), size);
            assert_eq!(&bytes[..4], b"RIFF");
            assert_eq!(&bytes[8..12], b"WAVE");
            assert_eq!(u16::from_le_bytes(bytes[20..22].try_into().unwrap()), 1);
            assert_eq!(u16::from_le_bytes(bytes[22..24].try_into().unwrap()), 1);
            assert_eq!(
                u32::from_le_bytes(bytes[24..28].try_into().unwrap()),
                48_000
            );
            assert_eq!(u16::from_le_bytes(bytes[34..36].try_into().unwrap()), 16);
        }
    }
}
