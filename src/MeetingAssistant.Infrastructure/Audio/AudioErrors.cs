using MeetingAssistant.Application.Meetings.Recording;
using MeetingAssistant.SharedKernel;
using NAudio.Wave;

namespace MeetingAssistant.Infrastructure.Audio;

/// <summary>
/// Отказы источников звука: записи с диска и живого захвата.
/// </summary>
public static class AudioErrors
{
    public const string FileNotFoundCode = "Audio.FileNotFound";
    public const string UnsupportedFormatCode = "Audio.UnsupportedFormat";
    public const string UnsupportedDeviceFormatCode = "Audio.UnsupportedDeviceFormat";
    public const string CaptureUnsupportedCode = "Audio.CaptureUnsupported";
    public const string NoDeviceCode = "Audio.NoDevice";

    public static readonly Error CaptureUnsupported =
        new(CaptureUnsupportedCode, "Захват звука встречи работает только в Windows: запустите meeting start там.");

    public static Error NoDevice(string kind) =>
        new(NoDeviceCode, $"В Windows нет устройства {kind} по умолчанию.");

    public static Error FileNotFound(string path) => new(FileNotFoundCode, $"Нет файла записи: {path}.");

    public static Error UnsupportedFormat(string path, WaveFormat format) =>
        new(UnsupportedFormatCode, $"Запись {path} — {format}. {Conversion(path)}");

    public static Error NotWave(string path) =>
        new(UnsupportedFormatCode, $"Запись {path} — не WAV. {Conversion(path)}");

    public static Error UnsupportedDeviceFormat(WaveFormat format) =>
        new(UnsupportedDeviceFormatCode, $"Устройство отдаёт {format}; захват принимает float 32 и PCM 16.");

    private static string Conversion(string path) =>
        "Нужны WAV 16 кГц, моно или стерео, PCM 16 бит или float 32. Перевести с сохранением каналов:\n" +
        $"  ffmpeg -i \"{path}\" -ar {SpeechAudio.SampleRate} -sample_fmt s16 <выход>.wav\n" +
        "Больше двух каналов или нужен один — добавить -ac 1 (всё уйдёт в «Собеседники»).";
}
