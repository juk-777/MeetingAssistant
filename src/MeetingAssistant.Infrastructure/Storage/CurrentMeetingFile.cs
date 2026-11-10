using System.Text.Encodings.Web;
using System.Text.Json;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Domain.Meetings;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Встреча процесса и <c>current.json</c> в корне встреч: по нему скилл находит, что читать.
/// </summary>
internal sealed class CurrentMeetingFile(IOptions<StorageOptions> storageOptions, MeetingDirectory meetingDirectory)
    : ICurrentMeeting
{
    public const string FileName = "current.json";

    // Имя как есть (кириллица, кавычки, «&»): файл читает и человек, а в HTML он не попадает.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Meeting? Meeting { get; private set; }

    public async Task Set(Meeting meeting, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(storageOptions.Value.MeetingsRoot);
        string target = Path.Combine(root, FileName);
        string draft = target + ".tmp";

        // Формат читает скилл: docs/architecture.md, «current.json».
        var current = new { meeting.Id, meeting.Name, Directory = meetingDirectory.Path, meeting.StartedAt };
        await File.WriteAllBytesAsync(draft, JsonSerializer.SerializeToUtf8Bytes(current, JsonOptions), cancellationToken);

        // Замена переносом: читатель видит старый файл или новый, но не половину.
        File.Move(draft, target, overwrite: true);

        Meeting = meeting;
    }
}
