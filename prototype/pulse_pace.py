"""Темп записи с сервера звука Linux через libpulse-simple — так, как её читает приложение.

Открывает устройство в s16le 16 кГц моно и читает пакеты по 100 мс, как `PulseDeviceStream`. Для каждого пакета
снимает ожидание чтения и задержку `pa_simple_get_latency`. Кусок записи (`fragsize`) — длина пакета, как в
приложении, или выбор сервера (`server`).

    python3 pulse_pace.py <устройство> <пакетов> <app|server>
    python3 pulse_pace.py @DEFAULT_MONITOR@ 200 server

Нужна libpulse-simple.so.0 (пакет libpulse0) и сервер звука; в WSL — WSLg, PULSE_SERVER=unix:/mnt/wslg/PulseServer.
"""

import ctypes
import sys
import time

PACKET_BYTES = 3200  # 100 мс s16le 16 кГц моно — PulseDeviceStream.PacketBytes
PA_STREAM_RECORD = 2
PA_SAMPLE_S16LE = 3
SERVER_DEFAULT = 0xFFFFFFFF


class SampleSpec(ctypes.Structure):
    _fields_ = [("format", ctypes.c_int), ("rate", ctypes.c_uint32), ("channels", ctypes.c_uint8)]


class BufferAttr(ctypes.Structure):
    _fields_ = [(name, ctypes.c_uint32) for name in ("maxlength", "tlength", "prebuf", "minreq", "fragsize")]


def main():
    device, packets, mode = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    lib = ctypes.CDLL("libpulse-simple.so.0")
    lib.pa_simple_new.restype = ctypes.c_void_p
    lib.pa_simple_new.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_char_p,
                                  ctypes.POINTER(SampleSpec), ctypes.c_void_p, ctypes.POINTER(BufferAttr),
                                  ctypes.POINTER(ctypes.c_int)]
    lib.pa_simple_read.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_int)]
    lib.pa_simple_get_latency.restype = ctypes.c_uint64
    lib.pa_simple_get_latency.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int)]
    lib.pa_simple_free.argtypes = [ctypes.c_void_p]

    error = ctypes.c_int(0)
    spec = SampleSpec(PA_SAMPLE_S16LE, 16000, 1)
    fragsize = PACKET_BYTES if mode == "app" else SERVER_DEFAULT
    attr = BufferAttr(SERVER_DEFAULT, SERVER_DEFAULT, SERVER_DEFAULT, SERVER_DEFAULT, fragsize)
    stream = lib.pa_simple_new(None, b"pulse_pace", PA_STREAM_RECORD, device.encode(), b"pulse_pace",
                               ctypes.byref(spec), None, ctypes.byref(attr), ctypes.byref(error))
    if not stream:
        sys.exit(f"сервер не открыл {device}: ошибка {error.value}")

    buffer = ctypes.create_string_buffer(PACKET_BYTES)
    waits, latencies = [], []
    started = last = time.perf_counter()
    for _ in range(packets):
        if lib.pa_simple_read(stream, buffer, PACKET_BYTES, ctypes.byref(error)) < 0:
            sys.exit(f"чтение: ошибка {error.value}")
        now = time.perf_counter()
        waits.append(now - last)
        last = now
        latencies.append(lib.pa_simple_get_latency(stream, ctypes.byref(error)) / 1000)
    lib.pa_simple_free(stream)

    # Первое чтение отдаёт накопленное до него — в ожидание не идёт.
    steady = sorted(waits[1:])
    print(f"{device} {mode}: пакетов {packets} по 100 мс за {last - started:.2f} с; "
          f"ожидание чтения p50 {steady[len(steady) // 2]:.3f} / max {steady[-1]:.3f} с; "
          f"задержка {min(latencies):.0f}–{max(latencies):.0f} мс")


if __name__ == "__main__":
    main()
