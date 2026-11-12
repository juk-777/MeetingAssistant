using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

/// <summary>
/// Пакеты устройства, как их отдаёт WASAPI: чередующиеся каналы в формате устройства.
/// </summary>
internal static class DevicePackets
{
    /// <summary>
    /// Пакеты по 10 мс: значение отсчёта задаёт функция от канала и номера кадра.
    /// </summary>
    public static List<byte[]> Of(WaveFormat format, TimeSpan duration, Func<int, long, float> sample)
    {
        int packetFrames = format.SampleRate / 100;
        long totalFrames = (long)(duration.TotalSeconds * format.SampleRate);
        var packets = new List<byte[]>();

        for (long first = 0; first < totalFrames; first += packetFrames)
        {
            int frames = (int)Math.Min(packetFrames, totalFrames - first);
            var interleaved = new float[frames * format.Channels];

            for (int frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    interleaved[(frame * format.Channels) + channel] = sample(channel, first + frame);
                }
            }

            packets.Add(Encode(format, interleaved));
        }

        return packets;
    }

    public static Func<int, long, float> Sine(WaveFormat format, double frequency, float amplitude) =>
        (_, frame) => amplitude * (float)Math.Sin(2 * Math.PI * frequency * frame / format.SampleRate);

    private static byte[] Encode(WaveFormat format, float[] interleaved)
    {
        if (format.BitsPerSample == 32)
        {
            return MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray();
        }

        short[] pcm = [.. interleaved.Select(value => (short)Math.Round(value * short.MaxValue))];
        return MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
    }
}

/// <summary>
/// Свойства сигнала для проверок перевода: частота по переходам через ноль, среднеквадратичное.
/// </summary>
internal static class Signal
{
    public static double Frequency(ReadOnlySpan<float> samples, int sampleRate)
    {
        int rising = 0;

        for (int index = 1; index < samples.Length; index++)
        {
            if (samples[index - 1] < 0 && samples[index] >= 0)
            {
                rising++;
            }
        }

        return rising * (double)sampleRate / samples.Length;
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;

        foreach (float sample in samples)
        {
            sum += sample * (double)sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
