using System.Net;
using System.Security.Cryptography;
using MeetingAssistant.Infrastructure.Speech;
using MeetingAssistant.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace MeetingAssistant.Infrastructure.IntegrationTests.Speech;

public sealed class WhisperModelFileTests : IDisposable
{
    private static readonly byte[] Model = [.. Enumerable.Range(0, 3_000_000).Select(index => (byte)index)];

    private readonly DirectoryInfo _models = Directory.CreateTempSubdirectory("meeting-assistant-models-");

    private readonly ServingHandler _server = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ensure_WhenDownloadMatchesChecksum_PutsModelInPlaceAndLeavesNoDraft()
    {
        WhisperModelFile file = File(Sha256(Model));

        Result result = await file.Ensure(Token);

        result.IsSuccess.ShouldBeTrue();
        (await System.IO.File.ReadAllBytesAsync(file.Path, Token)).ShouldBe(Model);
        System.IO.File.Exists(file.Path + WhisperModelFile.DraftSuffix).ShouldBeFalse();
        _server.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Ensure_WhileDownloading_KeepsModelOutOfPlace()
    {
        WhisperModelFile file = File(Sha256(Model));
        _server.OnRead = () => _server.ModelSeenWhileReading |= System.IO.File.Exists(file.Path);

        await file.Ensure(Token);

        _server.ModelSeenWhileReading.ShouldBeFalse();
    }

    [Fact]
    public async Task Ensure_WhenChecksumDiffers_RefusesAndLeavesNeitherModelNorDraft()
    {
        WhisperModelFile file = File(Sha256([1, 2, 3]));

        Result result = await file.Ensure(Token);

        result.Error.Code.ShouldBe(SpeechErrors.ModelChecksumMismatchCode);
        _models.GetFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ensure_WhenServerFails_RefusesAndLeavesNothing()
    {
        _server.Status = HttpStatusCode.NotFound;
        WhisperModelFile file = File(Sha256(Model));

        Result result = await file.Ensure(Token);

        result.Error.Code.ShouldBe(SpeechErrors.ModelDownloadFailedCode);
        _models.GetFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ensure_WhenCancelledMidDownload_LeavesNothing()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        WhisperModelFile file = File(Sha256(Model));
        _server.OnRead = cancellation.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(file.Ensure(cancellation.Token));

        _models.GetFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ensure_WhenModelInPlace_DoesNotDownload()
    {
        WhisperModelFile file = File(Sha256(Model));
        await System.IO.File.WriteAllBytesAsync(file.Path, [7], Token);

        Result result = await file.Ensure(Token);

        result.IsSuccess.ShouldBeTrue();
        _server.Requests.ShouldBe(0);
    }

    public void Dispose()
    {
        _server.Dispose();
        _models.Delete(recursive: true);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private WhisperModelFile File(string sha256) => new(
        _models.FullName,
        new WhisperModelSource("model.bin", new Uri("https://models.test/model.bin"), sha256),
        new HttpClient(_server, disposeHandler: false),
        NullLogger<WhisperModelFile>.Instance);

    /// <summary>
    /// Отдаёт <see cref="Model"/> кусками; на каждом чтении зовёт <see cref="OnRead"/>.
    /// </summary>
    private sealed class ServingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public Action? OnRead { get; set; }

        public bool ModelSeenWhileReading { get; set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            HttpContent content = Status == HttpStatusCode.OK
                ? new StreamContent(new ObservedStream(Model, () => OnRead?.Invoke()))
                : new ByteArrayContent([]);
            content.Headers.ContentLength = Status == HttpStatusCode.OK ? Model.Length : 0;

            return Task.FromResult(new HttpResponseMessage(Status) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>
    /// Поток в памяти, отдающий не больше 64 КБ за чтение и замечающий отмену, как сетевой.
    /// </summary>
    private sealed class ObservedStream(byte[] bytes, Action onRead) : MemoryStream(bytes, writable: false)
    {
        private const int MaxRead = 64 * 1024;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            cancellationToken.ThrowIfCancellationRequested();
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, MaxRead)], cancellationToken);
        }
    }
}
