using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Дозапись реплик в <c>transcript.md</c> каталога встречи.
/// </summary>
internal sealed class TranscriptFile(MeetingDirectory meetingDirectory) : ITranscriptWriter
{
    public const string FileName = "transcript.md";

    public Task Append(Utterance utterance, CancellationToken cancellationToken) =>
        MeetingFileLine.Append(
            Path.Combine(meetingDirectory.Path, FileName),
            MeetingFileFormat.Transcript(utterance),
            cancellationToken);
}

/// <summary>
/// Дозапись событий в <c>events.log</c> каталога встречи.
/// </summary>
internal sealed class EventsFile(MeetingDirectory meetingDirectory) : IWakeEventWriter
{
    public const string FileName = "events.log";

    public Task AppendWake(SessionWakeRequested wake, CancellationToken cancellationToken) =>
        Append(MeetingFileFormat.Wake(wake), cancellationToken);

    public Task AppendEnd(MeetingStopped stopped, CancellationToken cancellationToken) =>
        Append(MeetingFileFormat.End(stopped), cancellationToken);

    private Task Append(string line, CancellationToken cancellationToken) =>
        MeetingFileLine.Append(Path.Combine(meetingDirectory.Path, FileName), line, cancellationToken);
}

/// <summary>
/// Строки файлов встречи; формат читает скилл <c>/meeting</c> (<c>docs/architecture.md</c>).
/// </summary>
internal static partial class MeetingFileFormat
{
    public const string EndOfRecording = "конец записи";

    public static string Transcript(Utterance utterance) =>
        $"{utterance.Number}\t{Epoch(utterance.RecognizedAt)}\t[{Timecode(utterance.Span.Start)}] " +
        $"{ChannelName(utterance.Channel)}: {SingleLine(utterance.Text)}";

    public static string Wake(SessionWakeRequested wake) =>
        $"{Epoch(wake.At)}\t{wake.UtteranceNumber}\t{string.Join(',', wake.Reasons.Select(ReasonName))}";

    public static string End(MeetingStopped stopped) =>
        $"{Epoch(stopped.At)}\t{stopped.LastUtteranceNumber}\t{EndOfRecording}";

    // Секунды Unix с миллисекундами: целочисленно, без округления double.
    private static string Epoch(DateTimeOffset at)
    {
        long milliseconds = at.ToUnixTimeMilliseconds();
        return string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000}.{milliseconds % 1000:D3}");
    }

    private static string Timecode(TimeSpan start) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)start.TotalHours:D2}:{start.Minutes:D2}:{start.Seconds:D2}");

    private static string ChannelName(Channel channel) => channel switch
    {
        Channel.Me => "Я",
        Channel.Others => "Собеседники",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Неизвестный канал."),
    };

    private static string ReasonName(WakeReason reason) => reason switch
    {
        WakeReason.Address => "обращение",
        WakeReason.Question => "вопрос",
        WakeReason.Term => "термин",
        WakeReason.AccumulatedSpeech => "накопилось",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Неизвестный повод."),
    };

    private static string SingleLine(string text) => LineBreaks().Replace(text, " ");

    [GeneratedRegex(@"[\t\r\n]+")]
    private static partial Regex LineBreaks();
}

/// <summary>
/// Дозапись строки: файл открывается на одну строку и закрывается — закрытие отдаёт строку системе, и читатель
/// видит её сразу. Сброса на носитель (fsync) нет: строку читают, пока процесс жив.
/// </summary>
/// <remarks>
/// Читатель вроде <c>tail -F</c> держит файл открытым, поэтому запись разделяет его на чтение и запись.
/// </remarks>
internal static class MeetingFileLine
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task Append(string path, string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Utf8WithoutBom.GetBytes(line + "\n");

        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
