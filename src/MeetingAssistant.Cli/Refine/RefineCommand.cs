using System.CommandLine;
using System.Diagnostics;
using MeetingAssistant.Cli.Meetings;
using MeetingAssistant.SharedKernel;

namespace MeetingAssistant.Cli.Refine;

/// <summary>
/// <c>meeting refine</c>: второй транскрипт законченной встречи Whisper — <c>transcript.whisper.md</c>.
/// </summary>
internal static class RefineCommand
{
    public static Command Create()
    {
        Argument<DirectoryInfo> directoryArgument = new("каталог") { Description = "Каталог законченной встречи" };

        Command command = new("refine", "Уточнить встречу Whisper: transcript.whisper.md рядом с transcript.md")
        {
            directoryArgument,
        };

        command.SetAction((parseResult, cancellationToken) =>
            Run(parseResult.GetRequiredValue(directoryArgument), cancellationToken));

        return command;
    }

    private static async Task<int> Run(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        // Уточнение идёт фоном: живая запись на той же машине получает процессор раньше.
        using (Process process = Process.GetCurrentProcess())
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }

        Result<MeetingHost> opened = MeetingHost.Open(loadsRecognizer: false);

        if (opened.IsFailure)
        {
            return await MeetingHost.Exit(opened);
        }

        await using MeetingHost host = opened.Value;

        return await MeetingHost.Exit(await host.Refine(directory.FullName, cancellationToken));
    }
}
