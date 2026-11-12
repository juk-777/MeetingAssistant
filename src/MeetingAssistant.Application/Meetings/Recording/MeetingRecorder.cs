using System.Threading.Channels;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings.Recognize;
using MeetingAssistant.Application.Meetings.Stop;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Application.Meetings.Recording;

/// <summary>
/// Конвейер записи: звук каналов → детектор речи канала → одна очередь фраз → распознавание → остановка.
/// </summary>
/// <remarks>
/// Очередь читает один читатель, и остановка идёт им же после последней фразы: агрегат встречи не потокобезопасен.
/// Очередь без ограничения размера: переполненная очередь остановила бы подачу, а живой захват ждать не будет.
/// </remarks>
internal sealed class MeetingRecorder(
    ICommandHandler<RecognizeSpeech> recognizeSpeech,
    ICommandHandler<StopMeeting> stopMeeting,
    IVoiceActivityDetectorFactory detectorFactory) : IMeetingRecorder
{
    public async Task<Result> Record(IAudioSource source, CancellationToken cancellationToken)
    {
        Channel<RecognizeSpeech> phrases = System.Threading.Channels.Channel.CreateUnbounded<RecognizeSpeech>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        using var feeding = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Читатель ждёт фраз асинхронно, и продолжения канала идут в пуле: подача распознавание не ждёт.
        Task<Result> recognition = RecognizeAll(phrases.Reader, feeding);

        Result recognized;
        AudioSourceException? sourceLost;

        try
        {
            sourceLost = await Feed(source, phrases.Writer, feeding.Token);
        }
        finally
        {
            recognized = await recognition;
        }

        if (recognized.IsFailure)
        {
            return recognized;
        }

        // Без токена отмены: после Ctrl+C встреча всё равно получает конец записи.
        Result stopped = await stopMeeting.Handle(new StopMeeting(), CancellationToken.None);

        return stopped.IsSuccess && sourceLost is not null
            ? Result.Failure(MeetingScenarioErrors.AudioSourceFailed(sourceLost.Message))
            : stopped;
    }

    /// <returns>Потеря устройства, если подача кончилась ею.</returns>
    private async Task<AudioSourceException?> Feed(
        IAudioSource source,
        ChannelWriter<RecognizeSpeech> phrases,
        CancellationToken cancellationToken)
    {
        var detectors = new Dictionary<Channel, IVoiceActivityDetector>();

        try
        {
            await foreach (AudioChunk chunk in source.Read(cancellationToken))
            {
                if (!detectors.TryGetValue(chunk.Channel, out IVoiceActivityDetector? detector))
                {
                    detector = detectorFactory.Create();
                    detectors.Add(chunk.Channel, detector);
                }

                Enqueue(phrases, chunk.Channel, detector.Accept(chunk.Samples));
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена — штатный конец подачи: уже вырезанные фразы распознаются, встреча останавливается.
            return null;
        }
        catch (AudioSourceException exception)
        {
            // Записанное цело: фразы распознаются, встреча останавливается с концом записи, наружу — отказ.
            return exception;
        }
        finally
        {
            try
            {
                foreach ((Channel channel, IVoiceActivityDetector detector) in detectors)
                {
                    Enqueue(phrases, channel, detector.Flush());
                }
            }
            finally
            {
                // Очередь закрывается и при сбое дочистки: иначе читатель ждал бы фраз вечно.
                phrases.Complete();

                foreach (IVoiceActivityDetector detector in detectors.Values)
                {
                    detector.Dispose();
                }
            }
        }
    }

    private async Task<Result> RecognizeAll(ChannelReader<RecognizeSpeech> phrases, CancellationTokenSource feeding)
    {
        try
        {
            // Без токена отмены: фразы, вырезанные до Ctrl+C, распознаются до конца.
            await foreach (RecognizeSpeech phrase in phrases.ReadAllAsync(CancellationToken.None))
            {
                Result result = await recognizeSpeech.Handle(phrase, CancellationToken.None);

                if (result.IsFailure)
                {
                    await feeding.CancelAsync();
                    return result;
                }
            }

            return Result.Success();
        }
        catch
        {
            await feeding.CancelAsync();
            throw;
        }
    }

    private static void Enqueue(ChannelWriter<RecognizeSpeech> phrases, Channel channel, IReadOnlyList<DetectedPhrase> detected)
    {
        foreach (DetectedPhrase phrase in detected)
        {
            var span = new SpeechSpan(phrase.Start, SpeechAudio.Duration(phrase.Samples.Length));
            _ = phrases.TryWrite(new RecognizeSpeech(channel, span, phrase.Samples));
        }
    }
}
