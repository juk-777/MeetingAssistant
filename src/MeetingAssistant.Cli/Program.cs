using System.CommandLine;
using MeetingAssistant.Cli.Replay;
using MeetingAssistant.Cli.Start;

RootCommand rootCommand = new("Запись встречи: транскрипт и события для сессии Claude Code")
{
    StartCommand.Create(),
    ReplayCommand.Create(),
};

// Ctrl+C отменяет подачу звука, а уже вырезанные фразы распознаются и встреча получает конец записи.
// Вживую в очереди одна-две фразы; срок с запасом — на ускоренный replay с длинной очередью.
var invocation = new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) };

return await rootCommand.Parse(args).InvokeAsync(invocation);
