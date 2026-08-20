"""Minimal PNG (RGBA) decoder for the subset that Ghostscript's `pngalpha`
device emits (D-036).

This is *not* a general-purpose PNG reader. It only accepts colour type 6
(RGBA), bit depth 8, no interlacing, compression method 0, filter method 0
-- the one combination `pngalpha` produces. Anything else is a hard error.
The point of reading PNG at all is to recover the alpha channel (D-035/D-036):
"painted white" (alpha=255) must be distinguishable from "nothing drawn"
(alpha=0), which the existing PPM (P6) pipeline cannot represent.

Deflate/inflate is done with the standard library's `zlib` -- no new
dependency (`ref/requirements.txt` stays PyYAML-only, D-036).
"""

from __future__ import annotations

import struct
import zlib

_PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# The only colour type / bit depth / compression / filter / interlace
# combination this decoder accepts (Ghostscript's pngalpha output).
_SUPPORTED_COLOUR_TYPE = 6  # RGBA
_SUPPORTED_BIT_DEPTH = 8
_SUPPORTED_COMPRESSION_METHOD = 0
_SUPPORTED_FILTER_METHOD = 0
_SUPPORTED_INTERLACE_METHOD = 0

_BYTES_PER_PIXEL = 4  # RGBA, 8-bit


class PngFormatError(ValueError):
    """Unsupported PNG, or a corrupt one (bad CRC, truncated chunk, etc.)."""


def _read_chunks(data: bytes) -> list[tuple[bytes, bytes]]:
    """Split the chunk stream (everything after the 8-byte signature) into
    a list of (chunk_type, chunk_data) pairs, verifying each chunk's CRC-32.
    """
    if len(data) < len(_PNG_SIGNATURE) or data[: len(_PNG_SIGNATURE)] != _PNG_SIGNATURE:
        raise PngFormatError("not a PNG file (bad signature)")

    pos = len(_PNG_SIGNATURE)
    chunks: list[tuple[bytes, bytes]] = []
    while pos < len(data):
        if pos + 8 > len(data):
            raise PngFormatError("truncated PNG: incomplete chunk header")
        length = struct.unpack(">I", data[pos : pos + 4])[0]
        chunk_type = data[pos + 4 : pos + 8]
        chunk_start = pos + 8
        chunk_end = chunk_start + length
        if chunk_end + 4 > len(data):
            raise PngFormatError(
                f"truncated PNG: chunk '{chunk_type!r}' runs past end of file"
            )
        chunk_data = data[chunk_start:chunk_end]
        stored_crc = struct.unpack(">I", data[chunk_end : chunk_end + 4])[0]
        computed_crc = zlib.crc32(chunk_type + chunk_data) & 0xFFFFFFFF
        if stored_crc != computed_crc:
            raise PngFormatError(
                f"corrupt PNG: CRC mismatch in chunk '{chunk_type!r}' "
                f"(stored {stored_crc:#010x}, computed {computed_crc:#010x})"
            )
        chunks.append((chunk_type, chunk_data))
        pos = chunk_end + 4
        if chunk_type == b"IEND":
            break
    else:
        raise PngFormatError("truncated PNG: missing IEND chunk")

    return chunks


def _paeth_predictor(a: int, b: int, c: int) -> int:
    """PNG spec's Paeth predictor. a = left, b = above, c = upper-left."""
    p = a + b - c
    pa = abs(p - a)
    pb = abs(p - b)
    pc = abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def _unfilter(raw: bytes, width: int, height: int) -> bytearray:
    """Reverse the per-row filtering (PNG spec §6). `raw` is the inflated
    IDAT stream: height rows, each row = 1 filter-type byte + width*4 bytes
    of RGBA samples.
    """
    row_bytes = width * _BYTES_PER_PIXEL
    stride = row_bytes + 1
    expected_len = stride * height
    if len(raw) != expected_len:
        raise PngFormatError(
            f"corrupt PNG: expected {expected_len} bytes of unfiltered scanline data, got {len(raw)}"
        )

    out = bytearray(row_bytes * height)
    prev_row = bytearray(row_bytes)  # all-zero "row above the first row"

    for y in range(height):
        row_start = y * stride
        filter_type = raw[row_start]
        cur = bytearray(raw[row_start + 1 : row_start + 1 + row_bytes])

        if filter_type == 0:  # None
            pass
        elif filter_type == 1:  # Sub
            for i in range(_BYTES_PER_PIXEL, row_bytes):
                cur[i] = (cur[i] + cur[i - _BYTES_PER_PIXEL]) & 0xFF
        elif filter_type == 2:  # Up
            for i in range(row_bytes):
                cur[i] = (cur[i] + prev_row[i]) & 0xFF
        elif filter_type == 3:  # Average
            for i in range(row_bytes):
                left = cur[i - _BYTES_PER_PIXEL] if i >= _BYTES_PER_PIXEL else 0
                up = prev_row[i]
                cur[i] = (cur[i] + ((left + up) >> 1)) & 0xFF
        elif filter_type == 4:  # Paeth
            for i in range(row_bytes):
                left = cur[i - _BYTES_PER_PIXEL] if i >= _BYTES_PER_PIXEL else 0
                up = prev_row[i]
                upper_left = (
                    prev_row[i - _BYTES_PER_PIXEL] if i >= _BYTES_PER_PIXEL else 0
                )
                cur[i] = (cur[i] + _paeth_predictor(left, up, upper_left)) & 0xFF
        else:
            raise PngFormatError(
                f"corrupt PNG: unknown filter type {filter_type} on row {y}"
            )

        out[y * row_bytes : (y + 1) * row_bytes] = cur
        prev_row = cur

    return out


def read_png_rgba(path: str) -> tuple[int, int, bytes]:
    """Read a PNG file produced by Ghostscript's `pngalpha` device.

    Returns (width, height, pixels) where pixels is a bytes object of
    length width*height*4, row-major, one byte per R/G/B/A sample.

    Only colour type 6 (RGBA) / bit depth 8 / no interlacing / compression
    method 0 / filter method 0 is supported (the combination `pngalpha`
    produces). Anything else raises PngFormatError (D-036: this is not a
    general-purpose PNG decoder).
    """
    with open(path, "rb") as f:
        data = f.read()

    chunks = _read_chunks(data)

    if not chunks or chunks[0][0] != b"IHDR":
        raise PngFormatError("malformed PNG: first chunk is not IHDR")

    ihdr = chunks[0][1]
    if len(ihdr) != 13:
        raise PngFormatError(f"malformed PNG: IHDR length {len(ihdr)} != 13")

    (
        width,
        height,
        bit_depth,
        colour_type,
        compression_method,
        filter_method,
        interlace_method,
    ) = struct.unpack(">IIBBBBB", ihdr)

    if colour_type != _SUPPORTED_COLOUR_TYPE:
        raise PngFormatError(
            f"unsupported PNG colour type {colour_type}; only colour type 6 (RGBA) is supported (D-036)"
        )
    if bit_depth != _SUPPORTED_BIT_DEPTH:
        raise PngFormatError(
            f"unsupported PNG bit depth {bit_depth}; only 8-bit is supported (D-036)"
        )
    if compression_method != _SUPPORTED_COMPRESSION_METHOD:
        raise PngFormatError(
            f"unsupported PNG compression method {compression_method}; only 0 is supported"
        )
    if filter_method != _SUPPORTED_FILTER_METHOD:
        raise PngFormatError(
            f"unsupported PNG filter method {filter_method}; only 0 is supported"
        )
    if interlace_method != _SUPPORTED_INTERLACE_METHOD:
        raise PngFormatError(
            f"unsupported PNG interlace method {interlace_method}; interlacing is not supported (D-036)"
        )
    if width <= 0 or height <= 0:
        raise PngFormatError(f"malformed PNG: non-positive dimensions {width}x{height}")

    # Concatenate every IDAT chunk before inflating -- Ghostscript splits
    # IDAT into many pieces (47 observed in practice, D-036). Ancillary
    # chunks (iCCP, bKGD, pHYs, tEXt, ...) are simply skipped.
    idat_parts = [
        chunk_data for chunk_type, chunk_data in chunks if chunk_type == b"IDAT"
    ]
    if not idat_parts:
        raise PngFormatError("malformed PNG: no IDAT chunk")
    compressed = b"".join(idat_parts)

    try:
        raw = zlib.decompress(compressed)
    except zlib.error as exc:
        raise PngFormatError(f"corrupt PNG: zlib decompression failed: {exc}") from exc

    pixels = _unfilter(raw, width, height)
    return width, height, bytes(pixels)
