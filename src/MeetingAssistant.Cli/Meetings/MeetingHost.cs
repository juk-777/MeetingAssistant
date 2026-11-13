using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MeetingAssistant.Application;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Refine;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Cli.Refine;
using MeetingAssistant.Infrastructure;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Cli.Meetings;

/// <summary>
/// Общий ход команд: настройки и модель, словарь, начало встречи, конвейер записи или уточнение, итог.
/// </summary>
internal sealed class MeetingHost : IDisposable, IAsyncDisposable
{
    public const string SettingsInvalidCode = "Settings.Invalid";
    public const string GlossaryNotFoundCode = "Glossary.NotFound";

    private static readonly Error RefineCancelled =
        new("Refine.Cancelled", "Уточнение прервано: черновик удалён, прежний transcript.whisper.md не тронут.");

    private const int Success = 0;
    private const int Failure = 1;

    private static readonly TimeSpan WindowCloseGrace = TimeSpan.FromSeconds(5);

    private readonly IHost _host;

    private MeetingHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Собирает приложение, проверяет настройки и грузит модель — до начала встречи, чтобы отказ
    /// не оставлял пустого каталога.
    /// </summary>
    /// <param name="loadsRecognizer">Грузить модель живого распознавания: уточнению она не нужна.</param>
    public static Result<MeetingHost> Open(bool loadsRecognizer = true)
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

            if (loadsRecognizer)
            {
                host.Services.GetRequiredService<ISpeechRecognizer>();
            }
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
    public Task<Result> Record(IAudioSource source, CancellationToken cancellationToken) =>
        UntilWindowClosed(
            async token =>
            {
                var elapsed = Stopwatch.StartNew();
                Result recorded = await Services.GetRequiredService<IMeetingRecorder>().Record(source, token);
                int utterances = Services.GetRequiredService<ICurrentMeeting>().Meeting?.LastUtteranceNumber ?? 0;

                Console.WriteLine(Services.GetRequiredService<RecordingStatistics>().Summary(utterances, elapsed.Elapsed));

                return recorded;
            },
            cancellationToken);

    /// <summary>
    /// Уточняет законченную встречу во второй транскрипт и печатает ход и итог. Отмена — отказ: прежний второй
    /// транскрипт цел.
    /// </summary>
    public Task<Result> Refine(string directory, CancellationToken cancellationToken) =>
        UntilWindowClosed(
            async token =>
            {
                var progress = new ConsoleRefineProgress();

                try
                {
                    Result refined = await Services.GetRequiredService<ICommandHandler<RefineMeeting>>()
                        .Handle(new RefineMeeting(directory, progress), token);

                    if (refined.IsSuccess)
                    {
                        Console.WriteLine(progress.Summary());
                    }

                    return refined;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return Result.Failure(RefineCancelled);
                }
            },
            cancellationToken);

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Освобождение для тех, у кого есть только асинхронное (распознаватель уточнения).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable host)
        {
            await host.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }

    /// <summary>
    /// Работа до конца или отмены; закрытие окна консоли — та же отмена.
    /// </summary>
    /// <remarks>
    /// Закрытие окна (на Windows SIGHUP — это CTRL_CLOSE_EVENT) Windows не даёт отменить: процесс снимается, как
    /// только обработчик вернётся, и не позже чем через 5 с. Поэтому обработчик ждёт, пока работа дочистится
    /// (очередь фраз, конец записи, WAV; брошенный черновик). Cancel — чтобы там, где завершение можно отменить
    /// (SIGHUP в Linux), процесс вышел своим кодом, как после Ctrl+C.
    /// </remarks>
    private static async Task<Result> UntilWindowClosed(
        Func<CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken)
    {
        using var working = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using PosixSignalRegistration windowClosed = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;

            try
            {
                // Снятие регистрации не ждёт уже начавшегося обработчика: окно закрыли ровно тогда, когда работа
                // кончилась сама, — working может быть освобождён. Тогда отменять уже нечего.
                // ReSharper disable once AccessToDisposedClosure
                working.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            finished.Task.Wait(WindowCloseGrace);
        });

        try
        {
            return await work(working.Token);
        }
        finally
        {
            finished.TrySetResult();
        }
    }
}
