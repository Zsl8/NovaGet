#!/usr/bin/env python3
"""Synthesizes NovaGet's default notification sounds (original, public domain) into assets/sounds.

Run: python3 tools/make-sounds.py   (deterministic: same output every time)
"""
import math
import pathlib
import struct
import wave

RATE = 22050
OUT = pathlib.Path(__file__).resolve().parent.parent / "assets" / "sounds"


def note(freq, start, length, volume=0.5, decay=6.0):
    """A soft bell: fundamental plus two quiet harmonics, short attack, exponential decay."""
    return (freq, start, length, volume, decay)


def render(notes, total):
    samples = [0.0] * int(RATE * total)
    for freq, start, length, volume, decay in notes:
        first = int(RATE * start)
        count = int(RATE * length)
        for i in range(count):
            if first + i >= len(samples):
                break
            t = i / RATE
            attack = min(1.0, t / 0.006)
            envelope = attack * math.exp(-decay * t)
            value = (math.sin(2 * math.pi * freq * t)
                     + 0.30 * math.sin(2 * math.pi * freq * 2 * t)
                     + 0.12 * math.sin(2 * math.pi * freq * 3 * t)) / 1.42
            samples[first + i] += volume * envelope * value
    # Short fade-out so nothing clicks at the end.
    fade = int(RATE * 0.02)
    for i in range(fade):
        samples[-1 - i] *= i / fade
    return samples


def write(name, samples):
    OUT.mkdir(parents=True, exist_ok=True)
    peak = max(1e-9, max(abs(s) for s in samples))
    scale = min(1.0, 0.85 / peak)
    with wave.open(str(OUT / name), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(b"".join(struct.pack("<h", int(s * scale * 32767)) for s in samples))


C5, E5, G5, C6, E6, G6 = 523.25, 659.25, 783.99, 1046.50, 1318.51, 1567.98
A3, C4, E4 = 220.00, 261.63, 329.63

write("complete.wav", render([note(C6, 0.00, 0.6), note(G6, 0.12, 0.6, 0.45)], 0.75))
write("failed.wav", render([note(E4, 0.00, 0.5, 0.6, 5.0), note(A3, 0.18, 0.6, 0.6, 4.0)], 0.8))
write("queue-started.wav", render([note(C5, 0.00, 0.25, 0.4, 10), note(E5, 0.09, 0.25, 0.4, 10), note(G5, 0.18, 0.4, 0.45, 8)], 0.6))
write("queue-finished.wav", render([note(G5, 0.00, 0.25, 0.4, 10), note(E5, 0.09, 0.25, 0.4, 10), note(C5, 0.18, 0.45, 0.45, 7)], 0.65))
write("added.wav", render([note(E6, 0.00, 0.18, 0.35, 18)], 0.2))
print("wrote", sorted(p.name for p in OUT.glob("*.wav")))
