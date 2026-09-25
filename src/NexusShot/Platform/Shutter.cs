using System.Runtime.InteropServices;

namespace NexusShot.Platform;

/// <summary>
/// The capture sound: a soft two-note chime, synthesized once and played from memory, so no asset
/// file ships. It replaced a noise-burst shutter that was harsh enough to be switched off on sight.
/// </summary>
internal static unsafe partial class Shutter
{
    private const int SampleRate = 44100;

    /// <summary>Native memory, kept for the process: SND_ASYNC reads it after the call returns.</summary>
    private static byte* _wave;

    public static void Play()
    {
        _wave = _wave is null ? Synthesize() : _wave;
        if (!PlaySoundW(_wave, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
            Core.Log.Info("shutter.play_failed", Marshal.GetLastPInvokeError().ToString());
    }

    private static byte* Synthesize()
    {
        var samples = (int)(SampleRate * 0.34);
        var dataBytes = samples * 2;
        var wave = (byte*)NativeMemory.Alloc((nuint)(44 + dataBytes));

        var header = new Span<byte>(wave, 44);
        "RIFF"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BitConverter.TryWriteBytes(header[16..], 16);                 // fmt chunk size
        BitConverter.TryWriteBytes(header[20..], (short)1);           // PCM
        BitConverter.TryWriteBytes(header[22..], (short)1);           // mono
        BitConverter.TryWriteBytes(header[24..], SampleRate);
        BitConverter.TryWriteBytes(header[28..], SampleRate * 2);     // byte rate
        BitConverter.TryWriteBytes(header[32..], (short)2);           // block align
        BitConverter.TryWriteBytes(header[34..], (short)16);          // bits per sample
        "data"u8.CopyTo(header[36..]);
        BitConverter.TryWriteBytes(header[40..], dataBytes);

        var data = new Span<short>(wave + 44, samples);
        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)SampleRate;
            var sample = Pluck(t, 0, 1046.5, 1.0) + Pluck(t, 0.07, 1568.0, 0.8);   // C6, then G6
            data[i] = (short)Math.Clamp(sample * 7000, short.MinValue, short.MaxValue);
        }
        return wave;

        // Bell-like: a 4 ms fade-in so it starts without a click, then a quick natural decay.
        static double Pluck(double t, double start, double frequency, double gain)
        {
            if (t < start) return 0;
            var local = t - start;
            var envelope = Math.Min(1, local / 0.004) * Math.Exp(-local / 0.07);
            var phase = 2 * Math.PI * frequency * local;
            return gain * envelope * (Math.Sin(phase) + 0.25 * Math.Sin(2 * phase) + 0.08 * Math.Sin(3 * phase));
        }
    }

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    [LibraryImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(byte* sound, IntPtr module, uint flags);
}
