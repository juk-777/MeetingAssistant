using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace MeetingAssistant.Infrastructure.Speech;

/// <summary>
/// GigaAM v3 RNNT с пунктуацией через sherpa-onnx: модель грузится один раз, фраза распознаётся целиком.
/// </summary>
/// <remarks>
/// Вызывается одним читателем очереди фраз, поэтому распознаватель не делится между потоками.
/// </remarks>
internal sealed class GigaAmSpeechRecognizer : ISpeechRecognizer, IDisposable
{
    private readonly OfflineRecognizer _recognizer;

    public GigaAmSpeechRecognizer(IOptions<SpeechOptions> speechOptions)
    {
        SpeechOptions options = speechOptions.Value;

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SpeechAudio.SampleRate;
        config.ModelConfig.Transducer.Encoder = SpeechModels.RecognizerEncoder(options.ModelsDirectory);
        config.ModelConfig.Transducer.Decoder = SpeechModels.RecognizerDecoder(options.ModelsDirectory);
        config.ModelConfig.Transducer.Joiner = SpeechModels.RecognizerJoiner(options.ModelsDirectory);
        config.ModelConfig.Tokens = SpeechModels.RecognizerTokens(options.ModelsDirectory);
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = options.RecognizerThreads;
        // Beam на 4 пути — режим, на котором сделан выбор модели (docs/measurements.md, «Открытый набор»).
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;

        _recognizer = new OfflineRecognizer(config);
    }

    public Task<string> Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using OfflineStream stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SpeechAudio.SampleRate, samples.ToArray());
        _recognizer.Decode(stream);

        return Task.FromResult(stream.Result.Text.Trim());
    }

    public void Dispose() => _recognizer.Dispose();
}
