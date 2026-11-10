namespace MeetingAssistant.Infrastructure.Storage;

/// <summary>
/// Где лежат каталоги встреч и <c>current.json</c>.
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>
    /// Корень встреч; относительный путь — от текущего каталога процесса.
    /// </summary>
    public required string MeetingsRoot { get; init; }
}
