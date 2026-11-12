using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Пакеты одного устройства захвата в его собственном формате.
/// </summary>
/// <remarks>
/// Шов между WASAPI и источником встречи: всё, кроме самого захвата, проверяется тестами на двойнике.
/// </remarks>
internal interface IDeviceStream
{
    /// <summary>
    /// Канал встречи, который пишет это устройство.
    /// </summary>
    Channel Channel { get; }

    /// <summary>
    /// Имя устройства для человека.
    /// </summary>
    string DeviceName { get; }

    WaveFormat Format { get; }

    /// <summary>
    /// Пакеты до отмены. Потеря устройства — <see cref="AudioSourceException"/>.
    /// </summary>
    IAsyncEnumerable<DevicePacket> Read(CancellationToken cancellationToken);
}

/// <summary>
/// Пакет устройства: целые кадры в формате устройства.
/// </summary>
/// <remarks>
/// Пакеты, помеченные устройством как тишина, <c>WasapiRecorder.CaptureAsync</c> не отдаёт: такой промежуток
/// закрывает тишина по часам источника.
/// </remarks>
/// <param name="Data">Кадры.</param>
internal sealed record DevicePacket(ReadOnlyMemory<byte> Data);
