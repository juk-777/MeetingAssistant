using System.Text;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Infrastructure.Storage;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Storage;

public sealed class MeetingFilesTests : IDisposable
{
    // Миллисекунды с ведущим нулём: epoch пишется тремя знаками после точки.
    private static readonly DateTimeOffset RecognizedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_762_774_261_081);

    private readonly StorageHarness _harness = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Append_WhenUtteranceAndEventsWritten_WritesLinesInSkillFormat()
    {
        await _harness.Start("Формат");
        var question = new Utterance(
            12,
            Channel.Others,
            new SpeechSpan(new TimeSpan(0, 1, 3, 7, 900), TimeSpan.FromSeconds(2)),
            "А это где\tу нас\r\nлежит?",
            RecognizedAt);
        var answer = new Utterance(13, Channel.Me, new SpeechSpan(TimeSpan.Zero, TimeSpan.FromSeconds(1)), "Тут.", RecognizedAt);

        await _harness.Service<ITranscriptWriter>().Append(question, Token);
        await _harness.Service<ITranscriptWriter>().Append(answer, Token);
        IWakeEventWriter events = _harness.Service<IWakeEventWriter>();
        await events.AppendWake(
            new SessionWakeRequested(12, [WakeReason.Address, WakeReason.Question, WakeReason.Term], RecognizedAt.AddMilliseconds(2)),
            Token);
        await events.AppendWake(new SessionWakeRequested(13, [WakeReason.AccumulatedSpeech], RecognizedAt.AddMilliseconds(3)), Token);
        await events.AppendEnd(new MeetingStopped(13, RecognizedAt.AddSeconds(1)), Token);

        (await ReadLines(TranscriptFile.FileName)).ShouldBe(
        [
            "12\t1762774261.081\t[01:03:07] Собеседники: А это где у нас лежит?",
            "13\t1762774261.081\t[00:00:00] Я: Тут.",
        ]);
        (await ReadLines(EventsFile.FileName)).ShouldBe(
        [
            "1762774261.083\t12\tобращение,вопрос,термин",
            "1762774261.084\t13\tнакопилось",
            "1762774262.081\t13\tконец записи",
        ]);
    }

    [Fact]
    public async Task Append_WhenReaderHoldsFileOpen_ReaderSeesEachLineAtOnce()
    {
        await _harness.Start("Хвост");
        ITranscriptWriter transcript = _harness.Service<ITranscriptWriter>();
        await transcript.Append(Utterance(1), Token);

        await using var tail = new FileStream(
            Path.Combine(_harness.MeetingFolder, TranscriptFile.FileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(tail, Encoding.UTF8);

        (await reader.ReadLineAsync(Token)).ShouldStartWith("1\t");
        (await reader.ReadLineAsync(Token)).ShouldBeNull();

        await transcript.Append(Utterance(2), Token);

        (await reader.ReadLineAsync(Token)).ShouldStartWith("2\t");
    }

    [Fact]
    public async Task Append_WhenWritten_StartsWithoutByteOrderMark()
    {
        await _harness.Start("Кодировка");

        await _harness.Service<ITranscriptWriter>().Append(Utterance(1), Token);

        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_harness.MeetingFolder, TranscriptFile.FileName), Token);
        bytes[0].ShouldBe((byte)'1');
    }

    public void Dispose() => _harness.Dispose();

    private static Utterance Utterance(int number) =>
        new(number, Channel.Me, new SpeechSpan(TimeSpan.Zero, TimeSpan.FromSeconds(1)), "Текст", RecognizedAt);

    private async Task<string[]> ReadLines(string fileName) =>
        await File.ReadAllLinesAsync(Path.Combine(_harness.MeetingFolder, fileName), Token);
}
