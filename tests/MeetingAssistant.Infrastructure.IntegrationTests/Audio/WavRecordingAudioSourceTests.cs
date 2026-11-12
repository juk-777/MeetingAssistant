using System.Runtime.CompilerServices;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using MeetingAssistant.Infrastructure.Audio;
using NAudio.Wave;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Audio;

public sealed class WavRecordingAudioSourceTests : IDisposable
{
    // PCM 16 бит: NAudio пишет с множителем 32767, а читает делителем 32768.
    private const float Pcm16Tolerance = 0.001f;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("meeting-assistant-audio-");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string AudioPath => Path.Combine(_folder.FullName, "audio.wav");

    [Fact]
    public async Task Read_WhenChannelsComeInUnevenChunks_PassesChunksOnAndWritesMeLeftOthersRight()
    {
        AudioChunk[] chunks =
        [
            Chunk(Channel.Me, 0.25f, 600),
            Chunk(Channel.Others, -0.5f, 1_000),
            Chunk(Channel.Me, 0.25f, 400),
        ];

        List<AudioChunk> passed = await ReadAll(new ChunkSource(chunks));

        passed.ShouldBe(chunks);
        (float[] me, float[] others) = await ReadBack();
        me.Length.ShouldBe(1_000);
        me.ShouldAllBe(sample => Math.Abs(sample - 0.25f) < Pcm16Tolerance);
        others.ShouldAllBe(sample => Math.Abs(sample + 0.5f) < Pcm16Tolerance);
    }

    [Fact]
    public async Task Read_WhenOneChannelEndsShorter_PadsItWithSilence()
    {
        await ReadAll(new ChunkSource([Chunk(Channel.Me, 0.25f, 1_000), Chunk(Channel.Others, -0.5f, 700)]));

        (float[] me, float[] others) = await ReadBack();
        me.Length.ShouldBe(1_000);
        others[..700].ShouldAllBe(sample => Math.Abs(sample + 0.5f) < Pcm16Tolerance);
        others[700..].ShouldAllBe(sample => sample == 0);
    }

    [Fact]
    public async Task Read_WhenSampleBeyondFullScale_WritesItAsFullScaleOfSameSign()
    {
        await ReadAll(new ChunkSource([Chunk(Channel.Me, 1.5f, 100), Chunk(Channel.Others, -1.5f, 100)]));

        (float[] me, float[] others) = await ReadBack();
        me.ShouldAllBe(sample => sample > 0.99f);
        others.ShouldAllBe(sample => sample < -0.99f);
    }

    [Fact]
    public async Task Read_WhileStillRecording_FileIsReadableUpToLastHeaderUpdate()
    {
        int frames = WavRecordingAudioSource.FramesPerHeaderUpdate * 3 / 2;
        var source = new ChunkSource([Chunk(Channel.Me, 0.25f, frames), Chunk(Channel.Others, -0.5f, frames)], holdOpen: true);
        using var recording = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var recorder = new WavRecordingAudioSource(source, AudioPath);
        Task reading = Consume(recorder, recording.Token);

        await source.Exhausted.WaitAsync(Limit, Token);
        long framesVisible = FramesVisibleToOtherReader();
        await recording.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(reading.WaitAsync(Limit, Token));

        framesVisible.ShouldBeGreaterThanOrEqualTo(WavRecordingAudioSource.FramesPerHeaderUpdate);
    }

    [Fact]
    public async Task Read_WhenSourceLosesDevice_KeepsWhatCameAndPadsShorterChannel()
    {
        var source = new ChunkSource(
            [Chunk(Channel.Me, 0.25f, 1_000), Chunk(Channel.Others, -0.5f, 700)],
            failure: new AudioSourceException("Устройство отключено"));

        await Should.ThrowAsync<AudioSourceException>(ReadAll(source));

        await ShouldHoldMeAndPaddedOthers();
    }

    [Fact]
    public async Task Read_WhenCancelled_KeepsWhatCameAndPadsShorterChannel()
    {
        var source = new ChunkSource([Chunk(Channel.Me, 0.25f, 1_000), Chunk(Channel.Others, -0.5f, 700)], holdOpen: true);
        using var recording = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task reading = Consume(new WavRecordingAudioSource(source, AudioPath), recording.Token);

        await source.Exhausted.WaitAsync(Limit, Token);
        await recording.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(reading.WaitAsync(Limit, Token));

        await ShouldHoldMeAndPaddedOthers();
    }

    public void Dispose() => _folder.Delete(recursive: true);

    private static AudioChunk Chunk(Channel channel, float value, int samples) =>
        new(channel, Enumerable.Repeat(value, samples).ToArray());

    private static async Task Consume(WavRecordingAudioSource source, CancellationToken cancellationToken)
    {
        await foreach (AudioChunk _ in source.Read(cancellationToken))
        {
        }
    }

    private long FramesVisibleToOtherReader()
    {
        // Файл открыт записью: читать с разделением записи, как читает его другой процесс.
        using var stream = new FileStream(AudioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new WaveFileReader(stream);
        return reader.SampleCount;
    }

    private async Task<List<AudioChunk>> ReadAll(IAudioSource inner)
    {
        var passed = new List<AudioChunk>();

        await foreach (AudioChunk chunk in new WavRecordingAudioSource(inner, AudioPath).Read(Token))
        {
            passed.Add(chunk);
        }

        return passed;
    }

    /// <summary>
    /// Файл закрыт и читается: «Я» — 1000 отсчётов, «Собеседники» — 700 и тишина до той же длины.
    /// </summary>
    private async Task ShouldHoldMeAndPaddedOthers()
    {
        (float[] me, float[] others) = await ReadBack();
        me.Length.ShouldBe(1_000);
        me.ShouldAllBe(sample => Math.Abs(sample - 0.25f) < Pcm16Tolerance);
        others[..700].ShouldAllBe(sample => Math.Abs(sample + 0.5f) < Pcm16Tolerance);
        others[700..].ShouldAllBe(sample => sample == 0);
    }

    /// <summary>
    /// Записанный файл читается тем же источником, что и <c>meeting replay</c>.
    /// </summary>
    private async Task<(float[] Me, float[] Others)> ReadBack()
    {
        IAudioSource replay = WavFileAudioSource.Open(AudioPath, speed: 1_000_000, TimeProvider.System).Value;
        var me = new List<float>();
        var others = new List<float>();

        await foreach (AudioChunk chunk in replay.Read(Token))
        {
            (chunk.Channel == Channel.Me ? me : others).AddRange(chunk.Samples.Span);
        }

        return ([.. me], [.. others]);
    }

    /// <summary>
    /// Источник с заданными кусками; удерживаемый после них ждёт отмены, как живой захват, со сбоем — бросает его.
    /// </summary>
    private sealed class ChunkSource(IReadOnlyList<AudioChunk> chunks, bool holdOpen = false, Exception? failure = null)
        : IAudioSource
    {
        private readonly TaskCompletionSource _exhausted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Exhausted => _exhausted.Task;

        public async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (AudioChunk chunk in chunks)
            {
                await Task.Yield();
                yield return chunk;
            }

            _exhausted.TrySetResult();

            if (failure is not null)
            {
                throw failure;
            }

            if (holdOpen)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
