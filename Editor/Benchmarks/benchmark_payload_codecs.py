"""Local codec experiment; no HTTP requests or source-file changes.

Run after the Unity suite has finished so CPU workloads do not overlap.
Requires numpy and zstandard. LZ4 is optional via --lz4-library.
The input is an owned scratch payload produced by NativeMeshPipelineBenchmark.
"""
import argparse
import ctypes
import gzip
import hashlib
import io
import json
import platform
import statistics
import time
import zipfile
from pathlib import Path

import numpy as np
import zstandard as zstd


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("payload", type=Path)
    parser.add_argument("key", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--lz4-library", type=Path)
    parser.add_argument("--repeats", type=int, default=3)
    args = parser.parse_args()
    payload, key = args.payload.read_bytes(), args.key.read_bytes()
    digest = hashlib.sha256(payload).hexdigest()
    codecs = [("none", lambda b: b, lambda b: b),
              ("gzip_6", lambda b: gzip.compress(b, compresslevel=6, mtime=0), gzip.decompress)]
    for level in (1, 3, 6, 9, 15):
        codecs.append((f"zstd_{level}", zstd.ZstdCompressor(level=level).compress,
                       zstd.ZstdDecompressor().decompress))
    if args.lz4_library:
        lib = ctypes.CDLL(str(args.lz4_library.resolve()))
        lib.LZ4_compressBound.argtypes = [ctypes.c_int]
        lib.LZ4_compressBound.restype = ctypes.c_int
        lib.LZ4_compress_default.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        lib.LZ4_compress_default.restype = ctypes.c_int
        lib.LZ4_decompress_safe.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
        lib.LZ4_decompress_safe.restype = ctypes.c_int
        lib.LZ4_versionNumber.restype = ctypes.c_int

        def compress_lz4(data):
            bound = lib.LZ4_compressBound(len(data))
            result = ctypes.create_string_buffer(bound)
            length = lib.LZ4_compress_default(data, result, len(data), bound)
            if length <= 0:
                raise RuntimeError("LZ4 compression failed")
            return result.raw[:length]

        def decompress_lz4(data):
            result = ctypes.create_string_buffer(len(payload))
            length = lib.LZ4_decompress_safe(data, result, len(data), len(payload))
            if length != len(payload):
                raise RuntimeError("LZ4 decompression length mismatch")
            return result.raw

        codecs.append(("lz4_block", compress_lz4, decompress_lz4))

    def xor(data):
        values = np.frombuffer(data, dtype=np.uint8)
        repeating_key = np.resize(np.frombuffer(key, dtype=np.uint8), len(values))
        return np.bitwise_xor(values, repeating_key).tobytes()

    def zip_payload(data, method):
        result = io.BytesIO()
        with zipfile.ZipFile(result, "w", compression=method, compresslevel=6 if method == zipfile.ZIP_DEFLATED else None) as archive:
            archive.writestr("payload.bin", data)
        return result.getvalue()

    report = {"python": platform.python_version(), "zstandard": zstd.__version__,
              "lz4_version_number": lib.LZ4_versionNumber() if args.lz4_library else None,
              "payload_bytes": len(payload), "payload_sha256": digest, "repetitions": args.repeats,
              "notes": ["Native codecs through Python; these are not Unity runtime codec timings.",
                        "No real network benchmark. Transfer seconds are payload ZIP bytes * 8 / link bitrate; exclude latency, logic files, and protocol overhead.",
                        "Iteration 0 is excluded warmup. All decoded payloads are SHA-256 verified.",
                        "LZ4 uses a single block and out-of-band uncompressed length; a shipping format needs a header/chunk manifest."], "results": []}
    for name, compress, decompress in codecs:
        row = {"codec": name, "samples": [], "verified": True}
        for iteration in range(args.repeats + 1):
            start = time.perf_counter()
            encoded = compress(payload)
            compress_ms = (time.perf_counter() - start) * 1000
            start = time.perf_counter()
            decoded = decompress(encoded)
            decode_ms = (time.perf_counter() - start) * 1000
            if hashlib.sha256(decoded).hexdigest() != digest:
                raise RuntimeError(f"{name}: decoded payload differs")
            if iteration:
                row["samples"].append({"compress_ms": compress_ms, "decode_ms": decode_ms})
        encrypted = xor(encoded)
        if xor(encrypted) != encoded:
            raise RuntimeError("XOR roundtrip failed")
        row["encoded_bytes"] = len(encoded)
        for method, label in ((zipfile.ZIP_STORED, "stored"), (zipfile.ZIP_DEFLATED, "deflate")):
            start = time.perf_counter()
            archive = zip_payload(encrypted, method)
            row[f"outer_zip_{label}_ms"] = (time.perf_counter() - start) * 1000
            row[f"outer_zip_{label}_bytes"] = len(archive)
            with zipfile.ZipFile(io.BytesIO(archive)) as reader:
                if reader.read("payload.bin") != encrypted:
                    raise RuntimeError("ZIP roundtrip failed")
        row["compress_median_ms"] = statistics.median(s["compress_ms"] for s in row["samples"])
        row["decode_median_ms"] = statistics.median(s["decode_ms"] for s in row["samples"])
        report["results"].append(row)
        args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(name, row["encoded_bytes"], round(row["compress_median_ms"], 2), round(row["decode_median_ms"], 2), flush=True)


if __name__ == "__main__":
    main()
