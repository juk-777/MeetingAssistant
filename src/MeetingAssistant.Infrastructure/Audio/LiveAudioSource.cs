using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.SharedKernel;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Живой захват как источник встречи: устройство на канал, звук в 16 кГц моно, молчащий канал — тишиной по часам.
/// </summary>
/// <remarks>
/// Потоки устройств только кладут пакеты в очередь: захват не ждёт ни перевода, ни детектора речи.
/// Loopback не отдаёт пакетов, пока ничего не звучит; без тишины по часам таймкоды канала отставали бы
/// от встречи, а фраза перед паузой не закрывалась бы до следующего звука. Сплошной поток пакетов идёт подряд:
/// расхождение часов устройства с часами процесса не выравнивается. На своё время устройства пакет ставится
/// только на разрыве потока.
/// </remarks>
internal sealed class LiveAudioSource : IAudioSource
{
    /// <summary>
    /// Как часто молчащий канал проверяется на тишину.
    /// </summary>
    public static readonly TimeSpan CheckPeriod = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Насколько тишина по часам отстаёт от часов: пакет, задержанный доставкой меньше, ещё не перекрыт тишиной
    /// и ложится на своё место.
    /// </summary>
    public static readonly TimeSpan DeliveryAllowance = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Разброс отметок времени, в пределах которого пакет считается продолжением предыдущего.
    /// </summary>
    public static readonly TimeSpan StampTolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Насколько отметка устройства может отстоять от прихода пакета: дальше — сбой часов устройства без пометки,
    /// и пакет ставится по приходу, а не срезается или отодвигается на годы.
    /// </summary>
    public static readonly TimeSpan StampLimit = TimeSpan.FromSeconds(2);

    private const int CheckEvent = -1;

    private readonly IReadOnlyList<IDeviceStream> _devices;
    private readonly IReadOnlyList<DeviceAudioConverter> _converters;
    private readonly TimeProvider _time;

    private LiveAudioSource(IReadOnlyList<IDeviceStream> devices, IReadOnlyList<DeviceAudioConverter> converters, TimeProvider time)
    {
        _devices = devices;
        _converters = converters;
        _time = time;
    }

    public IReadOnlyList<IDeviceStream> Devices => _devices;

    /// <summary>
    /// Проверяет форматы устройств.
    /// </summary>
    /// <param name="devices">Устройство на канал встречи.</param>
    /// <param name="time">Часы встречи.</param>
    public static Result<LiveAudioSource> Open(IReadOnlyList<IDeviceStream> devices, TimeProvider time)
    {
        var converters = new List<DeviceAudioConverter>(devices.Count);

        foreach (IDeviceStream device in devices)
        {
            Result<DeviceAudioConverter> converter = DeviceAudioConverter.Create(device.Format);

            if (converter.IsFailure)
            {
                return Result.Failure<LiveAudioSource>(converter.Error);
            }

            converters.Add(converter.Value);
        }

        return Result.Success(new LiveAudioSource(devices, converters, time));
    }

    public async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<CaptureEvent> events = System.Threading.Channels.Channel.CreateUnbounded<CaptureEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        using var capturing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long started = _time.GetTimestamp();
        Timeline[] timelines =
            [.. _devices.Select((device, index) => new Timeline(device.Channel, _converters[index], started, _time))];
        using ITimer check = _time.CreateTimer(
            _ => events.Writer.TryWrite(new CaptureEvent(CheckEvent, null, _time.GetTimestamp(), null)),
            null,
            CheckPeriod,
            CheckPeriod);
        CancellationToken capturingToken = capturing.Token;
        Task[] captures = [.. _devices.Select((device, index) => Capture(index, device, events.Writer, capturingToken))];

        try
        {
            await foreach (CaptureEvent captured in events.Reader.ReadAllAsync(cancellationToken))
            {
                if (captured.Failure is not null)
                {
                    ExceptionDispatchInfo.Throw(captured.Failure);
                }

                IEnumerable<AudioChunk> chunks = captured.Packet is null
                    ? timelines.SelectMany(timeline => timeline.Check(captured.Timestamp))
                    : timelines[captured.Device].Accept(captured.Packet, captured.Timestamp);

                foreach (AudioChunk chunk in chunks)
                {
                    yield return chunk;
                }
            }
        }
        finally
        {
            await capturing.CancelAsync();
            await Task.WhenAll(captures);
        }
    }

    private async Task Capture(int index, IDeviceStream device, ChannelWriter<CaptureEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (DevicePacket packet in device.Read(cancellationToken))
            {
                _ = events.TryWrite(new CaptureEvent(index, packet, _time.GetTimestamp(), null));
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _ = events.TryWrite(new CaptureEvent(
                    index,
                    null,
                    _time.GetTimestamp(),
                    new AudioSourceException($"Устройство «{device.DeviceName}» перестало отдавать звук.")));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Остановка захвата — штатный конец.
        }
        catch (Exception exception)
        {
            // Сбой устройства доходит до читателя очередью: поток захвата не бросает в никуда.
            _ = events.TryWrite(new CaptureEvent(index, null, _time.GetTimestamp(), exception));
        }
    }

    /// <param name="Device">Индекс устройства или <see cref="CheckEvent"/>.</param>
    /// <param name="Packet">Пакет устройства; нет — проверка тишины или сбой.</param>
    /// <param name="Timestamp">Когда пакет пришёл или сработала проверка.</param>
    /// <param name="Failure">Сбой устройства.</param>
    private readonly record struct CaptureEvent(int Device, DevicePacket? Packet, long Timestamp, Exception? Failure);

    /// <summary>
    /// Звук канала на шкале встречи: сколько отдано и где по времени устройства кончился последний пакет.
    /// </summary>
    private sealed class Timeline(Channel channel, DeviceAudioConverter converter, long started, TimeProvider time)
    {
        private long _emitted;

        // Конец последнего пакета от начала встречи по времени устройства; нет — пакетов не было или после
        // него канал закрыла тишина, и следующий пакет ставится на своё время.
        private TimeSpan? _streamEnd;

        public IEnumerable<AudioChunk> Accept(DevicePacket packet, long arrived)
        {
            float[] samples = converter.Convert(packet.Data.Span);
            TimeSpan duration = converter.Duration(packet.Data.Span);
            TimeSpan byArrival = Since(arrived) - duration;
            TimeSpan start = packet.CapturedAt is { } capturedAt && (Since(capturedAt) - byArrival).Duration() <= StampLimit
                ? Since(capturedAt)
                : byArrival;
            ReadOnlyMemory<float> sound = samples;

            if (_streamEnd is not { } end || (start - end).Duration() > StampTolerance)
            {
                long shift = Samples(start) - _emitted;

                if (shift > 0)
                {
                    yield return Silence(shift);
                }
                else if (shift < 0)
                {
                    // Тишина по часам уже закрыла начало пакета: он пришёл позже запаса на доставку.
                    if (-shift >= sound.Length)
                    {
                        // Закрыт целиком: канал всё ещё впереди, следующий пакет тоже ставится на своё время.
                        _streamEnd = null;
                        yield break;
                    }

                    sound = sound[(int)-shift..];
                }
            }

            _streamEnd = start + duration;

            if (sound.Length > 0)
            {
                _emitted += sound.Length;
                yield return new AudioChunk(channel, sound);
            }
        }

        public IEnumerable<AudioChunk> Check(long now)
        {
            TimeSpan horizon = Since(now) - DeliveryAllowance;
            long missing = Samples(horizon) - _emitted;

            if (missing > 0 && (_streamEnd ?? TimeSpan.Zero) < horizon)
            {
                _streamEnd = null;
                yield return Silence(missing);
            }
        }

        private static long Samples(TimeSpan sinceStart) => sinceStart.Ticks * SpeechAudio.SampleRate / TimeSpan.TicksPerSecond;

        private TimeSpan Since(long timestamp) => time.GetElapsedTime(started, timestamp);

        private AudioChunk Silence(long samples)
        {
            _emitted += samples;
            return new AudioChunk(channel, new float[samples]);
        }
    }
}
