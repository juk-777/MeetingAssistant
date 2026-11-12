using System.Diagnostics;
using System.Globalization;
using MeetingAssistant.Application;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Cli.Meetings;

/// <summary>
/// Общий ход команд записи: настройки и модель, словарь, начало встречи, конвейер, итог.
/// </summary>
internal sealed class MeetingHost : IDisposable
{
    public const string SettingsInvalidCode = "Settings.Invalid";
    public const string GlossaryNotFoundCode = "Glossary.NotFound";

    private const int Success = 0;
    private const int Failure = 1;

    private readonly IHost _host;

    private MeetingHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Собирает приложение, проверяет настройки и грузит модель — до начала встречи, чтобы отказ
    /// не оставлял пустого каталога.
    /// </summary>
    public static Result<MeetingHost> Open()
    {
        // Настройки — рядом с исполняемым файлом, а не в текущем каталоге: meeting запускают откуда угодно.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        RecordingStatistics.Register(builder.Services);

        IHost host = builder.Build();

        try
        {
            host.Services.GetRequiredService<IStartupValidator>().Validate();
            host.Services.GetRequiredService<ISpeechRecognizer>();
        }
        catch (OptionsValidationException exception)
        {
            host.Dispose();
            return Result.Failure<MeetingHost>(new Error(SettingsInvalidCode, string.Join(Environment.NewLine, exception.Failures)));
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return Result.Success(new MeetingHost(host));
    }

    /// <summary>
    /// Словарь встречи: шаблон на строку; нет файла — пустой словарь.
    /// </summary>
    public static async Task<Result<string[]>> ReadGlossary(FileInfo? glossary, CancellationToken cancellationToken)
    {
        if (glossary is null)
        {
            return Result.Success<string[]>([]);
        }

        if (!glossary.Exists)
        {
            return Result.Failure<string[]>(new Error(GlossaryNotFoundCode, $"Нет файла словаря: {glossary.FullName}"));
        }

        return Result.Success(await File.ReadAllLinesAsync(glossary.FullName, cancellationToken));
    }

    /// <summary>
    /// Печатает отказ и отдаёт код выхода.
    /// </summary>
    public static async Task<int> Exit(Result result)
    {
        if (result.IsSuccess)
        {
            return Success;
        }

        await Console.Error.WriteLineAsync(
            string.Create(CultureInfo.InvariantCulture, $"{result.Error.Code}: {result.Error.Description}"));
        return Failure;
    }

    public async Task<Result> Start(string name, string[] glossary, CancellationToken cancellationToken) =>
        await Services.GetRequiredService<ICommandHandler<StartMeeting, Guid>>()
            .Handle(new StartMeeting(name, glossary), cancellationToken);

    /// <summary>
    /// Записывает встречу из источника до его конца, отмены или потери устройства и печатает итог.
    /// </summary>
    public async Task<Result> Record(IAudioSource source, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        Result recorded = await Services.GetRequiredService<IMeetingRecorder>().Record(source, cancellationToken);
        int utterances = Services.GetRequiredService<ICurrentMeeting>().Meeting?.LastUtteranceNumber ?? 0;

        Console.WriteLine(Services.GetRequiredService<RecordingStatistics>().Summary(utterances, elapsed.Elapsed));

        return recorded;
    }

    public void Dispose() => _host.Dispose();
}
