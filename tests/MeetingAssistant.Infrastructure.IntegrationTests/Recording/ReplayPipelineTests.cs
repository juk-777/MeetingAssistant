using MeetingAssistant.Application;
using MeetingAssistant.Application.Abstractions.Messaging;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Application.Meetings.Start;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.Infrastructure.IntegrationTests.Speech;
using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.Infrastructure.Storage;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Recording;

/// <summary>
/// Запись с диска через весь конвейер: WAV → VAD → GigaAM → файлы встречи, в боевой регистрации.
/// </summary>
public sealed class ReplayPipelineTests : IDisposable
{
    // Подача без пауз: темп проверяют тесты источника.
    private const double NoPacing = 1_000_000;

    // Не целые секунды: начало фразы после тишины VAD ставит на доли секунды раньше, а таймкод — в секундах.
    private static readonly TimeSpan OthersDelay = TimeSpan.FromSeconds(3.5);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("meeting-assistant-replay-");
    private readonly ServiceProvider _provider;

    public ReplayPipelineTests()
    {
        Models.EnsurePresent();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{StorageOptions.Section}:{nameof(StorageOptions.MeetingsRoot)}"] = _root.FullName,
                [$"{SpeechOptions.Section}:{nameof(SpeechOptions.ModelsDirectory)}"] = Models.Directory,
                [$"{SpeechOptions.Section}:{nameof(SpeechOptions.RecognizerThreads)}"] = "2",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddApplication();
        services.AddInfrastructure(configuration);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Record_WhenStereoReplayed_WritesBothChannelsAndEventsEndingWithEndOfRecording()
    {
        string wav = StereoFromExample();
        Result<Guid> started = await _provider.GetRequiredService<ICommandHandler<StartMeeting, Guid>>()
            .Handle(new StartMeeting("Прогон", ["."]), Token);
        started.IsSuccess.ShouldBeTrue();

        Result recorded = await _provider.GetRequiredService<IMeetingRecorder>()
            .Record(WavFileAudioSource.Open(wav, NoPacing, TimeProvider.System).Value, Token);

        recorded.IsSuccess.ShouldBeTrue();
        string folder = _provider.GetRequiredService<MeetingDirectory>().Path;
        string[][] transcript = Fields(Path.Combine(folder, TranscriptFile.FileName));
        string[][] events = Fields(Path.Combine(folder, EventsFile.FileName));

        transcript.Select(line => line[0]).ShouldBe(["1", "2", "3", "4"]);
        transcript.Select(line => line[2][..Math.Max(0, line[2].IndexOf(':', 10))]).ShouldBe(
            ["[00:00:00] Я", "[00:00:09] Я", "[00:00:03] Собеседники", "[00:00:12] Собеседники"],
            ignoreOrder: true);
        transcript.ShouldAllBe(line => line[2].Contains("лукоморья") || line[2].Contains("похвал"));

        events[..^1].Select(line => line[1]).ShouldBe(["1", "2", "3", "4"]);
        events[..^1].ShouldAllBe(line => line[2] == "термин");
        events[^1][1..].ShouldBe(["4", MeetingFileFormat.EndOfRecording]);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _root.Delete(recursive: true);
    }

    private static string[][] Fields(string path) =>
        [.. File.ReadAllLines(path).Select(line => line.Split('\t'))];

    // Левый канал — образец, правый — тот же образец позже: фразы каналов перекрываются по времени.
    private string StereoFromExample()
    {
        float[] example = Models.ReadExample();
        int delay = (int)(OthersDelay.TotalSeconds * SpeechAudio.SampleRate);
        int frames = example.Length + delay;
        var interleaved = new float[frames * 2];

        for (int frame = 0; frame < frames; frame++)
        {
            interleaved[frame * 2] = frame < example.Length ? example[frame] : 0;
            interleaved[(frame * 2) + 1] = frame >= delay ? example[frame - delay] : 0;
        }

        string path = Path.Combine(_root.FullName, "stereo.wav");

        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(SpeechAudio.SampleRate, 2)))
        {
            writer.WriteSamples(interleaved, 0, interleaved.Length);
        }

        return path;
    }
}
