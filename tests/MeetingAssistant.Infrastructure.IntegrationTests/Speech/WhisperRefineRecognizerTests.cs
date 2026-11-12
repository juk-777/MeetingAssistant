using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Speech;

/// <summary>
/// Настоящий Whisper на tiny: связка с Whisper.net, а не качество распознавания.
/// </summary>
public sealed class WhisperRefineRecognizerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recognize_WhenPreparedAndExampleSpeech_ReturnsRussianText()
    {
        Models.EnsureWhisperTinyPresent();
        await using var recognizer = new WhisperRefineRecognizer(ModelFile());

        Result prepared = await recognizer.Prepare(Token);
        string text = await recognizer.Recognize(Models.ReadExample(), Token);

        prepared.IsSuccess.ShouldBeTrue();
        text.ShouldNotBeNullOrWhiteSpace();
        text.ShouldBe(text.Trim());
        text.Count(char.IsLetter).ShouldBeGreaterThan(10);
        text.Where(char.IsLetter).ShouldAllBe(letter => letter >= 'А' && letter <= 'я' || letter == 'ё' || letter == 'Ё');
    }

    [Fact]
    public async Task Recognize_WhenNotPrepared_Throws()
    {
        await using var recognizer = new WhisperRefineRecognizer(ModelFile());

        await Should.ThrowAsync<InvalidOperationException>(recognizer.Recognize(new float[16_000], Token));
    }

    // Модель на месте: загрузки нет, клиент не нужен.
    private static WhisperModelFile ModelFile() =>
        new(Models.Directory, Models.WhisperTiny, new HttpClient(), NullLogger<WhisperModelFile>.Instance);
}
