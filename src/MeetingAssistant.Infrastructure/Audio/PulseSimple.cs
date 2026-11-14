using System.Runtime.InteropServices;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// <see cref="IPulseSimple"/> на <c>libpulse-simple.so.0</c> из пакета <c>libpulse0</c>.
/// </summary>
/// <remarks>
/// Проверяется только живым проходом: в тестах шов подменён двойником. Библиотека загружается при первом вызове,
/// поэтому на Windows и без неё сборка работает, пока захват Linux не нужен.
/// </remarks>
internal sealed partial class PulseSimple : IPulseSimple
{
    private const string SimpleLibrary = "libpulse-simple.so.0";

    // pa_strerror — в основной библиотеке, libpulse-simple её подгружает.
    private const string CoreLibrary = "libpulse.so.0";

    private const int StreamRecord = 2;
    private const int SampleS16Le = 3;
    private const ulong LatencyUnknown = ulong.MaxValue;

    public bool Load() =>
        OperatingSystem.IsLinux() && NativeLibrary.TryLoad(SimpleLibrary, out _) && NativeLibrary.TryLoad(CoreLibrary, out _);

    public bool TryOpen(string device, string streamName, out nint stream, out string error)
    {
        var spec = new SampleSpec(SampleS16Le, (uint)PulseDeviceStream.DeviceFormat.SampleRate, (byte)PulseDeviceStream.DeviceFormat.Channels);
        stream = SimpleNew(null, "meeting", StreamRecord, device, streamName, in spec, 0, 0, out int code);
        error = stream == 0 ? Describe(code) : string.Empty;
        return stream != 0;
    }

    public bool TryRead(nint stream, Span<byte> data, out string error)
    {
        bool read = SimpleRead(stream, ref MemoryMarshal.GetReference(data), (nuint)data.Length, out int code) >= 0;
        error = read ? string.Empty : Describe(code);
        return read;
    }

    public TimeSpan? Latency(nint stream)
    {
        ulong microseconds = SimpleGetLatency(stream, out _);
        return microseconds == LatencyUnknown ? null : TimeSpan.FromMicroseconds((long)microseconds);
    }

    public void Free(nint stream) => SimpleFree(stream);

    private static string Describe(int code) => Marshal.PtrToStringUTF8(StrError(code)) ?? $"ошибка {code}";

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SimpleNew(
        string? server,
        string name,
        int direction,
        string device,
        string streamName,
        in SampleSpec spec,
        nint channelMap,
        nint bufferAttributes,
        out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_read")]
    private static partial int SimpleRead(nint simple, ref byte data, nuint bytes, out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_get_latency")]
    private static partial ulong SimpleGetLatency(nint simple, out int error);

    [LibraryImport(SimpleLibrary, EntryPoint = "pa_simple_free")]
    private static partial void SimpleFree(nint simple);

    // Строка принадлежит библиотеке и не освобождается.
    [LibraryImport(CoreLibrary, EntryPoint = "pa_strerror")]
    private static partial nint StrError(int error);

    /// <summary>
    /// <c>pa_sample_spec</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SampleSpec(int Format, uint Rate, byte Channels);
}
