using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.Domain.Meetings;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Пакеты одного устройства захвата в его собственном формате.
/// </summary>
/// <remarks>
/// Шов между захватом ОС (WASAPI, сервер звука Linux) и источником встречи: всё, кроме самого захвата, проверяется
/// тестами на двойнике.
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
/// источник видит разрывом по времени устройства и ставит перед следующим пакетом тишину.
/// </remarks>
/// <param name="Data">Кадры.</param>
/// <param name="CapturedAt">
/// Когда устройство записало первый кадр — отметка часов встречи. WASAPI даёт счётчик производительности, это
/// те же часы, что у <see cref="TimeProvider.System"/>, поэтому часы встречи у живого захвата — только системные;
/// сервер звука Linux даёт задержку, и отметка считается от прихода. Нет — устройство не знает, и пакет считается
/// кончившимся в момент прихода.
/// </param>
internal sealed record DevicePacket(ReadOnlyMemory<byte> Data, long? CapturedAt);
