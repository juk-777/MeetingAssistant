using System.CommandLine;
using MeetingAssistant.Cli.Meetings;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingAssistant.Cli.Start;

/// <summary>
/// <c>meeting start</c>: живой созвон — микрофон и системный звук до Ctrl+C.
/// </summary>
internal static class StartCommand
{
    public static Command Create()
    {
        Argument<string> nameArgument = new("name") { Description = "Имя встречи" };

        Option<FileInfo?> glossaryOption = new("--glossary") { Description = "Словарь встречи: шаблон на строку" };

        Command command = new("start", "Записать встречу: микрофон — «Я», системный звук — «Собеседники»; Ctrl+C — остановить")
        {
            nameArgument,
            glossaryOption,
        };

        command.SetAction((parseResult, cancellationToken) => Run(
            parseResult.GetRequiredValue(nameArgument),
            parseResult.GetValue(glossaryOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> Run(string name, FileInfo? glossary, CancellationToken cancellationToken)
    {
        Result<MeetingHost> opened = MeetingHost.Open();

        if (opened.IsFailure)
        {
            return await MeetingHost.Exit(opened);
        }

        using MeetingHost host = opened.Value;

        Result<string[]> glossaryLines = await MeetingHost.ReadGlossary(glossary, cancellationToken);

        if (glossaryLines.IsFailure)
        {
            return await MeetingHost.Exit(glossaryLines);
        }

        ILiveCapture capture = host.Services.GetRequiredService<ILiveCapture>();
        Result<OpenedCapture> devices = capture.Open();

        if (devices.IsFailure)
        {
            return await MeetingHost.Exit(devices);
        }

        Result started = await host.Start(name, glossaryLines.Value, cancellationToken);

        if (started.IsFailure)
        {
            return await MeetingHost.Exit(started);
        }

        Console.WriteLine($"Каталог встречи: {Path.GetDirectoryName(capture.AudioFile)}");
        Console.WriteLine($"Микрофон («Я»): {devices.Value.Microphone}");
        Console.WriteLine($"Системный звук («Собеседники»): {devices.Value.SystemSound}");
        Console.WriteLine("Идёт запись. Ctrl+C — остановить.");

        return await MeetingHost.Exit(await host.Record(capture.RecordToMeeting(devices.Value.Source), cancellationToken));
    }
}
