using System.IO;
using System.Media;

namespace Glance.Services;

/// The alert sounds: short, calm bell-like figures, synthesized (no sound files to ship). Picked and leveled in Settings.
public static class Chime
{
    /// Notes: (frequency, start in seconds). Ring: how fast a note fades (higher = shorter). Octave: how much of the
    /// note an octave up rings with it (bell shimmer; 0 = pure, marimba-like).
    record Sound(string Name, (double Hz, double At)[] Notes, double Ring, double Octave);

    static readonly Sound[] Sounds =
    [
        new("Chime", [(659.25, 0), (880.00, 0.17)], 4.5, 0.22),                 // E5 → A5, the original
        new("Rise", [(523.25, 0), (783.99, 0.15)], 4.5, 0.18),                  // C5 → G5
        new("Fall", [(880.00, 0), (659.25, 0.17)], 4.5, 0.22),                  // A5 → E5
        new("Soft", [(392.00, 0), (523.25, 0.20)], 6.0, 0.00),                  // G4 → C5, woody and short
        new("Glass", [(1318.51, 0), (1975.53, 0.12)], 5.5, 0.35),               // E6 → B6, bright
        new("Triad", [(523.25, 0), (659.25, 0.11), (783.99, 0.22)], 4.0, 0.15), // C5 E5 G5
    ];

    public static IEnumerable<string> Names => Sounds.Select(s => s.Name);

    static SoundPlayer? player;
    static string? playerFor;

    /// The chosen sound at the chosen volume. preview: from Settings, so it plays even with a fullscreen app in front.
    public static void Play(bool preview = false)
    {
        if (!preview && Native.FullscreenApp()) return; // a game or a presentation in front: stay quiet, like Windows does
        var s = App.Settings;
        var key = $"{s.AlertSoundName}|{s.AlertSoundVolume:0.00}";
        try
        {
            if (playerFor != key) // rebuilt only when the sound or volume changed
            {
                player?.Dispose();
                player = new SoundPlayer(new MemoryStream(Wave(s.AlertSoundName, s.AlertSoundVolume)));
                playerFor = key;
            }
            player!.Play(); // async; returns at once
        }
        catch (Exception e) when (e is InvalidOperationException or IOException) { } // no audio device
    }

    internal static byte[] Wave(string name, double volume)
    {
        const int Rate = 44100;
        var sound = Sounds.FirstOrDefault(s => s.Name == name) ?? Sounds[0];
        var samples = new double[(int)(Rate * (sound.Notes[^1].At + 1.0))];
        foreach (var (hz, at) in sound.Notes) Note(samples, Rate, hz, at, sound.Ring, sound.Octave);

        var gain = 0.45 * Math.Clamp(volume, 0, 1);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var tail = Math.Min(1, (samples.Length - i) / (Rate * 0.08)); // last 80 ms fade to silence: no click at the end
            w.Write((short)(Math.Clamp(samples[i] * tail * gain, -1, 1) * 32767));
        }
        return ms.ToArray();
    }

    // Soft attack, exponential ring, plus an octave partial that fades faster: a small bell, not a beep.
    static void Note(double[] buf, int rate, double hz, double start, double ring, double octave)
    {
        for (var i = (int)(start * rate); i < buf.Length; i++)
        {
            var t = i / (double)rate - start;
            var env = Math.Min(1, t / 0.012) * Math.Exp(-t * ring);
            buf[i] += 0.5 * env * (Math.Sin(2 * Math.PI * hz * t) + octave * Math.Sin(4 * Math.PI * hz * t) * Math.Exp(-t * 6));
        }
    }
}
