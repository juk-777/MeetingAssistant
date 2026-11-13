using System.Text;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Второй транскрипт встречи — <c>transcript.whisper.md</c> в формате <c>transcript.md</c>.
/// </summary>
/// <remarks>
/// Пишется в <c>.part</c> и переименовывается по завершении: есть итоговый файл — он полный, есть <c>.part</c> —
/// уточнение идёт. Прежний итоговый файл заменяется только завершением. Черновик открыт без совместного доступа
/// всё время уточнения: второе уточнение той же встречи получает отказ, а не пишет в тот же файл.
/// </remarks>
internal sealed class RefinedTranscriptFiles : IRefinedTranscripts
{
    public const string FileName = "transcript.whisper.md";
    public const string DraftSuffix = ".part";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public Task<Result<IRefinedTranscriptDraft>> Begin(string directory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, FileName);
        string draft = path + DraftSuffix;
        FileStream stream;

        try
        {
            // Create — остаток прошлой оборванной попытки начинается пустым.
            stream = new FileStream(draft, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(draft))
        {
            return Task.FromResult(Result.Failure<IRefinedTranscriptDraft>(StorageErrors.RefineInProgress(directory)));
        }

        return Task.FromResult(Result.Success<IRefinedTranscriptDraft>(new Draft(stream, draft, path)));
    }

    private sealed class Draft(FileStream stream, string draft, string path) : IRefinedTranscriptDraft
    {
        private bool _completed;

        public async Task Append(Utterance utterance, CancellationToken cancellationToken) =>
            await stream.WriteAsync(Utf8WithoutBom.GetBytes(MeetingFileFormat.Transcript(utterance) + "\n"), cancellationToken);

        public async Task Complete(CancellationToken cancellationToken)
        {
            await stream.DisposeAsync();
            File.Move(draft, path, overwrite: true);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();

            if (!_completed)
            {
                File.Delete(draft);
            }
        }
    }
}
