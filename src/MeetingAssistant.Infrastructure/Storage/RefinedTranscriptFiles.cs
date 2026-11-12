using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Domain.Meetings;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Второй транскрипт встречи — <c>transcript.whisper.md</c> в формате <c>transcript.md</c>.
/// </summary>
/// <remarks>
/// Пишется в <c>.part</c> и переименовывается по завершении: есть итоговый файл — он полный, есть <c>.part</c> —
/// уточнение идёт. Прежний итоговый файл заменяется только завершением.
/// </remarks>
internal sealed class RefinedTranscriptFiles : IRefinedTranscripts
{
    public const string FileName = "transcript.whisper.md";
    public const string DraftSuffix = ".part";

    public Task<IRefinedTranscriptDraft> Begin(string directory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, FileName);
        string draft = path + DraftSuffix;

        // Остаток прошлой оборванной попытки: строки дописываются, поэтому файл начинается пустым.
        File.WriteAllBytes(draft, []);

        return Task.FromResult<IRefinedTranscriptDraft>(new Draft(draft, path));
    }

    private sealed class Draft(string draft, string path) : IRefinedTranscriptDraft
    {
        private bool _completed;

        public Task Append(Utterance utterance, CancellationToken cancellationToken) =>
            MeetingFileLine.Append(draft, MeetingFileFormat.Transcript(utterance), cancellationToken);

        public Task Complete(CancellationToken cancellationToken)
        {
            File.Move(draft, path, overwrite: true);
            _completed = true;

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                File.Delete(draft);
            }

            return ValueTask.CompletedTask;
        }
    }
}
