using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Infrastructure.Audio;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Time.Testing;
using NAudio.Wave;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class WavFileAudioSourceTests : IDisposable
{
    // Подача без пауз: темп проверяет отдельный тест на часах теста.
    private const double NoPacing = 1_000_000;

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("meeting-assistant-wav-");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_WhenMono_GivesWholeRecordingAsOthersInChunksOf100Milliseconds()
    {
        // Разные значения по всей записи: кусок, ссылающийся на буфер чтения, перезаписался бы следующим.
        float[] ramp = [.. Enumerable.Range(0, 25_000).Select(index => index / 50_000f)];
        string path = Write(new WaveFormat(SpeechAudio.SampleRate, 16, 1), ramp);

        List<AudioChunk> chunks = await ReadAll(path);

        chunks.ShouldAllBe(chunk => chunk.Channel == Channel.Others);
        chunks.Select(chunk => chunk.Samples.Length).ShouldBe([.. Enumerable.Repeat(WavFileAudioSource.ChunkFrames, 15), 1_000]);
        float[] read = [.. chunks.SelectMany(chunk => chunk.Samples.ToArray())];

        // PCM 16 бит: NAudio пишет с множителем 32767, а читает делителем 32768.
        read.Zip(ramp).ShouldAllBe(pair => Math.Abs(pair.First - pair.Second) < 0.001f);
    }

    [Fact]
    public async Task Read_WhenStereo_GivesLeftAsMeAndRightAsOthers()
    {
        float[] interleaved = [.. Enumerable.Range(0, 4_000).SelectMany(_ => new[] { 0.25f, -0.5f })];
        string path = Write(WaveFormat.CreateIeeeFloatWaveFormat(SpeechAudio.SampleRate, 2), interleaved);

        List<AudioChunk> chunks = await ReadAll(path);

        float[] me = [.. chunks.Where(chunk => chunk.Channel == Channel.Me).SelectMany(chunk => chunk.Samples.ToArray())];
        float[] others = [.. chunks.Where(chunk => chunk.Channel == Channel.Others).SelectMany(chunk => chunk.Samples.ToArray())];
        // float 32 пишется и читается без преобразования, поэтому значения сравниваются точно.
        me.ShouldBe(Constant(0.25f, 4_000));
        others.ShouldBe(Constant(-0.5f, 4_000));
    }

    [Fact]
    public async Task Read_WhenPaced_GivesChunkOnlyAfterItHasSoundedAtGivenSpeed()
    {
        string path = Write(new WaveFormat(SpeechAudio.SampleRate, 16, 1), Constant(0.1f, SpeechAudio.SampleRate));
        var time = new FakeTimeProvider();
        IAudioSource source = WavFileAudioSource.Open(path, speed: 2, time).Value;
        await using IAsyncEnumerator<AudioChunk> chunks = source.Read(Token).GetAsyncEnumerator(Token);

        // Кусок 100 мс при ускорении вдвое звучит 50 мс.
        Task<bool> first = chunks.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromMilliseconds(49));
        first.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromMilliseconds(1));
        (await first.WaitAsync(TimeSpan.FromSeconds(10), Token)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(44_100, 16, 1)]
    [InlineData(16_000, 8, 1)]
    [InlineData(16_000, 24, 1)]
    [InlineData(16_000, 32, 1)]
    [InlineData(16_000, 16, 3)]
    public void Open_WhenFormatUnsupported_FailsWithConversionCommand(int sampleRate, int bits, int channels)
    {
        string path = Write(new WaveFormat(sampleRate, bits, channels), []);

        Result<IAudioSource> source = WavFileAudioSource.Open(path, 1, TimeProvider.System);

        source.Error.Code.ShouldBe(AudioErrors.UnsupportedFormatCode);
        source.Error.Description.ShouldContain("ffmpeg");
    }

    [Theory]
    [InlineData("ID3 это не WAV, а например mp3")]
    [InlineData("")]
    public async Task Open_WhenFileIsNotWave_FailsWithConversionCommand(string content)
    {
        string path = Path.Combine(_folder.FullName, "запись.wav");
        await File.WriteAllTextAsync(path, content, Token);

        Result<IAudioSource> source = WavFileAudioSource.Open(path, 1, TimeProvider.System);

        source.Error.Code.ShouldBe(AudioErrors.UnsupportedFormatCode);
        source.Error.Description.ShouldContain("ffmpeg");
    }

    [Fact]
    public async Task Read_WhenCancelledWithoutPacing_StopsBeforeEndOfFile()
    {
        string path = Write(new WaveFormat(SpeechAudio.SampleRate, 16, 1), Constant(0.1f, SpeechAudio.SampleRate * 10));
        IAudioSource source = WavFileAudioSource.Open(path, NoPacing, TimeProvider.System).Value;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int chunks = 0;
        bool cancelled = false;

        try
        {
            await foreach (AudioChunk _ in source.Read(cancellation.Token))
            {
                chunks++;
                await cancellation.CancelAsync();
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        cancelled.ShouldBeTrue();
        chunks.ShouldBe(1);
    }

    [Fact]
    public void Open_WhenFileMissing_FailsWithFileNotFound()
    {
        Result<IAudioSource> source = WavFileAudioSource.Open(Path.Combine(_folder.FullName, "нет.wav"), 1, TimeProvider.System);

        source.Error.Code.ShouldBe(AudioErrors.FileNotFoundCode);
    }

    public void Dispose() => _folder.Delete(recursive: true);

    private static float[] Constant(float value, int count) => [.. Enumerable.Repeat(value, count)];

    private static async Task<List<AudioChunk>> ReadAll(string path)
    {
        IAudioSource source = WavFileAudioSource.Open(path, NoPacing, TimeProvider.System).Value;
        var chunks = new List<AudioChunk>();

        await foreach (AudioChunk chunk in source.Read(Token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private string Write(WaveFormat format, float[] interleaved)
    {
        string path = Path.Combine(_folder.FullName, $"{Guid.NewGuid():N}.wav");

        using (var writer = new WaveFileWriter(path, format))
        {
            writer.WriteSamples(interleaved, 0, interleaved.Length);
        }

        return path;
    }
}
