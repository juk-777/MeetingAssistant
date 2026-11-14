using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class PulseDeviceStreamTests
{
    // Предел ожидания: поток, который не отдаёт пакеты, должен ронять тест, а не вешать прогон.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static readonly int PacketBytes = PulseDeviceStream.DeviceFormat.AverageBytesPerSecond / 10;

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_StampsFirstFrameByArrivalLessLatencyAndPacket()
    {
        var pulse = new FakePulse(_time) { ReportedLatency = TimeSpan.FromMilliseconds(20) };
        long started = _time.GetTimestamp();

        List<DevicePacket> packets = await Take(Open(pulse), 2);

        // Чтение «длится» 100 мс: пакеты приходят на 100 и 200 мс, их первый кадр записан на 120 мс раньше.
        packets.Select(packet => packet.CapturedAt).ShouldBe([Shifted(started, -20), Shifted(started, 80)]);
        packets.ShouldAllBe(packet => packet.Data.Length == PacketBytes);
        packets[1].Data.ToArray().ShouldAllBe(sample => sample == 2);
    }

    [Fact]
    public async Task Read_WhenServerDoesNotKnowLatency_LeavesStampToArrival()
    {
        var pulse = new FakePulse(_time) { ReportedLatency = null };

        List<DevicePacket> packets = await Take(Open(pulse), 1);

        packets.Single().CapturedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Read_WhenServerFailsReading_ThrowsAudioSourceExceptionWithServerReasonAndFreesStream()
    {
        var pulse = new FakePulse(_time) { FailingRead = 3 };
        PulseDeviceStream stream = Open(pulse);
        var packets = new List<DevicePacket>();

        AudioSourceException failure = await Should.ThrowAsync<AudioSourceException>(async () =>
        {
            await foreach (DevicePacket packet in stream.Read(Token))
            {
                packets.Add(packet);
            }
        }).WaitAsync(Limit, Token);

        packets.Count.ShouldBe(2);
        failure.Message.ShouldContain(FakePulse.ReadFailure);
        pulse.Freed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Read_WhenStopped_FreesStreamOnceAfterReadInProgressReturns()
    {
        // Третье чтение идёт, когда чтение останавливают: соединение освобождается только после его конца.
        var pulse = new FakePulse(_time) { HoldingRead = 3 };
        PulseDeviceStream stream = Open(pulse);

        _ = await Take(stream, 2);
        stream.Dispose();

        pulse.Freed.Count.ShouldBe(1);
        pulse.ReadsAfterFree.ShouldBe(0);
    }

    private PulseDeviceStream Open(FakePulse pulse)
    {
        Result<PulseDeviceStream> stream = PulseDeviceStream.Open(Channel.Me, pulse, _time);
        stream.IsSuccess.ShouldBeTrue();
        return stream.Value;
    }

    private static Task<List<DevicePacket>> Take(PulseDeviceStream stream, int count) =>
        Collect(stream, count).WaitAsync(Limit, Token);

    private static async Task<List<DevicePacket>> Collect(PulseDeviceStream stream, int count)
    {
        var packets = new List<DevicePacket>();

        await foreach (DevicePacket packet in stream.Read(Token).ConfigureAwait(false))
        {
            packets.Add(packet);

            if (packets.Count == count)
            {
                break;
            }
        }

        return packets;
    }

    private long Shifted(long timestamp, int milliseconds) =>
        timestamp + (milliseconds * _time.TimestampFrequency / 1000);
}
