using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using NAudio.Wave;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class DeviceAudioConverterTests
{
    private const double Tone = 1_000;
    private const float Amplitude = 0.5f;

    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);

    // Ресемплер держит хвост не длиннее пакета: длительность сверяется с точностью до пакета 10 мс.
    private static readonly int DurationTolerance = SpeechAudio.SampleRate / 100;

    private static readonly double ExpectedRms = Amplitude / Math.Sqrt(2);

    [Fact]
    public void Convert_When48kStereoFloatIn10msPackets_Gives16kMonoOfSameDurationToneAndLevel()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        float[] converted = ConvertAll(format, DevicePackets.Sine(format, Tone, Amplitude));

        AssertTone(converted);
    }

    [Fact]
    public void Convert_When44kMonoPcm16_Gives16kMonoOfSameDurationToneAndLevel()
    {
        var format = new WaveFormat(44_100, 16, 1);

        float[] converted = ConvertAll(format, DevicePackets.Sine(format, Tone, Amplitude));

        AssertTone(converted);
    }

    [Fact]
    public void Convert_WhenMixFormatIsExtensibleFloat_ConvertsLikePlainFloat()
    {
        // Так WASAPI отдаёт формат микшера в общем режиме.
        var format = new WaveFormatExtensible(48_000, 32, 2);

        float[] converted = ConvertAll(format, DevicePackets.Sine(format, Tone, Amplitude));

        AssertTone(converted);
    }

    [Fact]
    public void Convert_WhenChannelsInOpposition_AveragesThemToSilence()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        Func<int, long, float> sine = DevicePackets.Sine(format, Tone, Amplitude);

        float[] converted = ConvertAll(format, (channel, frame) => channel == 0 ? sine(channel, frame) : -sine(channel, frame));

        converted.Max(Math.Abs).ShouldBeLessThan(1e-4f);
    }

    [Fact]
    public void Convert_WhenPcm16StereoHasOneSilentChannel_GivesHalfLevel()
    {
        var format = new WaveFormat(48_000, 16, 2);
        Func<int, long, float> sine = DevicePackets.Sine(format, Tone, Amplitude);

        float[] converted = ConvertAll(format, (channel, frame) => channel == 1 ? sine(channel, frame) : 0);

        Signal.Rms(Middle(converted)).ShouldBe(ExpectedRms / 2, ExpectedRms * 0.05);
    }

    [Fact]
    public void Convert_WhenDeviceAlready16kMonoFloat_PassesSamplesUnchanged()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(SpeechAudio.SampleRate, 1);
        List<byte[]> packets = DevicePackets.Of(format, TimeSpan.FromMilliseconds(30), (_, frame) => frame / 1_000f);
        DeviceAudioConverter converter = DeviceAudioConverter.Create(format).Value;

        float[] converted = [.. packets.SelectMany(packet => converter.Convert(packet))];

        converted.ShouldBe([.. Enumerable.Range(0, 480).Select(frame => frame / 1_000f)]);
    }

    [Fact]
    public void Create_WhenPcm24_FailsWithUnsupportedDeviceFormat()
    {
        Result<DeviceAudioConverter> converter = DeviceAudioConverter.Create(new WaveFormat(48_000, 24, 2));

        converter.Error.Code.ShouldBe(AudioErrors.UnsupportedDeviceFormatCode);
    }

    private static float[] ConvertAll(WaveFormat format, Func<int, long, float> sample)
    {
        DeviceAudioConverter converter = DeviceAudioConverter.Create(format).Value;
        return [.. DevicePackets.Of(format, Duration, sample).SelectMany(packet => converter.Convert(packet))];
    }

    private static void AssertTone(float[] converted)
    {
        int expectedLength = (int)(Duration.TotalSeconds * SpeechAudio.SampleRate);

        converted.Length.ShouldBeInRange(expectedLength - DurationTolerance, expectedLength + DurationTolerance);
        Signal.Frequency(Middle(converted), SpeechAudio.SampleRate).ShouldBe(Tone, Tone * 0.01);
        Signal.Rms(Middle(converted)).ShouldBe(ExpectedRms, ExpectedRms * 0.05);
    }

    // Без краёв: на входе ресемплер разгоняет фильтр, на выходе держит хвост.
    private static float[] Middle(float[] samples) => samples[(SpeechAudio.SampleRate / 10)..^(SpeechAudio.SampleRate / 10)];
}
