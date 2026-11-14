using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.SharedKernel;
using NAudio.Wave;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Пакеты устройства Linux с сервера звука: микрофон по умолчанию или монитор устройства вывода по умолчанию.
/// </summary>
/// <remarks>
/// Чтение блокирующее, поэтому идёт в своём потоке, а пакеты отдаются очередью. Соединение освобождается, когда
/// чтение закончено, или <see cref="Dispose"/>, если чтение не начиналось.
/// </remarks>
internal sealed class PulseDeviceStream : IDeviceStream, IDisposable
{
    /// <summary>
    /// Формат, в котором сервер отдаёт звук: частоту и каналы устройства переводит он сам.
    /// </summary>
    public static readonly WaveFormat DeviceFormat = new(SpeechAudio.SampleRate, 16, 1);

    /// <summary>
    /// Длина пакета: столько звука ждёт одно чтение.
    /// </summary>
    public static readonly TimeSpan PacketDuration = TimeSpan.FromMilliseconds(100);

    private static readonly int PacketBytes = (int)(DeviceFormat.AverageBytesPerSecond * PacketDuration.TotalSeconds);

    private readonly IPulseSimple _pulse;
    private readonly TimeProvider _time;
    private nint _stream;

    private PulseDeviceStream(Channel channel, string device, string deviceName, IPulseSimple pulse, nint stream, TimeProvider time)
    {
        Channel = channel;
        Device = device;
        DeviceName = deviceName;
        _pulse = pulse;
        _stream = stream;
        _time = time;
    }

    public Channel Channel { get; }

    /// <summary>
    /// Имя источника на сервере звука.
    /// </summary>
    public string Device { get; }

    public string DeviceName { get; }

    public WaveFormat Format => DeviceFormat;

    /// <summary>
    /// Открывает устройство канала: «Я» — микрофон по умолчанию, «Собеседники» — монитор устройства вывода по умолчанию.
    /// </summary>
    public static Result<PulseDeviceStream> Open(Channel channel, IPulseSimple pulse, TimeProvider time)
    {
        (string device, string name, string kind) = channel == Channel.Me
            ? ("@DEFAULT_SOURCE@", "микрофон по умолчанию", "микрофон")
            : ("@DEFAULT_MONITOR@", "монитор вывода по умолчанию", "монитор устройства вывода");

        return pulse.TryOpen(device, name, out nint stream, out string error)
            ? Result.Success(new PulseDeviceStream(channel, device, name, pulse, stream, time))
            : Result.Failure<PulseDeviceStream>(AudioErrors.SoundServerRefused(kind, error));
    }

    public async IAsyncEnumerable<DevicePacket> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<DevicePacket> packets = System.Threading.Channels.Channel.CreateUnbounded<DevicePacket>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken readingToken = reading.Token;
        Task reader = Task.Factory.StartNew(
            () => ReadPackets(packets.Writer, readingToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await foreach (DevicePacket packet in packets.Reader.ReadAllAsync(cancellationToken))
            {
                yield return packet;
            }
        }
        finally
        {
            // Соединение освобождается только после того, как поток чтения из него вышел.
            await reading.CancelAsync();
            await reader;
            Dispose();
        }
    }

    public void Dispose()
    {
        nint stream = Interlocked.Exchange(ref _stream, 0);

        if (stream != 0)
        {
            _pulse.Free(stream);
        }
    }

    // Поток чтения: отказ сервера закрывает очередь с ошибкой, и она доходит до читателя.
    private void ReadPackets(ChannelWriter<DevicePacket> packets, CancellationToken cancellationToken)
    {
        Exception? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] data = new byte[PacketBytes];

                if (!_pulse.TryRead(_stream, data, out string error))
                {
                    failure = new AudioSourceException($"Устройство «{DeviceName}»: {error}");
                    return;
                }

                long arrived = _time.GetTimestamp();
                _ = packets.TryWrite(new DevicePacket(data, CapturedAt(arrived)));
            }
        }
        catch (Exception exception)
        {
            failure = new AudioSourceException($"Устройство «{DeviceName}»: {exception.Message}", exception);
        }
        finally
        {
            _ = packets.TryComplete(failure);
        }
    }

    // Задержка — возраст звука, следующего за прочитанным: последний кадр пакета записан на неё раньше прихода,
    // первый — ещё на длину пакета.
    private long? CapturedAt(long arrived) =>
        _pulse.Latency(_stream) is { } latency
            ? arrived - (long)((Int128)(latency + PacketDuration).Ticks * _time.TimestampFrequency / TimeSpan.TicksPerSecond)
            : null;
}
