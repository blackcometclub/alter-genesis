"""Create original, audition-only notification sounds using the standard library."""

import argparse
from array import array
import hashlib
import json
import math
from pathlib import Path
import sys
import wave

RATE = 48_000
TAU = math.tau
ROOT = Path(__file__).resolve().parents[1]
TARGET_RMS = 10 ** (-25 / 20)
PEAK_LIMIT = 10 ** (-12 / 20)


def envelope(t, attack, decay):
    if t < 0:
        return 0.0
    return (1 - math.exp(-t / attack)) ** 2 * math.exp(-t / decay)


def rounded_note(t, frequency):
    if t < 0:
        return 0.0
    phase = TAU * frequency * t
    return envelope(t, .008, .19) * (
        math.sin(phase)
        + .19 * math.exp(-t / .12) * math.sin(2 * phase)
        + .035 * math.exp(-t / .06) * math.sin(3 * phase)
    )


def bell_note(t, frequency):
    if t < 0:
        return 0.0
    phase = TAU * frequency * t
    return envelope(t, .004, .24) * (
        math.sin(phase)
        + .16 * math.exp(-t / .13) * math.sin(2.76 * phase)
        + .035 * math.exp(-t / .07) * math.sin(4.08 * phase)
    )


def electronic_note(t):
    if t < 0:
        return 0.0
    # Integrate a smooth pitch rise from C5 toward F5, without a phase discontinuity.
    frequency_start, frequency_end, glide = 523.251, 698.456, .055
    cycles = frequency_end * t + (frequency_start - frequency_end) * glide * (
        1 - math.exp(-t / glide)
    )
    phase = TAU * cycles
    return envelope(t, .010, .18) * (
        math.sin(phase + .22 * math.exp(-t / .07) * math.sin(2 * phase))
        + .055 * math.sin(3 * phase)
    )


def render(path, duration, voice):
    count = round(duration * RATE)
    samples = []
    for index in range(count):
        t = index / RATE
        release = min(1.0, (count - 1 - index) / (RATE * .10))
        samples.append(voice(t - .04) * math.sin(release * math.pi / 2) ** 2)

    peak = max(map(abs, samples))
    rms = math.sqrt(sum(x * x for x in samples) / count)
    gain = min(TARGET_RMS / rms, PEAK_LIMIT / peak)
    pcm = array('h', (round(x * gain * 32767) for x in samples))
    if sys.byteorder != 'little':
        pcm.byteswap()
    with path.open('xb') as output:
        with wave.open(output, 'wb') as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(RATE)
            wav.writeframes(pcm.tobytes())

    # Reopen the delivered file, checking format, endpoints, and headroom.
    with wave.open(str(path), 'rb') as wav:
        assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (1, 2, RATE)
        decoded = array('h', wav.readframes(wav.getnframes()))
    if sys.byteorder != 'little':
        decoded.byteswap()
    assert len(decoded) == count and decoded[0] == decoded[-1] == 0
    actual_peak = max(map(abs, decoded)) / 32768
    actual_rms = math.sqrt(sum(x * x for x in decoded) / count) / 32768
    assert 0 < actual_peak <= PEAK_LIMIT + 1 / 32768
    assert abs(sum(decoded) / count / 32768) < .001
    return {
        'file': path.name,
        'duration_seconds': duration,
        'sample_rate': RATE,
        'channels': 1,
        'bits': 16,
        'peak_dbfs': round(20 * math.log10(actual_peak), 2),
        'rms_dbfs': round(20 * math.log10(actual_rms), 2),
        'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'audio/candidates/2026-10-28')
    args = parser.parse_args()
    voices = [
        ('01-koron.wav', 1.12, lambda t: rounded_note(t, 523.251)
         + .8 * rounded_note(t - .16, 698.456)),
        ('02-kirari.wav', 1.42, lambda t: bell_note(t, 659.255)
         + .7 * bell_note(t - .20, 987.767)),
        ('03-pikon.wav', .96, electronic_note),
    ]
    if any((args.output / name).exists() for name, _, _ in voices):
        parser.error('Candidate WAVs already exist; choose a new --output folder.')
    args.output.mkdir(parents=True, exist_ok=True)
    results = [render(args.output / name, duration, voice) for name, duration, voice in voices]
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
