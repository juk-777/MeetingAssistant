"""tokenizer.model (SentencePiece) -> bpe_vocab для hotwords sherpa-onnx: «кусок<TAB>вес» на строку.

Запуск: python spm2vocab.py <tokenizer.model> > <bpe.vocab>. Без пакета sentencepiece: разбирается
protobuf ModelProto, поле 1 — куски (поле 1 — текст, поле 2 — вес float).
"""
import struct
import sys


def varint(data, i):
    value = shift = 0
    while True:
        byte = data[i]
        i += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            return value, i


def fields(data):
    i = 0
    while i < len(data):
        key, i = varint(data, i)
        number, wire = key >> 3, key & 7
        if wire == 0:
            value, i = varint(data, i)
        elif wire == 1:
            value, i = data[i:i + 8], i + 8
        elif wire == 2:
            size, i = varint(data, i)
            value, i = data[i:i + size], i + size
        elif wire == 5:
            value, i = data[i:i + 4], i + 4
        else:
            sys.exit(f"неизвестный тип поля {wire}")
        yield number, wire, value


model = open(sys.argv[1], "rb").read()
for number, wire, piece in fields(model):
    if number != 1 or wire != 2:
        continue
    text, weight = None, 0.0
    for n, w, value in fields(piece):
        if n == 1 and w == 2:
            text = value.decode("utf-8")
        elif n == 2 and w == 5:
            weight = struct.unpack("<f", value)[0]
    print(f"{text}\t{weight}")
