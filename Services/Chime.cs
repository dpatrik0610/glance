using System.IO;
using System.Media;

namespace Glance.Services;

/// The alert sound: two soft bell notes a fourth apart (E5, then A5), synthesized once, no sound file to ship.
public static class Chime
{
    static SoundPlayer? player;

    public static void Play()
    {
        if (Native.FullscreenApp()) return; // a game or a presentation in front: stay quiet, like Windows does
        try { (player ??= new SoundPlayer(new MemoryStream(Wave()))).Play(); } // async; returns at once
        catch (Exception e) when (e is InvalidOperationException or IOException) { } // no audio device
    }

    internal static byte[] Wave()
    {
        const int Rate = 44100;
        var samples = new double[(int)(Rate * 1.2)];
        Note(samples, Rate, 659.25, 0.00);
        Note(samples, Rate, 880.00, 0.17);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var tail = Math.Min(1, (samples.Length - i) / (Rate * 0.08)); // last 80 ms fade to silence: no click at the end
            w.Write((short)(Math.Clamp(samples[i] * tail, -1, 1) * 32767 * 0.28));
        }
        return ms.ToArray();
    }

    // Soft attack, long exponential ring, a touch of the octave that fades faster: a small bell, not a beep.
    static void Note(double[] buf, int rate, double hz, double start)
    {
        for (var i = (int)(start * rate); i < buf.Length; i++)
        {
            var t = i / (double)rate - start;
            var env = Math.Min(1, t / 0.012) * Math.Exp(-t * 4.5);
            buf[i] += 0.5 * env * (Math.Sin(2 * Math.PI * hz * t) + 0.22 * Math.Sin(4 * Math.PI * hz * t) * Math.Exp(-t * 6));
        }
    }
}
