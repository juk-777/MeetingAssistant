using System.Runtime.CompilerServices;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Application.UnitTests.Meetings;

/// <summary>
/// Детекторы, у которых кусок звука становится фразой, когда приходит следующий кусок канала или дочистка.
/// </summary>
/// <remarks>
/// Последний кусок канала доходит до распознавания только через <see cref="IVoiceActivityDetector.Flush"/>.
/// </remarks>
internal sealed class HoldingDetectors : IVoiceActivityDetectorFactory
{
    private readonly List<HoldingDetector> _created = [];

    public IReadOnlyList<HoldingDetector> Created => _created;

    /// <summary>
    /// Дочистка бросает — как сбой нативного детектора.
    /// </summary>
    public bool FailFlush { get; set; }

    public IVoiceActivityDetector Create()
    {
        var detector = new HoldingDetector { FailFlush = FailFlush };
        _created.Add(detector);
        return detector;
    }
}

internal sealed class HoldingDetector : IVoiceActivityDetector
{
    /// <summary>
    /// Кусок, заполненный этим значением, — пауза: фразой не становится и закрывает удержанную.
    /// </summary>
    public const float PauseSample = float.NegativeInfinity;

    private long _position;
    private DetectedPhrase? _held;

    public bool FailFlush { get; init; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<DetectedPhrase> Accept(ReadOnlyMemory<float> samples)
    {
        DetectedPhrase? previous = _held;
        bool pause = samples.Length > 0 && float.IsNegativeInfinity(samples.Span[0]);
        _held = pause ? null : new DetectedPhrase(SpeechAudio.Duration(_position), samples);
        _position += samples.Length;
        return previous is null ? [] : [previous];
    }

    public IReadOnlyList<DetectedPhrase> Flush()
    {
        if (FailFlush)
        {
            throw new InvalidOperationException("Детектор не дочистился");
        }

        DetectedPhrase? held = _held;
        _held = null;
        return held is null ? [] : [held];
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Источник с заданными кусками; бесконечный после них ждёт отмены, как живой захват; со сбоем — бросает его.
/// </summary>
internal sealed class ScriptedSource(IReadOnlyList<AudioChunk> chunks, bool endless = false, Exception? failure = null)
    : IAudioSource
{
    private readonly TaskCompletionSource _exhausted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Все куски отданы.
    /// </summary>
    public Task Exhausted => _exhausted.Task;

    public static AudioChunk Seconds(Channel channel, int seconds) =>
        new(channel, new float[SpeechAudio.SampleRate * seconds]);

    /// <summary>
    /// Пауза для <see cref="HoldingDetector"/>.
    /// </summary>
    public static AudioChunk Pause(Channel channel, int seconds) =>
        new(channel, Enumerable.Repeat(HoldingDetector.PauseSample, SpeechAudio.SampleRate * seconds).ToArray());

    public async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (AudioChunk chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }

        _exhausted.TrySetResult();

        if (failure is not null)
        {
            throw failure;
        }

        if (endless)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
