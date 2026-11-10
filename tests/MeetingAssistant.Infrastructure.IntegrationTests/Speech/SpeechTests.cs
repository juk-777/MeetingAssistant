using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Infrastructure.Speech;
using Microsoft.Extensions.Options;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Speech;

/// <summary>
/// Настоящие Silero VAD и GigaAM на образце из архива модели.
/// </summary>
public sealed class SpeechTests
{
    // Кусок записи с диска — 100 мс.
    private const int ReplayChunk = SpeechAudio.SampleRate / 10;

    private static readonly IOptions<SpeechOptions> Settings = Options.Create(
        new SpeechOptions { ModelsDirectory = Models.Directory, RecognizerThreads = 2 });

    // Лениво: без моделей падают тесты, которым нужна запись, с командой скачивания, а не весь класс.
    private static readonly Lazy<float[]> LazyExample = new(Models.ReadExample);

    private static float[] Example => LazyExample.Value;

    [Fact]
    public async Task Recognize_WhenExampleSpeech_ReturnsPunctuatedRussianText()
    {
        using var recognizer = new GigaAmSpeechRecognizer(Settings);

        string text = await recognizer.Recognize(Example, TestContext.Current.CancellationToken);

        text.ShouldContain("лукоморья");
        text.ShouldContain(",");
    }

    [Fact]
    public void Detect_WhenExampleSpeech_KeepsPhraseLongerThanSherpaDefaultWhole()
    {
        List<DetectedPhrase> phrases = Detect(Example, ReplayChunk);

        phrases.Count.ShouldBe(2);
        Duration(phrases[0]).ShouldBeGreaterThan(TimeSpan.FromSeconds(5), "умолчание sherpa-onnx режет фразу на 5 с");
        phrases[1].Start.ShouldBeGreaterThan(phrases[0].Start + Duration(phrases[0]));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(333)]
    [InlineData(160)]
    public void Detect_WhenFedInOtherChunks_CutsSamePhrasesAsReplay(int chunk)
    {
        List<DetectedPhrase> replay = Detect(Example, ReplayChunk);

        List<DetectedPhrase> phrases = Detect(Example, chunk);

        Spans(phrases).ShouldBe(Spans(replay));
    }

    [Fact]
    public void Detect_WhenSilenceBetweenCopies_CountsStartsFromBeginningOfRecording()
    {
        TimeSpan offset = SpeechAudio.Duration(Example.Length) + TimeSpan.FromSeconds(2);
        float[] twice = [.. Example, .. new float[SpeechAudio.SampleRate * 2], .. Example];

        List<DetectedPhrase> once = Detect(Example, ReplayChunk);
        List<DetectedPhrase> phrases = Detect(twice, ReplayChunk);

        phrases.Count.ShouldBe(once.Count * 2);
        // Окна Silero не совпадают со стыком копий, поэтому начало плавает на доли окна, а не на секунды.
        TimeSpan shift = phrases[once.Count].Start - once[0].Start;
        (shift - offset).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void Validate_WhenModelsMissing_FailsWithDownloadCommand()
    {
        DirectoryInfo empty = Directory.CreateTempSubdirectory("meeting-assistant-models-");

        try
        {
            ValidateOptionsResult result = new SpeechOptionsValidator().Validate(
                null,
                new SpeechOptions { ModelsDirectory = empty.FullName, RecognizerThreads = 2 });

            result.Failed.ShouldBeTrue();
            string message = result.FailureMessage.ShouldNotBeNull();
            message.ShouldContain(SpeechModels.Vad(empty.FullName));
            message.ShouldContain($"curl -sSLO $base/{SpeechModels.RecognizerDirectory}.tar.bz2");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Validate_WhenModelsPresent_Succeeds()
    {
        new SpeechOptionsValidator().Validate(null, Settings.Value).Succeeded.ShouldBeTrue();
    }

    private static List<DetectedPhrase> Detect(float[] samples, int chunk)
    {
        using IVoiceActivityDetector detector = new SileroVoiceActivityDetectorFactory(Settings).Create();
        var phrases = new List<DetectedPhrase>();

        for (int offset = 0; offset < samples.Length; offset += Math.Min(chunk, samples.Length - offset))
        {
            phrases.AddRange(detector.Accept(samples.AsMemory(offset, Math.Min(chunk, samples.Length - offset))));
        }

        phrases.AddRange(detector.Flush());

        return phrases;
    }

    private static TimeSpan Duration(DetectedPhrase phrase) => SpeechAudio.Duration(phrase.Samples.Length);

    private static List<(TimeSpan Start, TimeSpan Duration)> Spans(List<DetectedPhrase> phrases) =>
        [.. phrases.Select(phrase => (phrase.Start, Duration(phrase)))];
}
