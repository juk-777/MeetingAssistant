using System.Runtime.CompilerServices;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Запись с диска как источник встречи: стерео — левый канал «Я», правый «Собеседники»; моно — «Собеседники».
/// </summary>
/// <remarks>
/// Кусок отдаётся, когда он «прозвучал» по часам с учётом ускорения, — как его отдал бы живой захват.
/// </remarks>
public sealed class WavFileAudioSource : IAudioSource
{
    /// <summary>
    /// Кусок — 100 мс звука.
    /// </summary>
    public const int ChunkFrames = SpeechAudio.SampleRate / 10;

    private readonly string _path;
    private readonly int _channels;
    private readonly double _speed;
    private readonly TimeProvider _timeProvider;

    private WavFileAudioSource(string path, int channels, double speed, TimeProvider timeProvider)
    {
        _path = path;
        _channels = channels;
        _speed = speed;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Проверяет запись: 16 кГц, моно или стерео, PCM 16 бит или float 32.
    /// </summary>
    /// <param name="path">Путь к WAV.</param>
    /// <param name="speed">Во сколько раз быстрее реального темпа, больше нуля.</param>
    /// <param name="timeProvider">Часы подачи.</param>
    public static Result<IAudioSource> Open(string path, double speed, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);

        if (!File.Exists(path))
        {
            return Result.Failure<IAudioSource>(AudioErrors.FileNotFound(path));
        }

        WaveFormat format;

        try
        {
            using var reader = new WaveFileReader(path);
            format = reader.WaveFormat;
        }
        catch (Exception exception) when (exception is FormatException or EndOfStreamException)
        {
            // Не WAV вовсе: запись созвона в другом контейнере (mp3, m4a) — тот же отказ с командой перевода.
            return Result.Failure<IAudioSource>(AudioErrors.NotWave(path));
        }

        bool supported = format.SampleRate == SpeechAudio.SampleRate
            && format.Channels is 1 or 2
            && ((format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
                || (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32));

        return supported
            ? Result.Success<IAudioSource>(new WavFileAudioSource(path, format.Channels, speed, timeProvider))
            : Result.Failure<IAudioSource>(AudioErrors.UnsupportedFormat(path, format));
    }

    public async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(_path);
        ISampleProvider provider = reader.ToSampleProvider();
        var interleaved = new float[ChunkFrames * _channels];
        long started = _timeProvider.GetTimestamp();
        long framesRead = 0;
        int samplesRead;

        while ((samplesRead = provider.Read(interleaved)) > 0)
        {
            // Без паузы (ускоренная подача, отставание) отмену иначе заметил бы только конец файла.
            cancellationToken.ThrowIfCancellationRequested();

            int frames = samplesRead / _channels;
            framesRead += frames;

            TimeSpan wait = (SpeechAudio.Duration(framesRead) / _speed) - _timeProvider.GetElapsedTime(started);

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider, cancellationToken);
            }

            if (_channels == 1)
            {
                // Копия: буфер чтения переиспользуется, а кусок живёт в очереди фраз.
                yield return new AudioChunk(Channel.Others, interleaved.AsSpan(0, frames).ToArray());
                continue;
            }

            var me = new float[frames];
            var others = new float[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                me[frame] = interleaved[frame * 2];
                others[frame] = interleaved[(frame * 2) + 1];
            }

            yield return new AudioChunk(Channel.Me, me);
            yield return new AudioChunk(Channel.Others, others);
        }
    }
}

/// <summary>
/// Отказы источника звука с диска.
/// </summary>
public static class AudioErrors
{
    public const string FileNotFoundCode = "Audio.FileNotFound";
    public const string UnsupportedFormatCode = "Audio.UnsupportedFormat";

    public static Error FileNotFound(string path) => new(FileNotFoundCode, $"Нет файла записи: {path}.");

    public static Error UnsupportedFormat(string path, WaveFormat format) =>
        new(UnsupportedFormatCode, $"Запись {path} — {format}. {Conversion(path)}");

    public static Error NotWave(string path) =>
        new(UnsupportedFormatCode, $"Запись {path} — не WAV. {Conversion(path)}");

    private static string Conversion(string path) =>
        "Нужны WAV 16 кГц, моно или стерео, PCM 16 бит или float 32. Перевести с сохранением каналов:\n" +
        $"  ffmpeg -i \"{path}\" -ar {SpeechAudio.SampleRate} -sample_fmt s16 <выход>.wav\n" +
        "Больше двух каналов или нужен один — добавить -ac 1 (всё уйдёт в «Собеседники»).";
}
