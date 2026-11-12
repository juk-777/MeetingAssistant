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

    // Звук проходит без перевода, поэтому допуск — только на сравнение float, а не на обработку.
    private const float SampleTolerance = 1e-6f;

    // Устройство сразу в 16 кГц моно: перевод проверен отдельно, здесь отсчёты считаются точно.
    private static readonly WaveFormat SpeechFormat = WaveFormat.CreateIeeeFloatWaveFormat(SpeechAudio.SampleRate, 1);

    private static readonly int PacketSamples = Samples(LiveAudioSource.CheckPeriod);

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
    public async Task Read_WhenChannelGetsNoPackets_GivesSilenceUpToClockAndLeavesStreamUntouched()
    {
        await Start();

        for (int packet = 1; packet <= 20; packet++)
        {
            await Step(_me);
        }

        await Until(() => Received(Channel.Others).Length == Clock());
        Received(Channel.Others).ShouldAllBe(sample => sample == 0);
        Received(Channel.Me).ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
    }

    [Fact]
    public async Task Read_WhenDeviceClockIsSlow_DoesNotInsertSilenceIntoContinuousStream()
    {
        await Start();
        TimeSpan slowPeriod = LiveAudioSource.CheckPeriod * 1.1;

        for (int packet = 1; packet <= 30; packet++)
        {
            _time.Advance(slowPeriod);
            _me.Push(Packet(Voice));
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
                Received(Channel.Others).Length.ShouldBeInRange(Clock(), Clock() + PacketSamples);
            }

            for (int tick = 0; tick < 7; tick++)
            {
                _time.Advance(LiveAudioSource.CheckPeriod);
            }

            await Until(() => Received(Channel.Others).Length == Clock());
        }
    }

    [Fact]
    public async Task Read_WhenSoundReturnsBeforeCheckFillsSilence_EndsPacketExactlyOnClock()
    {
        await Start();

        // Проверки на 100 и 200 мс канал ещё не считают молчащим; к 250 мс он молчит дольше допуска,
        // а следующая проверка — только на 300 мс: тишину до пакета ставит сам вернувшийся пакет.
        _time.Advance(LiveAudioSource.CheckPeriod);
        _time.Advance(LiveAudioSource.CheckPeriod);
        _time.Advance(LiveAudioSource.CheckPeriod / 2);
        _others.Push(Packet(Voice));

        await Until(() => Received(Channel.Others).Length >= Clock());
        float[] others = Received(Channel.Others);
        others.Length.ShouldBe(Clock());
        others[..^PacketSamples].ShouldAllBe(sample => sample == 0);
        others[^PacketSamples..].ShouldAllBe(sample => Math.Abs(sample - Voice) < SampleTolerance);
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

    private static DevicePacket Packet(float value)
    {
        float[] samples = [.. Enumerable.Repeat(value, PacketSamples)];
        return new DevicePacket(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
    }

    private int Clock() => Samples(_time.GetUtcNow() - _time.Start);

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
        device.Push(Packet(Voice));
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
