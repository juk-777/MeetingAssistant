using System.Threading.Channels;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings.Observe;
using MeetingAssistant.Application.Meetings.Recognize;
using MeetingAssistant.Application.Meetings.Stop;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;
using Channel = MeetingAssistant.Domain.Meetings.Channel;

namespace MeetingAssistant.Application.Meetings.Recording;

/// <summary>
/// Конвейер записи: звук каналов → детектор речи канала → одна очередь фраз и речи каналов → распознавание и
/// сверка речи → остановка.
/// </summary>
/// <remarks>
/// Очередь читает один читатель, и остановка идёт им же после последней фразы: агрегат встречи не потокобезопасен.
/// Речь каналов встаёт в очередь после каждого куска, следом за фразами, которые кусок закрыл: конец ответа
/// сверяется с уже записанными фразами. Очередь без ограничения размера: переполненная очередь остановила бы подачу,
/// а живой захват ждать не будет.
/// </remarks>
internal sealed class MeetingRecorder(
    ICommandHandler<RecognizeSpeech> recognizeSpeech,
    ICommandHandler<ObserveSpeech> observeSpeech,
    ICommandHandler<StopMeeting> stopMeeting,
    IVoiceActivityDetectorFactory detectorFactory) : IMeetingRecorder
{
    public async Task<Result> Record(IAudioSource source, CancellationToken cancellationToken)
    {
        Channel<ICommand> phrases = System.Threading.Channels.Channel.CreateUnbounded<ICommand>(
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
        ChannelWriter<ICommand> phrases,
        CancellationToken cancellationToken)
    {
        var detectors = new Dictionary<Channel, IVoiceActivityDetector>();
        var positions = new Dictionary<Channel, long>();

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
                positions[chunk.Channel] = positions.GetValueOrDefault(chunk.Channel) + chunk.Samples.Length;
                _ = phrases.TryWrite(new ObserveSpeech(
                    Activity(Channel.Me, detectors, positions),
                    Activity(Channel.Others, detectors, positions)));
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

    private async Task<Result> RecognizeAll(ChannelReader<ICommand> phrases, CancellationTokenSource feeding)
    {
        try
        {
            // Без токена отмены: фразы, вырезанные до Ctrl+C, распознаются до конца.
            await foreach (ICommand step in phrases.ReadAllAsync(CancellationToken.None))
            {
                Result result = step switch
                {
                    RecognizeSpeech phrase => await recognizeSpeech.Handle(phrase, CancellationToken.None),
                    ObserveSpeech activity => await observeSpeech.Handle(activity, CancellationToken.None),
                    _ => throw new InvalidOperationException($"Неизвестный шаг очереди записи: {step.GetType().Name}."),
                };

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

    // Канал без звука — позиция 0: конец ответа не наступит, пока звук не пошёл в оба канала.
    private static SpeechActivity Activity(
        Channel channel,
        Dictionary<Channel, IVoiceActivityDetector> detectors,
        Dictionary<Channel, long> positions)
    {
        TimeSpan position = SpeechAudio.Duration(positions.GetValueOrDefault(channel));
        TimeSpan? speakingSince = detectors.GetValueOrDefault(channel)?.SpeakingSince;

        return new SpeechActivity(position, speakingSince > position ? position : speakingSince);
    }

    private static void Enqueue(ChannelWriter<ICommand> phrases, Channel channel, IReadOnlyList<DetectedPhrase> detected)
    {
        foreach (DetectedPhrase phrase in detected)
        {
            var span = new SpeechSpan(phrase.Start, SpeechAudio.Duration(phrase.Samples.Length));
            _ = phrases.TryWrite(new RecognizeSpeech(channel, span, phrase.Samples));
        }
    }
}
