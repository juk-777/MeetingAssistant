using System.Diagnostics;
using System.Security.Cryptography;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using NAudio.Wave;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Storage;

public sealed class RefineFilesTests : IDisposable
{
    private const string EndLine = "1762774262.081\t3\tконец записи";

    private static readonly DateTimeOffset RecognizedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_762_774_261_081);

    private readonly DirectoryInfo _meeting = Directory.CreateTempSubdirectory("meeting-assistant-refine-");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string FinalPath => Path.Combine(_meeting.FullName, RefinedTranscriptFiles.FileName);

    private string DraftPath => FinalPath + RefinedTranscriptFiles.DraftSuffix;

    [Fact]
    public async Task Open_WhenNoDirectory_RefusesNamingIt()
    {
        Result<RecordedMeeting> opened = await Open(Path.Combine(_meeting.FullName, "нет"));

        opened.Error.Code.ShouldBe(StorageErrors.MeetingNotFoundCode);
    }

    [Fact]
    public async Task Open_WhenNoAudio_Refuses()
    {
        await WriteEvents(EndLine);

        Result<RecordedMeeting> opened = await Open(_meeting.FullName);

        opened.Error.Code.ShouldBe(StorageErrors.AudioNotFoundCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_WhenNoEndOfRecording_RefusesAsNotFinished(bool eventsExist)
    {
        WriteAudio(seconds: 1);

        if (eventsExist)
        {
            // События есть, конца записи нет — встреча ещё идёт.
            await WriteEvents("1762774261.083\t3\tнакопилось");
        }

        Result<RecordedMeeting> opened = await Open(_meeting.FullName);

        opened.Error.Code.ShouldBe(StorageErrors.RecordingNotFinishedCode);
    }

    [Fact]
    public async Task Open_WhenRecordingEnded_GivesBothChannelsOfWholeRecordingWithoutPacing()
    {
        const int seconds = 30;
        WriteAudio(seconds);
        await WriteEvents("1762774261.083\t1\tвопрос", EndLine);

        RecordedMeeting meeting = (await Open(_meeting.FullName)).Value;
        var elapsed = Stopwatch.StartNew();
        List<AudioChunk> chunks = [];

        await foreach (AudioChunk chunk in meeting.Audio.Read(Token))
        {
            chunks.Add(chunk);
        }

        // В реальном темпе чтение заняло бы длину записи.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(seconds) / 3);
        meeting.Duration.ShouldBe(TimeSpan.FromSeconds(seconds));
        chunks.Where(chunk => chunk.Channel == Channel.Me).Sum(chunk => chunk.Samples.Length)
            .ShouldBe(seconds * SpeechAudio.SampleRate);
        chunks.Where(chunk => chunk.Channel == Channel.Others).Sum(chunk => chunk.Samples.Length)
            .ShouldBe(seconds * SpeechAudio.SampleRate);
    }

    [Fact]
    public async Task Draft_WhileWriting_IsOnlyDraftAndOnCompletionBecomesTranscriptInSkillFormat()
    {
        IRefinedTranscriptDraft draft = await new RefinedTranscriptFiles().Begin(_meeting.FullName, Token);

        await draft.Append(Utterance(1, Channel.Others, "Первая\tстрока"), Token);
        await draft.Append(Utterance(2, Channel.Me, "Вторая."), Token);

        File.Exists(FinalPath).ShouldBeFalse();
        File.Exists(DraftPath).ShouldBeTrue();

        await draft.Complete(Token);
        await draft.DisposeAsync();

        File.Exists(DraftPath).ShouldBeFalse();
        (await File.ReadAllLinesAsync(FinalPath, Token)).ShouldBe(
        [
            "1\t1762774261.081\t[00:01:03] Собеседники: Первая строка",
            "2\t1762774261.081\t[00:01:03] Я: Вторая.",
        ]);
    }

    [Fact]
    public async Task Draft_WhenCompleted_ReplacesPreviousTranscript()
    {
        await File.WriteAllTextAsync(FinalPath, "прежний\n", Token);

        await using (IRefinedTranscriptDraft draft = await new RefinedTranscriptFiles().Begin(_meeting.FullName, Token))
        {
            await draft.Append(Utterance(1, Channel.Me, "Новый."), Token);
            (await File.ReadAllTextAsync(FinalPath, Token)).ShouldBe("прежний\n");
            await draft.Complete(Token);
        }

        (await File.ReadAllLinesAsync(FinalPath, Token)).ShouldBe(["1\t1762774261.081\t[00:01:03] Я: Новый."]);
    }

    [Fact]
    public async Task Draft_WhenReleasedWithoutCompletion_RemovesDraftAndKeepsPreviousTranscript()
    {
        await File.WriteAllTextAsync(FinalPath, "прежний\n", Token);

        await using (IRefinedTranscriptDraft draft = await new RefinedTranscriptFiles().Begin(_meeting.FullName, Token))
        {
            await draft.Append(Utterance(1, Channel.Me, "Брошенный."), Token);
        }

        File.Exists(DraftPath).ShouldBeFalse();
        (await File.ReadAllTextAsync(FinalPath, Token)).ShouldBe("прежний\n");
    }

    [Fact]
    public async Task Begin_WhenDraftLeftByAbortedRun_StartsItEmpty()
    {
        await File.WriteAllTextAsync(DraftPath, "1\tстарое\n", Token);

        await using IRefinedTranscriptDraft draft = await new RefinedTranscriptFiles().Begin(_meeting.FullName, Token);
        await draft.Append(Utterance(1, Channel.Me, "Новое."), Token);

        (await File.ReadAllLinesAsync(DraftPath, Token)).ShouldBe(["1\t1762774261.081\t[00:01:03] Я: Новое."]);
    }

    [Fact]
    public async Task Refine_WhenOpenedAndCompleted_LeavesMeetingFilesUntouched()
    {
        WriteAudio(seconds: 1);
        await WriteEvents(EndLine);
        await File.WriteAllTextAsync(Path.Combine(_meeting.FullName, TranscriptFile.FileName), "1\tживой\n", Token);
        Dictionary<string, string> before = Hashes();

        RecordedMeeting meeting = (await Open(_meeting.FullName)).Value;
        (await meeting.Audio.Read(Token).CountAsync(Token)).ShouldBePositive();

        await using (IRefinedTranscriptDraft draft = await new RefinedTranscriptFiles().Begin(_meeting.FullName, Token))
        {
            await draft.Append(Utterance(1, Channel.Me, "Текст."), Token);
            await draft.Complete(Token);
        }

        Hashes().ShouldBe(before);
    }

    public void Dispose() => _meeting.Delete(recursive: true);

    private static Task<Result<RecordedMeeting>> Open(string directory) => new RecordedMeetingFolder().Open(directory, Token);

    private static Utterance Utterance(int number, Channel channel, string text) =>
        new(number, channel, new SpeechSpan(TimeSpan.FromSeconds(63.5), TimeSpan.FromSeconds(2)), text, RecognizedAt);

    private Task WriteEvents(params string[] lines) =>
        File.WriteAllLinesAsync(Path.Combine(_meeting.FullName, EventsFile.FileName), lines, Token);

    private void WriteAudio(int seconds)
    {
        using var writer = new WaveFileWriter(
            Path.Combine(_meeting.FullName, LiveCapture.AudioFileName),
            new WaveFormat(SpeechAudio.SampleRate, 16, 2));
        float[] frames = new float[seconds * SpeechAudio.SampleRate * 2];
        writer.WriteSamples(frames, 0, frames.Length);
    }

    private Dictionary<string, string> Hashes() =>
        new[] { LiveCapture.AudioFileName, EventsFile.FileName, TranscriptFile.FileName }
            .ToDictionary(name => name, name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_meeting.FullName, name)))));
}
