using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Time.Testing;
using NAudio.Wave;
using Shouldly;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class LiveAudioSourceTests : IAsyncDisposable
{
    private const float Voice = 0.5f;

    private const float Marker = 0.25f;

    // Звук проходит без перевода, поэтому допуск — только на сравнение float, а не на обработку.
    private const float SampleTolerance = 1e-6f;

    // Устройство сразу в 16 кГц моно: перевод проверен отдельно, здесь отсчёты считаются точно.
    private static readonly WaveFormat SpeechFormat = WaveFormat.CreateIeeeFloatWaveFormat(SpeechAudio.SampleRate, 1);

    private static readonly int PacketSamples = Samples(LiveAudioSource.CheckPeriod);

    private static readonly int AllowanceSamples = Samples(LiveAudioSource.DeliveryAllowance);

    // Предел ожидания: механизм, отдающий не тот звук, должен ронять тест, а не вешать прогон.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeDevice _me = new(Channel.Me, SpeechFormat);
    private readonly FakeDevice _others = new(Channel.Others, SpeechFormat);
    private readonly CancellationTokenSource _reading = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly Lock _lock = new();
    private readonly Dictionary<Channel, List<float>> _received = new() { [Channel.Me] = [], [Channel.Others] = [] };
    private Task? _collecting;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_WhenChannelGetsNoPackets_GivesSilenceUpToClockLessAllowanceAndLeavesStreamUntouched()
    {
        await Start();

        for (int packet = 1; packet <= 20; packet++)
        {
            await Step(_me);
        }

        await Until(() => Received(Channel.Others).Length == Clock() - AllowanceSamples);
        Received(Channel.Others).ShouldAllBe(sample => sample == 0);
        Received(Channel.Me).ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Theory]
    [InlineData(1.1)]
    [InlineData(0.9)]
    public async Task Read_WhenDeviceClockDiffersFromProcessClock_KeepsContinuousStreamWhole(double periodScale)
    {
        await Start();
        TimeSpan period = LiveAudioSource.CheckPeriod * periodScale;

        // Устройство пишет 100 мс звука за 110 (или 90) мс часов процесса: отметки пакетов расходятся с их длиной.
        // За 60 пакетов канал расходится с часами на 600 мс — больше запаса на доставку.
        for (int packet = 1; packet <= 60; packet++)
        {
            _time.Advance(period);
            _me.Push(Packet(Voice, Stamp(period)));
            int expected = packet * PacketSamples;
            await Until(() => Received(Channel.Me).Length == expected);
        }

        Received(Channel.Me).ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenSoundReturnsAfterPauses_KeepsChannelOnClock()
    {
        await Start();

        for (int cycle = 0; cycle < 3; cycle++)
        {
            for (int packet = 0; packet < 5; packet++)
            {
                await Step(_others);
                Received(Channel.Others).Length.ShouldBe(Clock());
            }

            Advance(7);

            await Until(() => Received(Channel.Others).Length == Clock() - AllowanceSamples);
        }
    }

    [Fact]
    public async Task Read_WhenSoundReturnsAfterPause_PutsPacketOnDeviceTimeNotArrival()
    {
        await Start();
        Advance(7);
        await Until(() => Received(Channel.Others).Length == Clock() - AllowanceSamples);

        // Пакет записан с 600 до 700 мс, а дошёл на 750.
        _time.Advance(LiveAudioSource.CheckPeriod / 2);
        _others.Push(Packet(Voice, Stamp(LiveAudioSource.CheckPeriod * 1.5)));

        int recorded = Samples(LiveAudioSource.CheckPeriod * 7);
        await Until(() => Received(Channel.Others).Length >= recorded);
        float[] others = Received(Channel.Others);
        others.Length.ShouldBe(recorded);
        others[..^PacketSamples].ShouldAllBe(sample => sample == 0);
        others[^PacketSamples..].ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenStreamBreaksShorterThanAllowance_PutsNextPacketOnDeviceTime()
    {
        await Start();

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        // 200 мс без пакетов (выпавшие пакеты тишины): тишина по часам сюда ещё не дошла.
        Advance(2);
        await Step(_me);

        float[] me = Received(Channel.Me);
        me.Length.ShouldBe(Clock());
        me[(5 * PacketSamples)..(7 * PacketSamples)].ShouldAllBe(sample => sample == 0);
        me[(7 * PacketSamples)..].ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenPacketStartsBeforePreviousEnds_DropsWhatChannelAlreadyHas()
    {
        await Start();

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        // Отметка откатилась на 150 мс: пакет 350–450 мс, а канал уже дошёл до 500. Следом — штатный пакет
        // с другим уровнем: по нему видно, что оба пакета разобраны (очередь устройства идёт по порядку).
        _me.Push(Packet(Voice, Stamp(LiveAudioSource.CheckPeriod * 1.5)));
        _time.Advance(LiveAudioSource.CheckPeriod);
        _me.Push(Packet(Marker, Stamp(LiveAudioSource.CheckPeriod)));

        await Until(() => Received(Channel.Me) is [.., var last] && Math.Abs(last - Marker) < SampleTolerance);
        Received(Channel.Me).Length.ShouldBe(Clock());
    }

    [Fact]
    public async Task Read_WhenRolledBackPacketIsContinued_KeepsOnlyWhatChannelLacks()
    {
        await Start();

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        // Канал дошёл до 500 мс. Отметка откатилась: 350–450 мс закрыт целиком, следом 450–550 — у канала нет
        // только 500–550, и продолжение не должно сравниваться с концом срезанного пакета.
        _time.Advance(LiveAudioSource.CheckPeriod / 2);
        _me.Push(Packet(Voice, Stamp(LiveAudioSource.CheckPeriod * 2)));
        _me.Push(Packet(Marker, Stamp(LiveAudioSource.CheckPeriod)));

        await Until(() => Received(Channel.Me) is [.., var last] && Math.Abs(last - Marker) < SampleTolerance);
        float[] me = Received(Channel.Me);
        me.Length.ShouldBe(Clock());
        me[(5 * PacketSamples)..].Length.ShouldBe(PacketSamples / 2);
        me[(5 * PacketSamples)..].ShouldAllBe(sample => Math.Abs(sample - Marker) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenLatePacketIsPartlyCoveredBySilence_CutsOnlyCoveredPart()
    {
        await Start();

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        // Тишина по часам дошла до 800 мс, опоздавшие пакеты — 550–1350 мс: пакет 750–850 закрыт наполовину.
        Advance(8);
        int covered = Samples(LiveAudioSource.CheckPeriod * 8) - AllowanceSamples;
        await Until(() => Received(Channel.Me).Length == 5 * PacketSamples + covered);
        _time.Advance(LiveAudioSource.CheckPeriod / 2);
        await Late(_me, 8);

        float[] me = Received(Channel.Me);
        me.Length.ShouldBe(Clock());
        me.Count(sample => sample == 0).ShouldBe(covered);
    }

    [Fact]
    public async Task Read_WhenDeviceStampIsFarFromArrival_PutsPacketsByArrival()
    {
        await Start();

        // Часы устройства сбились без пометки: отметки на час назад. По ним каждый пакет срезался бы целиком.
        for (int packet = 1; packet <= 20; packet++)
        {
            _time.Advance(LiveAudioSource.CheckPeriod);
            _me.Push(Packet(Voice, Stamp(TimeSpan.FromHours(1))));
            int expected = packet * PacketSamples;
            await Until(() => Received(Channel.Me).Length == expected);
        }

        Received(Channel.Me).ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenPacketHasNoDeviceTime_EndsItOnArrival()
    {
        await Start();
        Advance(7);
        _time.Advance(LiveAudioSource.CheckPeriod / 2);

        _others.Push(Packet(Voice, capturedAt: null));

        await Until(() => Received(Channel.Others).Length >= Clock());
        float[] others = Received(Channel.Others);
        others.Length.ShouldBe(Clock());
        others[..^PacketSamples].ShouldAllBe(sample => sample == 0);
        others[^PacketSamples..].ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenDeliveryDelayedLessThanAllowance_KeepsStreamWholeAndOnClock()
    {
        await Start();

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        // Поток захвата не получил CPU 300 мс: три пакета, записанные за это время, приходят разом после проверок.
        Advance(3);
        await Until(() => Received(Channel.Others).Length == Clock() - AllowanceSamples);
        await Late(_me, 3);

        for (int packet = 0; packet < 20; packet++)
        {
            await Step(_me);
        }

        float[] me = Received(Channel.Me);
        me.Length.ShouldBe(Clock());
        me.ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenDeliveryDelayedMoreThanAllowance_StaysOnClockWithSilenceOnlyForExcess()
    {
        await Start();

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        // Задержка 800 мс при запасе 500: тишина по часам закрывает 300 мс, опоздавшие пакеты за них срезаются.
        Advance(8);
        int covered = Samples(LiveAudioSource.CheckPeriod * 8) - AllowanceSamples;
        await Until(() => Received(Channel.Me).Length == 5 * PacketSamples + covered);
        await Late(_me, 8);

        for (int packet = 0; packet < 5; packet++)
        {
            await Step(_me);
        }

        float[] me = Received(Channel.Me);
        me.Length.ShouldBe(Clock());
        me.Count(sample => sample == 0).ShouldBe(covered);
        me[(5 * PacketSamples)..(5 * PacketSamples + covered)].ShouldAllBe(sample => sample == 0);
    }

    [Fact]
    public async Task Read_WhenDeviceLost_ThrowsAudioSourceException()
    {
        await Start();

        _others.Fail(new AudioSourceException("Устройство отключено"));

        await Should.ThrowAsync<AudioSourceException>(_collecting!.WaitAsync(Limit, Token));
    }

    [Fact]
    public async Task Read_WhenDeviceStopsWithoutCancellation_ThrowsAudioSourceException()
    {
        await Start();

        _me.End();

        await Should.ThrowAsync<AudioSourceException>(_collecting!.WaitAsync(Limit, Token));
    }

    [Fact]
    public async Task Read_WhenCancelled_StopsBothDevices()
    {
        await Start();

        await _reading.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(_collecting!.WaitAsync(Limit, Token));
        await Task.WhenAll(_me.Stopped, _others.Stopped).WaitAsync(Limit, Token);
    }

    [Fact]
    public void Open_WhenDeviceFormatUnsupported_FailsWithCode()
    {
        var pcm24 = new FakeDevice(Channel.Others, new WaveFormat(48_000, 24, 2));

        Result<LiveAudioSource> source = LiveAudioSource.Open([_me, pcm24], _time);

        source.Error.Code.ShouldBe(AudioErrors.UnsupportedDeviceFormatCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _reading.CancelAsync();

        if (_collecting is not null)
        {
            await Task.WhenAny(_collecting, Task.Delay(Limit, CancellationToken.None));
        }

        _reading.Dispose();
    }

    private static int Samples(TimeSpan duration) => (int)(duration.Ticks * SpeechAudio.SampleRate / TimeSpan.TicksPerSecond);

    private static DevicePacket Packet(float value, long? capturedAt)
    {
        float[] samples = [.. Enumerable.Repeat(value, PacketSamples)];
        return new DevicePacket(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), capturedAt);
    }

    private int Clock() => Samples(_time.GetUtcNow() - _time.Start);

    /// <summary>
    /// Отметка часов встречи, отстоящая от текущей на <paramref name="ago"/>.
    /// </summary>
    private long Stamp(TimeSpan ago) => _time.GetTimestamp() - (ago.Ticks * _time.TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>
    /// Проверки тишины без пакетов: часы вперёд на <paramref name="ticks"/> периодов проверки.
    /// </summary>
    private void Advance(int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            _time.Advance(LiveAudioSource.CheckPeriod);
        }
    }

    /// <summary>
    /// Пакеты, записанные устройством за последние <paramref name="count"/> периодов, приходят разом;
    /// ожидание — пока канал не догонит часы, чтобы следующая проверка тишины не обогнала их в очереди.
    /// </summary>
    private async Task Late(FakeDevice device, int count)
    {
        for (int packet = count; packet >= 1; packet--)
        {
            device.Push(Packet(Voice, Stamp(LiveAudioSource.CheckPeriod * packet)));
        }

        await Until(() => Received(device.Channel).Length == Clock());
    }

    private async Task Start()
    {
        LiveAudioSource source = LiveAudioSource.Open([_me, _others], _time).Value;
        _collecting = Collect(source);

        // Часы двигаются только после того, как источник завёл проверку тишины и открыл устройства.
        await Task.WhenAll(_me.Started, _others.Started).WaitAsync(Limit, Token);
    }

    private async Task Collect(LiveAudioSource source)
    {
        await foreach (AudioChunk chunk in source.Read(_reading.Token))
        {
            lock (_lock)
            {
                _received[chunk.Channel].AddRange(chunk.Samples.ToArray());
            }
        }
    }

    /// <summary>
    /// 100 мс встречи: часы вперёд, пакет устройства, ожидание, пока канал не догонит часы.
    /// </summary>
    private async Task Step(FakeDevice device)
    {
        _time.Advance(LiveAudioSource.CheckPeriod);
        int before = Received(device.Channel).Length;
        device.Push(Packet(Voice, Stamp(LiveAudioSource.CheckPeriod)));
        await Until(() => Received(device.Channel).Length >= Math.Max(Clock(), before + PacketSamples));
    }

    private float[] Received(Channel channel)
    {
        lock (_lock)
        {
            return [.. _received[channel]];
        }
    }

    private async Task Until(Func<bool> condition)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
        limit.CancelAfter(Limit);

        while (!condition())
        {
            if (_collecting is { IsFaulted: true })
            {
                await _collecting;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), limit.Token);
        }
    }

    /// <summary>
    /// Устройство, которому тест сам подаёт пакеты, сбой или конец.
    /// </summary>
    private sealed class FakeDevice(Channel channel, WaveFormat format) : IDeviceStream
    {
        private static readonly object EndMarker = new();

        private readonly Channel<object> _items = System.Threading.Channels.Channel.CreateUnbounded<object>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Channel Channel => channel;

        public string DeviceName => $"Тестовое {channel}";

        public WaveFormat Format => format;

        public Task Started => _started.Task;

        public Task Stopped => _stopped.Task;

        public void Push(DevicePacket packet) => _items.Writer.TryWrite(packet);

        public void Fail(Exception exception) => _items.Writer.TryWrite(exception);

        public void End() => _items.Writer.TryWrite(EndMarker);

        public async IAsyncEnumerable<DevicePacket> Read([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _started.TrySetResult();

            try
            {
                while (true)
                {
                    object item = await _items.Reader.ReadAsync(cancellationToken);

                    if (item is Exception exception)
                    {
                        throw exception;
                    }

                    if (ReferenceEquals(item, EndMarker))
                    {
                        yield break;
                    }

                    yield return (DevicePacket)item;
                }
            }
            finally
            {
                _stopped.TrySetResult();
            }
        }
    }
}
