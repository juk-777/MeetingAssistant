using System.Runtime.CompilerServices;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Пишет звук источника в WAV встречи: стерео, 16 кГц, PCM 16 бит; левый канал — «Я», правый — «Собеседники».
/// </summary>
/// <remarks>
/// В файл идёт ровно то, что источник отдал конвейеру, поэтому <c>meeting replay</c> этого файла повторяет встречу.
/// Каналы приходят кусками порознь: кадр пишется, когда есть звук обоих, а в конце короткий канал дополняется
/// тишиной. Заголовок обновляется каждую секунду звука: обрыв процесса оставляет файл, читаемый до этого места.
/// </remarks>
public sealed class WavRecordingAudioSource(IAudioSource source, string path) : IAudioSource
{
    /// <summary>
    /// Сколько кадров пишется между обновлениями заголовка.
    /// </summary>
    public const int FramesPerHeaderUpdate = SpeechAudio.SampleRate;

    private const int Stereo = 2;

    public async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var writer = new WaveFileWriter(path, new WaveFormat(SpeechAudio.SampleRate, 16, Stereo));
        var me = new List<float>();
        var others = new List<float>();
        long framesSinceUpdate = 0;

        try
        {
            await foreach (AudioChunk chunk in source.Read(cancellationToken))
            {
                (chunk.Channel == Channel.Me ? me : others).AddRange(chunk.Samples.Span);
                framesSinceUpdate += WritePaired(writer, me, others);

                if (framesSinceUpdate >= FramesPerHeaderUpdate)
                {
                    writer.Flush();
                    framesSinceUpdate = 0;
                }

                yield return chunk;
            }
        }
        finally
        {
            // Источник кончился, отменён или упал: записанное дописывается, короткий канал — тишиной.
            // Файл закрывается и тогда, когда не удалось дописать хвост: до него WAV остаётся читаемым.
            try
            {
                int frames = Math.Max(me.Count, others.Count);
                me.AddRange(new float[frames - me.Count]);
                others.AddRange(new float[frames - others.Count]);
                WritePaired(writer, me, others);
            }
            finally
            {
                await writer.DisposeAsync();
            }
        }
    }

    private static int WritePaired(WaveFileWriter writer, List<float> me, List<float> others)
    {
        int frames = Math.Min(me.Count, others.Count);

        if (frames == 0)
        {
            return 0;
        }

        var interleaved = new float[frames * Stereo];

        for (int frame = 0; frame < frames; frame++)
        {
            // Без ограничения значение за пределом ±1 переполнило бы 16 бит и перевернуло знак.
            interleaved[frame * Stereo] = Math.Clamp(me[frame], -1f, 1f);
            interleaved[(frame * Stereo) + 1] = Math.Clamp(others[frame], -1f, 1f);
        }

        writer.WriteSamples(interleaved, 0, interleaved.Length);
        me.RemoveRange(0, frames);
        others.RemoveRange(0, frames);
        return frames;
    }
}
