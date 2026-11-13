using System.Reflection;
using System.Runtime.InteropServices;

namespace Wraithcairn;

/// <summary>Mono 16 kHz float samples from somewhere (microphone, or a WAV file in tests).</summary>
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }

    /// <summary>Human-readable device name, for the UI.</summary>
    string Name { get; }

    void Start();

    /// <summary>
    /// Copies whatever is available into <paramref name="buffer"/> and
    /// returns the count (0 = nothing yet). -1 = end of input (files only).
    /// </summary>
    int Read(float[] buffer);

    void Stop();
}

/// <summary>
/// Microphone capture through OpenAL Soft (bundled per RID by
/// Silk.NET.OpenAL.Soft.Native). One small C API on every platform:
/// WASAPI on Windows, CoreAudio on macOS, PipeWire/PulseAudio/ALSA on Linux.
/// </summary>
public sealed partial class OpenAlCapture : IAudioSource
{
    private const string Lib = "openal";

    private const int AlFormatMono16 = 0x1101;
    private const int AlcCaptureSamples = 0x312;
    private const int AlcCaptureDeviceSpecifier = 0x310;
    private const int AlcCaptureDefaultDeviceSpecifier = 0x311;

    private nint _device;
    private short[] _pcm = new short[4096];
    private bool _started;

    public int SampleRate { get; }

    public string Name { get; }

    static OpenAlCapture()
    {
        NativeLibrary.SetDllImportResolver(typeof(OpenAlCapture).Assembly, Resolve);
    }

    /// <summary>Maps "openal" to the bundled library, then the system one.</summary>
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib)
        {
            return 0;
        }

        string[] candidates = OperatingSystem.IsWindows() ? ["soft_oal", "OpenAL32"]
            : OperatingSystem.IsMacOS() ? ["libopenal.dylib", "libopenal.1.dylib"]
            : ["libopenal.so", "libopenal.so.1"];
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, path, out nint handle))
            {
                return handle;
            }
        }

        return 0;
    }

    [LibraryImport(Lib, EntryPoint = "alcCaptureOpenDevice", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CaptureOpenDevice(string? deviceName, uint frequency, int format, int bufferSize);

    [LibraryImport(Lib, EntryPoint = "alcCaptureCloseDevice")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CaptureCloseDevice(nint device);

    [LibraryImport(Lib, EntryPoint = "alcCaptureStart")]
    private static partial void CaptureStart(nint device);

    [LibraryImport(Lib, EntryPoint = "alcCaptureStop")]
    private static partial void CaptureStop(nint device);

    [LibraryImport(Lib, EntryPoint = "alcCaptureSamples")]
    private static unsafe partial void CaptureSamples(nint device, short* buffer, int samples);

    [LibraryImport(Lib, EntryPoint = "alcGetIntegerv")]
    private static unsafe partial void GetIntegerv(nint device, int param, int size, int* values);

    [LibraryImport(Lib, EntryPoint = "alcGetString")]
    private static partial nint GetString(nint device, int param);

    [LibraryImport(Lib, EntryPoint = "alcGetError")]
    private static partial int GetError(nint device);

    /// <summary>Capture devices OpenAL can see (first = system default order).</summary>
    public static IReadOnlyList<string> Devices()
    {
        nint list = GetString(0, AlcCaptureDeviceSpecifier);
        List<string> names = [];
        if (list == 0)
        {
            return names;
        }

        // Double-NUL terminated list of UTF-8 strings.
        int offset = 0;
        while (true)
        {
            string? item = Marshal.PtrToStringUTF8(list + offset);
            if (string.IsNullOrEmpty(item))
            {
                break;
            }

            names.Add(item);
            offset += System.Text.Encoding.UTF8.GetByteCount(item) + 1;
        }

        return names;
    }

    public static string? DefaultDevice() => Marshal.PtrToStringUTF8(GetString(0, AlcCaptureDefaultDeviceSpecifier));

    /// <param name="device">Exact name, a case-insensitive substring of one, or null for the default.</param>
    public OpenAlCapture(string? device, int sampleRate = 16000)
    {
        SampleRate = sampleRate;
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(device))
        {
            IReadOnlyList<string> all = Devices();
            chosen = all.FirstOrDefault(d => d.Equals(device, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(d => d.Contains(device, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"No microphone matches \"{device}\" (voiceInputDevice). Available: {(all.Count == 0 ? "none" : string.Join(", ", all))}");
        }

        // One second of ring buffer; we drain it every ~50 ms.
        _device = CaptureOpenDevice(chosen, (uint)sampleRate, AlFormatMono16, sampleRate);
        if (_device == 0)
        {
            throw new InvalidOperationException(chosen is null
                ? "Could not open the microphone (no capture device, or access was denied)."
                : $"Could not open the microphone \"{chosen}\".");
        }

        Name = chosen ?? DefaultDevice() ?? "default microphone";
    }

    public void Start()
    {
        if (!_started && _device != 0)
        {
            CaptureStart(_device);
            _started = true;
        }
    }

    public unsafe int Read(float[] buffer)
    {
        if (_device == 0)
        {
            return 0;
        }

        int available = 0;
        GetIntegerv(_device, AlcCaptureSamples, 1, &available);
        int count = Math.Min(available, buffer.Length);
        if (count <= 0)
        {
            return 0;
        }

        if (_pcm.Length < count)
        {
            _pcm = new short[count];
        }

        fixed (short* p = _pcm)
        {
            CaptureSamples(_device, p, count);
        }

        for (int i = 0; i < count; i++)
        {
            buffer[i] = _pcm[i] / 32768f;
        }

        return count;
    }

    public void Stop()
    {
        if (_started && _device != 0)
        {
            CaptureStop(_device);
            _started = false;
        }
    }

    public void Dispose()
    {
        Stop();
        if (_device != 0)
        {
            CaptureCloseDevice(_device);
            _device = 0;
        }
    }
}

/// <summary>
/// Plays a 16-bit PCM WAV file as if it were a microphone, in real time or
/// as fast as possible (selftests, and `--voice-test FILE`).
/// </summary>
public sealed class WavFileSource : IAudioSource
{
    private readonly float[] _samples;
    private readonly bool _realTime;
    private readonly bool _endless;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private int _position;
    private long _silenceSent;

    public int SampleRate { get; }

    public string Name { get; }

    /// <param name="endless">After the audio, keep producing silence (like an open microphone) instead of ending.</param>
    public WavFileSource(string path, bool realTime = false, double trailingSilenceSeconds = 1.5, bool endless = false)
    {
        Name = Path.GetFileName(path);
        _realTime = realTime;
        _endless = endless;
        float[] audio = ReadWav(path, out int rate);
        SampleRate = rate;
        // Trailing silence lets the endpoint detector close the last phrase.
        _samples = new float[audio.Length + (int)(rate * trailingSilenceSeconds)];
        audio.CopyTo(_samples, 0);
    }

    public void Start() => _clock.Start();

    public int Read(float[] buffer)
    {
        if (_position >= _samples.Length)
        {
            if (!_endless)
            {
                return -1;
            }

            // Open-mic silence, paced in real time.
            long silenceDue = (long)(_clock.Elapsed.TotalSeconds * SampleRate) - _samples.Length;
            int silence = (int)Math.Min(buffer.Length, silenceDue - _silenceSent);
            if (silence <= 0)
            {
                return 0;
            }

            Array.Clear(buffer, 0, silence);
            _silenceSent += silence;
            return silence;
        }

        int due = _realTime
            ? (int)Math.Min(_samples.Length, _clock.Elapsed.TotalSeconds * SampleRate)
            : _samples.Length;
        int count = Math.Min(buffer.Length, due - _position);
        if (count <= 0)
        {
            return 0;
        }

        Array.Copy(_samples, _position, buffer, 0, count);
        _position += count;
        return count;
    }

    public void Stop() => _clock.Stop();

    public void Dispose()
    {
    }

    /// <summary>Mono (first channel) 16-bit PCM WAV reader.</summary>
    public static float[] ReadWav(string path, out int sampleRate)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
        {
            throw new InvalidDataException($"{path}: not a WAV file");
        }

        int i = 12;
        int channels = 1;
        int bits = 16;
        sampleRate = 16000;
        while (i + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
            int size = BitConverter.ToInt32(b, i + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, i + 10);
                sampleRate = BitConverter.ToInt32(b, i + 12);
                bits = BitConverter.ToInt16(b, i + 22);
            }
            else if (id == "data")
            {
                if (bits != 16)
                {
                    throw new InvalidDataException($"{path}: only 16-bit PCM is supported");
                }

                size = Math.Min(size, b.Length - i - 8);
                int frames = size / 2 / channels;
                float[] samples = new float[frames];
                for (int k = 0; k < frames; k++)
                {
                    samples[k] = BitConverter.ToInt16(b, i + 8 + (k * 2 * channels)) / 32768f;
                }

                return samples;
            }

            i += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path}: no audio data");
    }
}
