using MeetingAssistant.Application.Meetings.Recording;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// Детекторы Silero VAD с параметрами, на которых сняты замеры (<c>docs/measurements.md</c>).
/// </summary>
/// <remarks>
/// Пороги — константы, а не настройки: их меняет новый замер, а не машина.
/// </remarks>
internal sealed class SileroVoiceActivityDetectorFactory(IOptions<SpeechOptions> speechOptions) : IVoiceActivityDetectorFactory
{
    public const float Threshold = 0.5f;
    public const float MinSilenceSeconds = 0.5f;
    public const float MinSpeechSeconds = 0.25f;

    // Порог, а не предел: дольше него детектор режет фразу на первой короткой паузе (docs/decisions.md).
    // Задаётся явно: умолчание sherpa-onnx — 5 с, и длинная реплика резалась бы на куски.
    public const float MaxSpeechSeconds = 20f;

    // Буфер детектора: с запасом больше самой длинной фразы.
    private const float BufferSeconds = 120f;

    public IVoiceActivityDetector Create()
    {
        var config = new VadModelConfig { SampleRate = SpeechAudio.SampleRate };
        config.SileroVad.Model = SpeechModels.Vad(speechOptions.Value.ModelsDirectory);
        config.SileroVad.Threshold = Threshold;
        config.SileroVad.MinSilenceDuration = MinSilenceSeconds;
        config.SileroVad.MinSpeechDuration = MinSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = MaxSpeechSeconds;

        return new SileroVoiceActivityDetector(
            new VoiceActivityDetector(config, BufferSeconds),
            config.SileroVad.WindowSize,
            (int)(MinSpeechSeconds * SpeechAudio.SampleRate));
    }
}

/// <summary>
/// Детектор одного канала: копит звук и отдаёт фразы, после которых наступила пауза.
/// </summary>
/// <remarks>
/// Нативный детектор получает звук ровно окнами Silero, остаток ждёт следующего куска: от размера кусков
/// sherpa-onnx зависят границы фраз, а звук одним большим массивом он почти целиком теряет.
/// </remarks>
internal sealed class SileroVoiceActivityDetector(VoiceActivityDetector detector, int windowSize, int minSpeechSamples)
    : IVoiceActivityDetector
{
    private readonly List<float> _pending = [];
    private long _processed;

    public TimeSpan? SpeakingSince { get; private set; }

    public IReadOnlyList<DetectedPhrase> Accept(ReadOnlyMemory<float> samples)
    {
        _pending.AddRange(samples.Span);

        int windows = _pending.Count / windowSize;

        for (int window = 0; window < windows; window++)
        {
            detector.AcceptWaveform(_pending.GetRange(window * windowSize, windowSize).ToArray());
            _processed += windowSize;
            TrackSpeech();
        }

        _pending.RemoveRange(0, windows * windowSize);

        return TakeReady();
    }

    // Неполное окно в конце записи sherpa-onnx не обрабатывает и при дочистке: теряется меньше одного окна (32 мс).
    public IReadOnlyList<DetectedPhrase> Flush()
    {
        detector.Flush();
        SpeakingSince = null;
        return TakeReady();
    }

    public void Dispose() => detector.Dispose();

    // Детектор признаёт речь, когда она длится минимальную длительность, — начало речи раньше на неё и окно.
    private void TrackSpeech()
    {
        if (!detector.IsSpeechDetected())
        {
            SpeakingSince = null;
            return;
        }

        SpeakingSince ??= SpeechAudio.Duration(Math.Max(0, _processed - windowSize - minSpeechSamples));
    }

    private List<DetectedPhrase> TakeReady()
    {
        var ready = new List<DetectedPhrase>();

        while (!detector.IsEmpty())
        {
            SpeechSegment segment = detector.Front();
            ready.Add(new DetectedPhrase(SpeechAudio.Duration(segment.Start), segment.Samples));
            detector.Pop();
        }

        return ready;
    }
}
