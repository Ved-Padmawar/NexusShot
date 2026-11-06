using System.Diagnostics;

namespace NexusShot.Core;

public enum RecordingFormat { Mp4, Gif }
public enum RecordingQuality { Standard, High }
public enum RecordingPhase { Idle, Countdown, Recording, Paused, Finalizing }

/// <summary>Bounds the encoder workload independently of desktop size.</summary>
public sealed class RecordingPlan
{
    public static readonly int[] FrameRates = [30, 60];

    /// <summary>GIF length limits in seconds; 0 is none.</summary>
    public static readonly int[] GifLimits = [0, 15, 30, 60];

    private const int GifFramesPerSecond = 10;

    /// <summary>H.264 bits per pixel per frame: Standard keeps screen text sharp, High holds up in motion.</summary>
    private const double StandardBitsPerPixel = 0.12;
    private const double HighBitsPerPixel = 0.25;

    public RecordingFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public string Extension { get; }

    /// <summary>MP4 only.</summary>
    public int BitsPerSecond { get; }

    /// <summary>When the recording stops itself, or null to run until stopped.</summary>
    public TimeSpan? Limit { get; }

    /// <summary>H.264 needs even dimensions; GIF is capped lower because every frame is palettised.</summary>
    public RecordingPlan(AppSettings settings, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        var (fps, maxWidth, maxHeight, alignment, extension) = settings.RecordingFormat switch
        {
            RecordingFormat.Mp4 => (settings.RecordingFrameRate, 3840.0, 2160.0, 2, ".mp4"),
            RecordingFormat.Gif => (GifFramesPerSecond, 1280.0, 720.0, 1, ".gif"),
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
        Format = settings.RecordingFormat;
        FramesPerSecond = fps;
        Extension = extension;
        var scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        Width = Math.Max(alignment, (int)(width * scale) / alignment * alignment);
        Height = Math.Max(alignment, (int)(height * scale) / alignment * alignment);
        var bitsPerPixel = settings.RecordingQuality == RecordingQuality.High ? HighBitsPerPixel : StandardBitsPerPixel;
        BitsPerSecond = (int)Math.Clamp((long)(Width * (double)Height * fps * bitsPerPixel), 2_000_000, 80_000_000);
        Limit = Format == RecordingFormat.Gif && settings.GifLimitSeconds > 0 ? TimeSpan.FromSeconds(settings.GifLimitSeconds) : null;
    }
}

/// <summary>Recorded time: it runs while recording and holds while paused, so paused time never reaches
/// the file. The UI thread drives it and the encoder thread reads it, hence the lock.</summary>
public sealed class RecordingClock(Func<long>? ticks = null)
{
    private readonly Func<long> _now = ticks ?? (() => Stopwatch.GetElapsedTime(0).Ticks);
    private readonly Lock _gate = new();
    private long _banked;
    private long? _since;

    public bool IsRunning
    {
        get { lock (_gate) return _since is not null; }
    }

    public TimeSpan Elapsed
    {
        get { lock (_gate) return TimeSpan.FromTicks(_banked + (_since is { } since ? _now() - since : 0)); }
    }

    public void Run()
    {
        lock (_gate) _since ??= _now();
    }

    public void Hold()
    {
        lock (_gate)
        {
            if (_since is not { } since) return;
            _banked += _now() - since;
            _since = null;
        }
    }
}

/// <summary>The UI owns these transitions; the encoder reads only the clock and a stop signal.</summary>
public sealed class RecordingState(Func<long>? ticks = null)
{
    /// <summary>Long enough to move the pointer off the Record button and into place.</summary>
    public const int CountdownSeconds = 3;

    public RecordingPhase Phase { get; private set; }
    public bool Discard { get; private set; }

    /// <summary>A new clock per recording, so an encoder still finishing never reads the next one's time.</summary>
    public RecordingClock Clock { get; private set; } = new(ticks);

    public bool IsBusy => Phase != RecordingPhase.Idle;
    public bool IsCapturing => Phase is RecordingPhase.Recording or RecordingPhase.Paused;

    public bool TryBegin()
    {
        if (IsBusy) return false;
        Phase = RecordingPhase.Countdown;
        Discard = false;
        Clock = new RecordingClock(ticks);
        return true;
    }

    public void Start()
    {
        if (Phase != RecordingPhase.Countdown) throw new InvalidOperationException("No recording is counting down.");
        Phase = RecordingPhase.Recording;
        Clock.Run();
    }

    public bool TogglePause()
    {
        switch (Phase)
        {
            case RecordingPhase.Recording:
                Phase = RecordingPhase.Paused;
                Clock.Hold();
                return true;
            case RecordingPhase.Paused:
                Phase = RecordingPhase.Recording;
                Clock.Run();
                return true;
            default:
                return false;
        }
    }

    public bool Stop(bool discard)
    {
        if (!IsCapturing) return false;
        Discard = discard;
        Phase = RecordingPhase.Finalizing;
        Clock.Hold();
        return true;
    }

    public void Finish()
    {
        Clock.Hold();
        Phase = RecordingPhase.Idle;
    }
}
