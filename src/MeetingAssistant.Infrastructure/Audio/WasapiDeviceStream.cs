using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Пакеты устройства Windows через WASAPI в общем режиме: микрофон или loopback устройства вывода.
/// </summary>
/// <remarks>
/// Проверяется только живым проходом: в тестах шов <see cref="IDeviceStream"/> подменён двойником.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WasapiDeviceStream : IDeviceStream
{
    private readonly MMDevice _device;
    private readonly bool _loopback;

    public WasapiDeviceStream(Channel channel, MMDevice device, bool loopback)
    {
        Channel = channel;
        _device = device;
        _loopback = loopback;
        DeviceName = device.FriendlyName;

        // Без явного формата WASAPI в общем режиме отдаёт формат микшера устройства.
        using AudioClient client = device.CreateAudioClient();
        Format = client.MixFormat;
    }

    public Channel Channel { get; }

    public string DeviceName { get; }

    public WaveFormat Format { get; }

    public async IAsyncEnumerable<DevicePacket> Read([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await using WasapiRecorder recorder = BuildOrDeviceLost();
            await using IAsyncEnumerator<AudioBuffer> buffers = recorder.CaptureAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

            while (await NextOrDeviceLost(buffers, cancellationToken))
            {
                AudioBuffer buffer = buffers.Current;
                long? capturedAt = buffer.Flags.HasFlag(AudioClientBufferFlags.TimestampError)
                    ? null
                    : DeviceClock.ToTimestamp(buffer.QPCPosition, Stopwatch.Frequency);
                yield return new DevicePacket(buffer.Data, capturedAt);
            }
        }
        finally
        {
            _device.Dispose();
        }
    }

    // Граница с устройством: любой отказ захвата — потеря устройства, записанное цело. Аудиоклиент
    // создаётся при сборке, а инициализируется на первом пакете — отказ возможен и там, и там.
    private WasapiRecorder BuildOrDeviceLost()
    {
        try
        {
            WasapiRecorderBuilder builder = new WasapiRecorderBuilder().WithDevice(_device);
            return (_loopback ? builder.WithLoopbackCapture() : builder).Build();
        }
        catch (Exception exception)
        {
            throw DeviceLost(exception);
        }
    }

    private async Task<bool> NextOrDeviceLost(IAsyncEnumerator<AudioBuffer> buffers, CancellationToken cancellationToken)
    {
        try
        {
            return await buffers.MoveNextAsync();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw DeviceLost(exception);
        }
    }

    private AudioSourceException DeviceLost(Exception exception) =>
        new($"Устройство «{DeviceName}»: {exception.Message}", exception);
}
