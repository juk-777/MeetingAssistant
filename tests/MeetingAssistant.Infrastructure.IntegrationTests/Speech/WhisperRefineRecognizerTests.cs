using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.SharedKernel;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Speech;

/// <summary>
/// Настоящий Whisper на tiny: связка с Whisper.net, а не качество распознавания.
/// </summary>
public sealed class WhisperRefineRecognizerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recognize_WhenPreparedAndExampleSpeech_ReturnsRussianSegmentsWithinTheSpeech()
    {
        Models.EnsureWhisperTinyPresent();
        await using var recognizer = new WhisperRefineRecognizer(Models.WhisperTiny);
        float[] example = Models.ReadExample();

        Result prepared = await recognizer.Prepare(Token);
        IReadOnlyList<RecognizedSegment> segments = await recognizer.Recognize(example, Token);

        prepared.IsSuccess.ShouldBeTrue();
        segments.ShouldNotBeEmpty();
        segments.ShouldAllBe(segment => segment.Start >= TimeSpan.Zero && segment.Start < segment.End);
        segments[^1].End.ShouldBeLessThanOrEqualTo(SpeechAudio.Duration(example.Length) + TimeSpan.FromSeconds(1));
        segments.ShouldAllBe(segment => segment.Text == segment.Text.Trim());
        string text = string.Join(' ', segments.Select(segment => segment.Text));
        text.Count(char.IsLetter).ShouldBeGreaterThan(10);
        text.Where(char.IsLetter).ShouldAllBe(letter => letter >= 'А' && letter <= 'я' || letter == 'ё' || letter == 'Ё');
    }

    [Fact]
    public async Task Prepare_WhenModelMissing_RefusesAsModelMissing()
    {
        await using var recognizer = new WhisperRefineRecognizer(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin"));

        Result prepared = await recognizer.Prepare(Token);

        prepared.Error.Code.ShouldBe(SpeechErrors.ModelMissingCode);
    }

    [Fact]
    public async Task Recognize_WhenNotPrepared_Throws()
    {
        await using var recognizer = new WhisperRefineRecognizer(Models.WhisperTiny);

        await Should.ThrowAsync<InvalidOperationException>(recognizer.Recognize(new float[16_000], Token));
    }
}
