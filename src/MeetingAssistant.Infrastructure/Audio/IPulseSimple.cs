namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Запись с сервера звука Linux через <c>libpulse-simple</c>: PulseAudio или PipeWire с <c>pipewire-pulse</c>.
/// </summary>
/// <remarks>
/// Шов между библиотекой и потоком устройства: всё, кроме самих вызовов библиотеки, проверяется тестами на двойнике.
/// Запись всегда в формате <see cref="PulseDeviceStream.DeviceFormat"/>: частоту и каналы переводит сервер.
/// </remarks>
internal interface IPulseSimple
{
    /// <summary>
    /// Загружает библиотеку. <c>false</c> — её нет в системе или ОС не Linux.
    /// </summary>
    bool Load();

    /// <summary>
    /// Открывает запись устройства.
    /// </summary>
    /// <param name="device">Имя источника сервера: <c>@DEFAULT_SOURCE@</c>, <c>@DEFAULT_MONITOR@</c>.</param>
    /// <param name="streamName">Имя записи, которое сервер показывает человеку.</param>
    /// <param name="stream">Открытая запись; освобождается <see cref="Free"/>.</param>
    /// <param name="error">Причина отказа словами сервера.</param>
    bool TryOpen(string device, string streamName, out nint stream, out string error);

    /// <summary>
    /// Заполняет буфер целиком; блокирует, пока сервер не отдаст столько звука.
    /// </summary>
    bool TryRead(nint stream, Span<byte> data, out string error);

    /// <summary>
    /// Насколько звук, который отдаст следующее чтение, старше текущего момента. Нет — сервер не знает.
    /// </summary>
    TimeSpan? Latency(nint stream);

    void Free(nint stream);
}
