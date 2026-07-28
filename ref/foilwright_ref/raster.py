"""L2 raster: PPM (P6) input -> per-ink 1bit planes.

Internal coordinates are always in dots (DOMAIN.md §4.1); this module
never sees mm.

The ink separation formula reproduces ppmtomd 1.6's ``colcorPlain``
colour-correction path plus its ``ditherNone`` threshold binarisation
(vendor/ppmtomd-1.6/ppmtomd.c:2897, 2933-2937, 3052-3058):

    c = maxval - r
    m = maxval - g
    y = maxval - b
    k = min(c, m, y)
    c -= k; m -= k; y -= k
    bit = 1 if value >= (maxval + 1) // 2 else 0

Only maxval == 255 (8 bits per sample) input is supported; this is what
every golden fixture uses and is what ppmtomd normalises everything to
internally (ppmtomd.c:2063 "let's change everything to 255").
"""

from __future__ import annotations


class PPMError(ValueError):
    """Raised when a file is not a supported binary (P6) PPM."""


def read_ppm(path: str) -> tuple[int, int, bytes]:
    """Read a binary (P6) PPM file.

    Returns (width, height, pixels) where pixels is a bytes object of
    length width*height*3, row-major, one byte per R/G/B sample.
    Only maxval 255 is supported.
    """
    with open(path, "rb") as f:
        data = f.read()

    pos = 0

    def _skip_ws_and_comments(p: int) -> int:
        while True:
            while p < len(data) and data[p : p + 1].isspace():
                p += 1
            if p < len(data) and data[p : p + 1] == b"#":
                nl = data.find(b"\n", p)
                p = len(data) if nl < 0 else nl + 1
                continue
            return p

    def _read_token(p: int) -> tuple[bytes, int]:
        p = _skip_ws_and_comments(p)
        start = p
        while p < len(data) and not data[p : p + 1].isspace():
            p += 1
        return data[start:p], p

    magic, pos = _read_token(pos)
    if magic != b"P6":
        raise PPMError(f"unsupported PPM magic {magic!r}; only P6 is supported")

    width_tok, pos = _read_token(pos)
    height_tok, pos = _read_token(pos)
    maxval_tok, pos = _read_token(pos)
    width = int(width_tok)
    height = int(height_tok)
    maxval = int(maxval_tok)
    if maxval != 255:
        raise PPMError(f"unsupported maxval {maxval}; only 255 is supported")

    # Exactly one whitespace byte separates the header from the binary
    # raster data. It must be consumed literally (not via
    # _skip_ws_and_comments, which would misinterpret arbitrary binary
    # pixel bytes as whitespace/comments to skip).
    if pos >= len(data) or not data[pos : pos + 1].isspace():
        raise PPMError("malformed PPM header: missing whitespace before raster data")
    pos += 1

    pixel_bytes = width * height * 3
    pixels = data[pos : pos + pixel_bytes]
    if len(pixels) != pixel_bytes:
        raise PPMError(
            f"truncated PPM data: expected {pixel_bytes} bytes, got {len(pixels)}"
        )
    return width, height, pixels


def to_planes(
    image: tuple[int, int, bytes], palette: dict[str, str]
) -> dict[str, bytes]:
    """Convert an image to per-ink 1bit planes.

    image: (width, height, pixels) as returned by read_ppm.
    palette: maps ink name -> one of "C", "M", "Y", "K", selecting which
        channel of the ppmtomd-style CMYK separation feeds that ink.
        This dict is supplied by the caller (DOMAIN.md §4.5: ink lists
        are never hardcoded in this module).

    Returns a dict ink name -> bytes: each row is packed MSB-first and
    padded to a byte boundary (row length = ceil(width/8) bytes), rows
    concatenated in image order.
    """
    width, height, pixels = image
    row_bytes = (width + 7) // 8
    planes = {name: bytearray(row_bytes * height) for name in palette}

    for y in range(height):
        row_base = y * width * 3
        plane_row_base = y * row_bytes
        for x in range(width):
            idx = row_base + x * 3
            r = pixels[idx]
            g = pixels[idx + 1]
            b = pixels[idx + 2]
            c = 255 - r
            m = 255 - g
            yv = 255 - b
            k = min(c, m, yv)
            c -= k
            m -= k
            yv -= k
            values = {"C": c, "M": m, "Y": yv, "K": k}
            byte_index = plane_row_base + (x >> 3)
            bit_mask = 0x80 >> (x & 7)
            for name, channel in palette.items():
                if values[channel] >= 128:
                    planes[name][byte_index] |= bit_mask

    return {name: bytes(buf) for name, buf in planes.items()}


def to_planes_magic(
    image: tuple[int, int, bytes], inks: list[dict]
) -> dict[str, bytes]:
    """Convert an image to per-ink 1bit planes using magic-colour matching
    (DOMAIN.md §6, D-015).

    image: (width, height, pixels) as returned by read_ppm.
    inks: a list of ink mappings as returned by config.load_palette,
        already sorted into pass execution order. Each ink must have
        `magic_rgb` (3 ints 0..255), `tolerance` (int >= 0), `order`
        (int), and `auto_undercoat` (bool).

    Matching rule (DOMAIN.md §6.3.2 / D-015):
      - A pixel matches an ink iff |r-mr| <= tolerance and |g-mg| <= tolerance
        and |b-mb| <= tolerance, using integer arithmetic only.
      - If a pixel matches more than one ink, the one with the smallest
        distance (max of the three per-channel deviations) wins; ties are
        broken by ascending `order`, then by position in `inks`.
      - A pixel matching no ink is not set in any plane.
      - Each pixel belongs to at most one (non-undercoat) ink.

    `auto_undercoat` inks (DOMAIN.md §6.2) instead receive the union of
    every pixel assigned to any other ink, plus any pixel that matched
    their own `magic_rgb` directly. At most one ink may set
    `auto_undercoat`; a second one raises ValueError.

    Returns a dict ink name -> bytes, in the same packed format as
    to_planes: each row MSB-first, padded to a byte boundary (row length
    = ceil(width/8) bytes), rows concatenated in image order.
    """
    width, height, pixels = image
    row_bytes = (width + 7) // 8

    undercoat_names = [ink["name"] for ink in inks if ink.get("auto_undercoat")]
    if len(undercoat_names) > 1:
        raise ValueError(
            "auto_undercoat is set on more than one ink: "
            f"{undercoat_names}; this is undefined (DOMAIN.md §6.2)"
        )
    undercoat_name = undercoat_names[0] if undercoat_names else None

    planes = {ink["name"]: bytearray(row_bytes * height) for ink in inks}

    for y in range(height):
        row_base = y * width * 3
        plane_row_base = y * row_bytes
        for x in range(width):
            idx = row_base + x * 3
            r = pixels[idx]
            g = pixels[idx + 1]
            b = pixels[idx + 2]

            best_ink = None
            best_distance = None
            for ink in inks:
                mr, mg, mb = ink["magic_rgb"]
                tolerance = ink["tolerance"]
                dr = r - mr if r > mr else mr - r
                dg = g - mg if g > mg else mg - g
                db = b - mb if b > mb else mb - b
                if dr > tolerance or dg > tolerance or db > tolerance:
                    continue
                distance = dr
                distance = max(distance, dg)
                distance = max(distance, db)
                if best_distance is None or distance < best_distance:
                    best_distance = distance
                    best_ink = ink
                elif distance == best_distance and best_ink is not None:
                    if ink["order"] < best_ink["order"]:
                        best_ink = ink
                    # equal order: earlier position in `inks` already won,
                    # since we only replace on strictly smaller order.

            if best_ink is not None:
                byte_index = plane_row_base + (x >> 3)
                bit_mask = 0x80 >> (x & 7)
                planes[best_ink["name"]][byte_index] |= bit_mask

    if undercoat_name is not None:
        union = bytearray(row_bytes * height)
        for name, buf in planes.items():
            if name == undercoat_name:
                continue
            for i, byte in enumerate(buf):
                union[i] |= byte
        undercoat_buf = planes[undercoat_name]
        for i, byte in enumerate(undercoat_buf):
            union[i] |= byte
        planes[undercoat_name] = union

    return {name: bytes(buf) for name, buf in planes.items()}
