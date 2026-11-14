using System.Collections.Concurrent;
using MeetingAssistant.Infrastructure.Audio;
using Microsoft.Extensions.Time.Testing;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

/// <summary>
/// Сервер звука для <see cref="IPulseSimple"/>: чтение «длится» пакет по часам теста, отказы и задержка задаются.
/// </summary>
internal sealed class FakePulse(FakeTimeProvider time) : IPulseSimple
{
    private readonly ConcurrentDictionary<nint, string> _open = new();
    private readonly ConcurrentQueue<nint> _freed = new();
    private readonly TaskCompletionSource _anyFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;
    private int _readsAfterFree;
    private nint _lastStream;

    public bool Available { get; init; } = true;

    /// <summary>
    /// Устройство, которое сервер не открывает.
    /// </summary>
    public string? RefusedDevice { get; init; }

    public TimeSpan? ReportedLatency { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Номер чтения (с 1), на котором сервер отказывает.
    /// </summary>
    public int? FailingRead { get; init; }

    /// <summary>
    /// С какого чтения (с 1) оно держится, пока соединение не освободят или не выйдет предел: так видно, освобождено ли
    /// соединение посреди чтения.
    /// </summary>
    public int? HoldingRead { get; init; }

    public const string Refusal = "Connection refused";

    public const string ReadFailure = "Connection terminated";

    public static readonly TimeSpan HoldLimit = TimeSpan.FromMilliseconds(300);

    public List<string> Opened { get; } = [];

    public IReadOnlyCollection<nint> Freed => _freed;

    public int ReadsAfterFree => Volatile.Read(ref _readsAfterFree);

    public string DeviceOf(nint stream) => _open[stream];

    public bool Load() => Available;

    public bool TryOpen(string device, string streamName, out nint stream, out string error)
    {
        Opened.Add(device);

        if (device == RefusedDevice)
        {
            stream = 0;
            error = Refusal;
            return false;
        }

        stream = ++_lastStream;
        _open[stream] = device;
        error = string.Empty;
        return true;
    }

    public bool TryRead(nint stream, Span<byte> data, out string error)
    {
        int read = Interlocked.Increment(ref _reads);

        if (read >= HoldingRead && _anyFreed.Task.Wait(HoldLimit))
        {
            _ = Interlocked.Increment(ref _readsAfterFree);
        }

        if (_freed.Contains(stream))
        {
            _ = Interlocked.Increment(ref _readsAfterFree);
        }

        if (read == FailingRead)
        {
            error = ReadFailure;
            return false;
        }

        data.Fill((byte)read);
        time.Advance(PulseDeviceStream.PacketDuration);

        // Настоящее чтение блокирует на длину пакета: без паузы поток чтения забил бы очередь.
        Thread.Sleep(1);
        error = string.Empty;
        return true;
    }

    public TimeSpan? Latency(nint stream) => ReportedLatency;

    public void Free(nint stream)
    {
        _freed.Enqueue(stream);
        _ = _anyFreed.TrySetResult();
    }
}
