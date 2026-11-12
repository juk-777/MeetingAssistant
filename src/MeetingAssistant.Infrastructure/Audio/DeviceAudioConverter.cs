using System.Runtime.InteropServices;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.SharedKernel;
using NAudio.Dsp;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Переводит пакеты устройства одного канала встречи в 16 кГц моно float.
/// </summary>
/// <remarks>
/// Экземпляр на канал: ресемплер держит хвост прошлого пакета, поэтому пакеты канала идут через него подряд.
/// </remarks>
internal sealed class DeviceAudioConverter
{
    private const int MonoChannels = 1;

    // Запас выхода сверх пропорции: ресемплер отдаёт накопленный хвост вместе с новым пакетом.
    private const int OutputMargin = 64;

    private const float Pcm16Scale = 32768f;

    private readonly WaveFormat _format;
    private readonly WdlResampler? _resampler;

    private DeviceAudioConverter(WaveFormat format)
    {
        _format = format;

        if (format.SampleRate == SpeechAudio.SampleRate)
        {
            return;
        }

        // Режим и фильтр — как у WdlResamplingSampleProvider из NAudio; подача — сколько пришло, а не сколько нужно.
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true);
        _resampler.SetRates(format.SampleRate, SpeechAudio.SampleRate);
    }

    /// <summary>
    /// Принимает float 32 и PCM 16 с любой частотой и числом каналов, в том числе в обёртке WAVE_FORMAT_EXTENSIBLE.
    /// </summary>
    /// <param name="format">Формат пакетов устройства.</param>
    public static Result<DeviceAudioConverter> Create(WaveFormat format)
    {
        WaveFormat standard = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;

        bool supported = standard.Channels > 0
            && standard.SampleRate > 0
            && ((standard.Encoding == WaveFormatEncoding.IeeeFloat && standard.BitsPerSample == 32)
                || (standard.Encoding == WaveFormatEncoding.Pcm && standard.BitsPerSample == 16));

        return supported
            ? Result.Success(new DeviceAudioConverter(standard))
            : Result.Failure<DeviceAudioConverter>(AudioErrors.UnsupportedDeviceFormat(format));
    }

    /// <summary>
    /// Звук пакета в 16 кГц моно; каналы устройства усредняются.
    /// </summary>
    /// <param name="packet">Целые кадры в формате устройства.</param>
    public float[] Convert(ReadOnlySpan<byte> packet)
    {
        float[] mono = Downmix(packet);

        if (_resampler is null)
        {
            return mono;
        }

        var output = new float[(int)((long)mono.Length * SpeechAudio.SampleRate / _format.SampleRate) + OutputMargin];
        int produced = 0;
        int consumed = 0;

        while (consumed < mono.Length)
        {
            int accepted = _resampler.ResamplePrepare(mono.Length - consumed, MonoChannels, out Span<float> input);
            int frames = Math.Min(accepted, mono.Length - consumed);
            mono.AsSpan(consumed, frames).CopyTo(input);
            produced += _resampler.ResampleOut(output.AsSpan(produced), frames, output.Length - produced, MonoChannels);
            consumed += frames;
        }

        return output[..produced];
    }

    /// <summary>
    /// Сколько звучит пакет на устройстве: по кадрам, а не по выходу ресемплера, который держит хвост у себя.
    /// </summary>
    /// <param name="packet">Целые кадры в формате устройства.</param>
    public TimeSpan Duration(ReadOnlySpan<byte> packet) =>
        TimeSpan.FromTicks(packet.Length / _format.BlockAlign * TimeSpan.TicksPerSecond / _format.SampleRate);

    private float[] Downmix(ReadOnlySpan<byte> packet)
    {
        int channels = _format.Channels;
        int frames = packet.Length / _format.BlockAlign;
        var mono = new float[frames];
        ReadOnlySpan<byte> whole = packet[..(frames * _format.BlockAlign)];

        if (_format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(whole);

            for (int frame = 0; frame < frames; frame++)
            {
                mono[frame] = Average(samples.Slice(frame * channels, channels));
            }
        }
        else
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(whole);

            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0;

                foreach (short sample in samples.Slice(frame * channels, channels))
                {
                    sum += sample / Pcm16Scale;
                }

                mono[frame] = sum / channels;
            }
        }

        return mono;
    }

    private static float Average(ReadOnlySpan<float> frame)
    {
        float sum = 0;

        foreach (float sample in frame)
        {
            sum += sample;
        }

        return sum / frame.Length;
    }
}
