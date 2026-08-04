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

``to_planes`` also supports ppmtomd's ``Halftone`` and ``CoarseHalftone``
ordered-dither modes (DOMAIN.md §4.2.1). Both replace the flat 128
threshold above with a per-pixel threshold read from a small dither
matrix, using a rotated-screen position that advances across each row
(vendor/ppmtomd-1.6/ppmtomd.c:2851-3093, the active ``ht_init``/``ht_inc``
macros at ppmtomd.c:549-613 -- the ``#if 0`` block above them at
ppmtomd.c:517-548 is dead code and is not reproduced here):

    bit = 1 if value > dither_matrix[hrow, hcol] else 0

Only maxval == 255 (8 bits per sample) input is supported; this is what
every golden fixture uses and is what ppmtomd normalises everything to
internally (ppmtomd.c:2063 "let's change everything to 255").
"""

from __future__ import annotations

_VALID_HALFTONES = frozenset({"none", "halftone", "coarse_halftone"})

# ppmtomd.c:986 "four" -- the 2x2 quadrant interpolation weights used by
# build_dith to expand each n x n cell into a 2x2 block of finer values.
_BUILD_DITH_FOUR = (0, 2, 3, 1)

# ppmtomd.c:666-673 -- fine ("Halftone") dither, input matrix for the C/M
# "line" screen.
_DITHMAT6LINE = (
    56 * 4,
    48 * 4,
    52 * 4,
    58 * 4,
    50 * 4,
    54 * 4,
    32 * 4,
    24 * 4,
    28 * 4,
    34 * 4,
    26 * 4,
    30 * 4,
    8 * 4,
    0 * 4,
    4 * 4,
    10 * 4,
    2 * 4,
    6 * 4,
    20 * 4,
    12 * 4,
    16 * 4,
    22 * 4,
    14 * 4,
    18 * 4,
    44 * 4,
    36 * 4,
    40 * 4,
    46 * 4,
    38 * 4,
    42 * 4,
    63 * 4,
    59 * 4,
    61 * 4,
    63 * 4,
    60 * 4,
    62 * 4,
)

# ppmtomd.c:693-701 -- fine ("Halftone") dither, input matrix for the Y/K
# "dot" screen. This is the active `#if 1` branch of a dead `#if`/`#else`;
# the `#else` branch (ppmtomd.c:702-721, which contains a stray 254
# literal) never compiles and is not reproduced here.
_DITHMAT6DOT = (
    100,
    40,
    140,
    176,
    222,
    144,
    20,
    0,
    60,
    234,
    246,
    208,
    160,
    80,
    120,
    128,
    192,
    160,
    184,
    228,
    152,
    110,
    50,
    150,
    240,
    252,
    216,
    30,
    10,
    70,
    136,
    200,
    168,
    170,
    90,
    130,
)

# ppmtomd.c:737-746 -- coarse dither matrix, used directly (unexpanded,
# by all four components) for CoarseHalftone. This is the active `#else`
# branch of the `#if 0` at ppmtomd.c:725.
_DITHMAT10 = (
    27 * 4,
    19 * 4,
    15 * 4,
    23 * 4,
    31 * 4,
    41 * 4,
    52 * 4,
    55 * 4,
    49 * 4,
    37 * 4,
    25 * 4,
    10 * 4,
    4 * 4,
    12 * 4,
    21 * 4,
    43 * 4,
    58 * 4,
    62 * 4,
    60 * 4,
    48 * 4,
    17 * 4,
    2 * 4,
    0 * 4,
    6 * 4,
    18 * 4,
    53 * 4,
    64 * 4,
    64 * 4,
    64 * 4,
    54 * 4,
    22 * 4,
    13 * 4,
    8 * 4,
    14 * 4,
    26 * 4,
    47 * 4,
    61 * 4,
    63 * 4,
    59 * 4,
    45 * 4,
    33 * 4,
    24 * 4,
    16 * 4,
    20 * 4,
    29 * 4,
    35 * 4,
    50 * 4,
    56 * 4,
    51 * 4,
    39 * 4,
    42 * 4,
    52 * 4,
    55 * 4,
    49 * 4,
    38 * 4,
    28 * 4,
    19 * 4,
    15 * 4,
    23 * 4,
    32 * 4,
    44 * 4,
    58 * 4,
    62 * 4,
    60 * 4,
    48 * 4,
    25 * 4,
    11 * 4,
    5 * 4,
    12 * 4,
    21 * 4,
    53 * 4,
    64 * 4,
    64 * 4,
    64 * 4,
    54 * 4,
    17 * 4,
    3 * 4,
    1 * 4,
    7 * 4,
    18 * 4,
    47 * 4,
    61 * 4,
    63 * 4,
    59 * 4,
    46 * 4,
    22 * 4,
    13 * 4,
    9 * 4,
    14 * 4,
    26 * 4,
    36 * 4,
    50 * 4,
    57 * 4,
    51 * 4,
    40 * 4,
    34 * 4,
    24 * 4,
    16 * 4,
    20 * 4,
    30 * 4,
)


def _round_half_even_div(numerator: int, denom: int) -> int:
    """Integer equivalent of C's ``rint(numerator / denom)`` (round to
    nearest, ties to even).

    ppmtomd's build_dith (ppmtomd.c:987) uses floating-point ``rint`` for
    this, but it is only ever applied to a handful of compile-time
    constants to build the fixed dither-matrix tables below -- never to
    per-pixel data. Replicating it here with exact integer arithmetic
    (instead of floats) produces the identical table while keeping every
    per-pixel computation in this module free of floating point, per
    DOMAIN.md §4.9 / D-015.
    """
    quot, rem = divmod(numerator, denom)  # denom > 0 here, so this floors
    twice_rem = 2 * rem
    if twice_rem < denom:
        return quot
    if twice_rem > denom:
        return quot + 1
    return quot if quot % 2 == 0 else quot + 1


def _build_dith(
    n: int, indith: tuple[int, ...], m: int, condith: tuple[int, ...]
) -> tuple[int, ...]:
    """Port of ppmtomd's build_dith (ppmtomd.c:958-997).

    Expands an n x n dither matrix into an (m*n) x (m*n) matrix: each
    output cell interpolates between its source cell's value and the
    next-higher distinct value found anywhere in the source matrix (256
    if there is none), weighted by the m x m condith quadrant pattern.
    Results are clamped to 254 so solid colour always prints as such.
    """
    sorted_vals = sorted(indith)
    result = [0] * (m * n * m * n)
    for i in range(n):
        for j in range(n):
            val = indith[i * n + j]
            k = sorted_vals.index(val)
            while k < n * n and sorted_vals[k] == val:
                k += 1
            nval = 256 if k == n * n else sorted_vals[k]
            for p in range(m):
                for q in range(m):
                    weight = condith[p * m + q]
                    numerator = (m * m - weight) * val + weight * nval
                    res = _round_half_even_div(numerator, m * m)
                    res = min(res, 254)
                    result[(n * p + i) * m * n + (n * q + j)] = res
    return tuple(result)


# Precomputed once at import time (see _build_dith docstring for why this
# is safe to do with the round-half-even helper above rather than floats).
_DITHMAT_LINE12 = _build_dith(6, _DITHMAT6LINE, 2, _BUILD_DITH_FOUR)
_DITHMAT_DOT12 = _build_dith(6, _DITHMAT6DOT, 2, _BUILD_DITH_FOUR)

# ppmtomd.c:1978-1992 -- default halftone screen angles (x, y, z, yneg)
# per CMYK component. y is always the abs() of the value passed to htset;
# a negative value only sets yneg (ppmtomd.c:491-493 htset macro).
_SCREEN_HALFTONE = {
    "C": (12, 5, 13, False),
    "M": (12, 5, 13, True),
    "Y": (3, 4, 5, False),
    "K": (1, 0, 1, False),
}
_SCREEN_COARSE_HALFTONE = {
    "C": (12, 5, 13, False),
    "M": (5, 12, 13, True),  # ppmtomd.c:1991 coarse-mode override
    "Y": (3, 4, 5, False),
    "K": (1, 0, 1, False),
}

# Per halftone mode: for each CMYK channel, (cellsize, dither matrix); plus
# the screen-angle table to use (ppmtomd.c:2761-2779, the "normal 600dpi
# mode" branch -- this project never drives the 1200dpi "photo-realistic"
# or vphoto paths, so those branches are not reproduced).
_HALFTONE_MODES = {
    "halftone": {
        "C": (12, _DITHMAT_LINE12),
        "M": (12, _DITHMAT_LINE12),
        "Y": (12, _DITHMAT_DOT12),
        "K": (12, _DITHMAT_DOT12),
        "screens": _SCREEN_HALFTONE,
    },
    "coarse_halftone": {
        "C": (10, _DITHMAT10),
        "M": (10, _DITHMAT10),
        "Y": (10, _DITHMAT10),
        "K": (10, _DITHMAT10),
        "screens": _SCREEN_COARSE_HALFTONE,
    },
}


def _cdiv(a: int, b: int) -> int:
    """C-style integer division: truncate toward zero. ``b`` must be > 0
    (always true here: callers only ever pass ``2 * y`` or ``2 * z`` with
    y, z > 0)."""
    return a // b if a >= 0 else -((-a) // b)


def _ht_row_positions(
    x: int, y: int, z: int, yneg: bool, row: int, cellsize: int, width: int
) -> list[tuple[int, int]]:
    """Reproduce ppmtomd's ht_init/ht_inc macros for one image row.

    Returns the (hrow, hcol) dither-matrix index to use for each column
    0..width-1, in order (ppmtomd.c:549-613, the active branch -- the
    incremental-rotation branch above it at ppmtomd.c:517-548 is guarded
    by ``#if 0`` and is dead code).

    ppmtomd calls ht_init once per row then ht_inc once per column,
    reading ht_elt (the matrix lookup) *before* each ht_inc call
    (ppmtomd.c:2851-2864, 3069-3092); this returns that same
    before-increment sequence of positions directly.

    Every value here is a plain Python int; C's truncating ``/`` is
    replicated via ``_cdiv``, while C's ``%=`` followed by a manual
    "+= cellsize if negative" correction is mathematically identical to
    Python's ``%`` for any integer and any positive modulus (a remainder's
    magnitude is always smaller than the modulus, so a single correction
    always suffices) -- so the final normalisations below just use ``%``.
    """
    positions: list[tuple[int, int]] = []

    if y == 0:
        # ppmtomd.c:556 -- no rotation: hrow is fixed for the row, hcol
        # just counts up from 0, so position col is simply col % cellsize.
        hrow = row % cellsize
        return [(hrow, col % cellsize) for col in range(width)]

    # ht_init (ppmtomd.c:557-576)
    row_eff = (10000 - row) if yneg else row
    s1xf = 2 * row_eff * (x - z)
    s1xi = _cdiv(s1xf - y + 1, 2 * y)
    s1yi = row_eff
    s2xi = s1xi
    s2yf = 2 * y * s1xi + 2 * z * s1yi
    if s2yf >= 0:
        s2yi = _cdiv(s2yf + z, 2 * z)
    else:
        s2yi = _cdiv(s2yf + 1 - z, 2 * z)
    s2yf -= 2 * z * s2yi
    s3xf = 2 * y * s2xi + 2 * (x - z) * s2yi
    if s3xf >= 0:
        hcol = _cdiv(s3xf + y, 2 * y)
    else:
        hcol = _cdiv(s3xf - y + 1, 2 * y)
    s3xf -= 2 * y * hcol
    hrow = s2yi

    for _ in range(width):
        positions.append((hrow % cellsize, hcol % cellsize))

        # ht_inc (ppmtomd.c:580-612). s1xi/s2xi are incremented in the
        # source but never read again afterwards, so they are omitted
        # here (dead state).
        s2yf += 2 * y
        if s2yf >= z:
            s2yi += 1
            s2yf -= 2 * z
            hcol += 1
            s3xf += 2 * (x - z)
            while s3xf < -y:
                hcol -= 1
                s3xf += 2 * y
            hrow += 1
        elif s2yf >= -z:
            hcol += 1
        else:
            s2yi -= 1
            s2yf += 2 * z
            hcol -= 1
            s3xf -= 2 * (x - z)
            while s3xf >= y:
                hcol += 1
                s3xf -= 2 * y
            hrow -= 1

    return positions


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
    image: tuple[int, int, bytes],
    palette: dict[str, str],
    halftone: str = "none",
) -> dict[str, bytes]:
    """Convert an image to per-ink 1bit planes.

    image: (width, height, pixels) as returned by read_ppm.
    palette: maps ink name -> one of "C", "M", "Y", "K", selecting which
        channel of the ppmtomd-style CMYK separation feeds that ink.
        This dict is supplied by the caller (DOMAIN.md §4.5: ink lists
        are never hardcoded in this module).
    halftone: one of "none" (flat 128 threshold, ppmtomd's ditherNone --
        the default, and byte-identical to this function's behaviour
        before halftoning was added), "halftone" (ppmtomd's -dither
        Halftone) or "coarse_halftone" (ppmtomd's -dither CoarseHalftone).
        FloydSteinberg and Square are not implemented (DOMAIN.md §4.2.1).

    Returns a dict ink name -> bytes: each row is packed MSB-first and
    padded to a byte boundary (row length = ceil(width/8) bytes), rows
    concatenated in image order.
    """
    if halftone not in _VALID_HALFTONES:
        raise ValueError(
            f"unknown halftone mode {halftone!r}; expected one of "
            f"{sorted(_VALID_HALFTONES)}"
        )

    width, height, pixels = image
    row_bytes = (width + 7) // 8
    planes = {name: bytearray(row_bytes * height) for name in palette}

    mode = _HALFTONE_MODES.get(halftone)
    channels_needed = set(palette.values()) if mode is not None else ()

    for y in range(height):
        row_base = y * width * 3
        plane_row_base = y * row_bytes

        # channel -> (positions for this row, dither matrix, cellsize)
        row_halftone: dict[str, tuple[list[tuple[int, int]], tuple[int, ...], int]] = {}
        if mode is not None:
            for channel in channels_needed:
                cellsize, matrix = mode[channel]
                sx, sy, sz, syneg = mode["screens"][channel]
                row_halftone[channel] = (
                    _ht_row_positions(sx, sy, sz, syneg, y, cellsize, width),
                    matrix,
                    cellsize,
                )

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
                value = values[channel]
                if mode is None:
                    hit = value >= 128
                else:
                    positions, matrix, cellsize = row_halftone[channel]
                    hrow, hcol = positions[x]
                    threshold = matrix[cellsize * hrow + hcol]
                    hit = value > threshold
                if hit:
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

    # プロセスインク(CMYK 分解の受け皿)はマジックカラーの対象ではない。
    # パレット全体を渡されても特色だけを見る(D-019)。
    inks = [ink for ink in inks if ink.get("magic_rgb") is not None]

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


def to_planes_auto(
    image: tuple[int, int, bytes],
    inks: list[dict],
    cmyk_map: dict[str, str],
    halftone: str = "none",
) -> dict[str, bytes]:
    """Convert an image to per-ink 1bit planes using the ``auto`` ink
    specification method (DOMAIN.md §6.6): spot colours and CMYK
    separation coexist on the same page, decided per pixel.

    image: (width, height, pixels) as returned by read_ppm.
    inks: a list of ink mappings as returned by config.load_palette,
        already sorted into pass execution order (same shape as
        to_planes_magic's ``inks`` argument).
    cmyk_map: maps a CMYK channel ("C"/"M"/"Y"/"K") to the ink name that
        receives that channel's plane. Note this is the inverse
        direction of to_planes's ``palette`` argument (channel -> name,
        not name -> channel), matching how callers already have a fixed
        set of process-colour ink names to fill in.
    halftone: forwarded to the CMYK-separation half of the algorithm;
        see to_planes for the meaning of "none"/"halftone"/
        "coarse_halftone".

    Per-pixel rule (DOMAIN.md §6.6):
      1. Try to match the pixel against a spot ink using the same rule
         as to_planes_magic (DOMAIN.md §6.3.2 / D-015): integer-only,
         per-channel tolerance, closest match wins ties broken by
         ascending `order` then by position in `inks`.
      2. If it matches a spot ink, it belongs to that ink's plane only
         (DOMAIN.md §4.3: one pass = one cartridge -- it is never also
         fed into CMYK separation).
      3. Otherwise it is fed into the CMYK separation formula (identical
         to to_planes's colcorPlain + ditherNone/Halftone/CoarseHalftone
         logic) and set in the plane named by `cmyk_map` for its
         dominant channel(s).
      4. `auto_undercoat` (at most one ink, same restriction as
         to_planes_magic) is computed last, as the union of every other
         plane -- both spot and CMYK -- plus any pixel that matched the
         undercoat ink's own `magic_rgb` directly.

    Returns a dict ink name -> bytes (spot ink names from `inks` plus
    process ink names from `cmyk_map`), in the same packed format as
    to_planes / to_planes_magic: each row MSB-first, padded to a byte
    boundary (row length = ceil(width/8) bytes), rows concatenated in
    image order.
    """
    if halftone not in _VALID_HALFTONES:
        raise ValueError(
            f"unknown halftone mode {halftone!r}; expected one of "
            f"{sorted(_VALID_HALFTONES)}"
        )

    width, height, pixels = image
    row_bytes = (width + 7) // 8

    # プロセスインク(CMYK 分解の受け皿)はマジックカラーの対象ではない。
    # パレット全体を渡されても特色だけを見る(D-019)。
    inks = [ink for ink in inks if ink.get("magic_rgb") is not None]

    undercoat_names = [ink["name"] for ink in inks if ink.get("auto_undercoat")]
    if len(undercoat_names) > 1:
        raise ValueError(
            "auto_undercoat is set on more than one ink: "
            f"{undercoat_names}; this is undefined (DOMAIN.md §6.2)"
        )
    undercoat_name = undercoat_names[0] if undercoat_names else None

    spot_planes = {ink["name"]: bytearray(row_bytes * height) for ink in inks}
    cmyk_planes = {name: bytearray(row_bytes * height) for name in cmyk_map.values()}

    mode = _HALFTONE_MODES.get(halftone)
    # cmyk_map keys are already the CMYK channels ("C"/"M"/"Y"/"K"), unlike
    # to_planes's `palette` where the channels are the *values* -- so here
    # channels_needed is simply the key set.
    channels_needed = set(cmyk_map.keys()) if mode is not None else ()

    for y in range(height):
        row_base = y * width * 3
        plane_row_base = y * row_bytes

        # channel -> (positions for this row, dither matrix, cellsize)
        # -- identical precomputation to to_planes's per-row halftone
        # setup, duplicated here rather than shared so this function has
        # no dependency on to_planes's internal state beyond the shared
        # module-level tables/helpers above.
        row_halftone: dict[str, tuple[list[tuple[int, int]], tuple[int, ...], int]] = {}
        if mode is not None:
            for channel in channels_needed:
                cellsize, matrix = mode[channel]
                sx, sy, sz, syneg = mode["screens"][channel]
                row_halftone[channel] = (
                    _ht_row_positions(sx, sy, sz, syneg, y, cellsize, width),
                    matrix,
                    cellsize,
                )

        for x in range(width):
            idx = row_base + x * 3
            r = pixels[idx]
            g = pixels[idx + 1]
            b = pixels[idx + 2]

            # Step 1: try to match a spot ink. This is the same matching
            # rule as to_planes_magic's inner loop (DOMAIN.md §6.3.2 /
            # D-015): duplicated here (rather than shared) to keep
            # to_planes_magic's implementation untouched; any change to
            # one must be mirrored in the other to avoid divergence.
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
                    # equal order: earlier position in `inks` already
                    # won, since we only replace on strictly smaller
                    # order.

            byte_index = plane_row_base + (x >> 3)
            bit_mask = 0x80 >> (x & 7)

            if best_ink is not None:
                # Step 2: spot match -- this pixel belongs to that ink's
                # plane only, never CMYK (DOMAIN.md §4.3).
                spot_planes[best_ink["name"]][byte_index] |= bit_mask
                continue

            # Step 3: no spot match -- fall through to CMYK separation.
            # This formula is identical to to_planes's colcorPlain +
            # threshold/halftone logic (duplicated here rather than
            # shared so to_planes's implementation stays untouched; any
            # change to one must be mirrored in the other to avoid
            # divergence -- see to_planes's module docstring for the
            # ppmtomd.c line references this reproduces).
            c = 255 - r
            m = 255 - g
            yv = 255 - b
            k = min(c, m, yv)
            c -= k
            m -= k
            yv -= k
            values = {"C": c, "M": m, "Y": yv, "K": k}
            for channel, name in cmyk_map.items():
                value = values[channel]
                if mode is None:
                    hit = value >= 128
                else:
                    positions, matrix, cellsize = row_halftone[channel]
                    hrow, hcol = positions[x]
                    threshold = matrix[cellsize * hrow + hcol]
                    hit = value > threshold
                if hit:
                    cmyk_planes[name][byte_index] |= bit_mask

    # Step 4: auto_undercoat is the union of every other plane (spot and
    # CMYK alike), computed only after both are fully determined, plus
    # any pixel that matched the undercoat ink's own magic_rgb directly
    # (already present in spot_planes[undercoat_name] from step 1/2).
    if undercoat_name is not None:
        union = bytearray(row_bytes * height)
        for name, buf in spot_planes.items():
            if name == undercoat_name:
                continue
            for i, byte in enumerate(buf):
                union[i] |= byte
        for buf in cmyk_planes.values():
            for i, byte in enumerate(buf):
                union[i] |= byte
        undercoat_buf = spot_planes[undercoat_name]
        for i, byte in enumerate(undercoat_buf):
            union[i] |= byte
        spot_planes[undercoat_name] = union

    result = {name: bytes(buf) for name, buf in spot_planes.items()}
    result.update({name: bytes(buf) for name, buf in cmyk_planes.items()})
    return result


def to_planes_per_page(
    images: list[tuple[int, int, bytes]],
    page_inks: list[str],
) -> dict[str, bytes]:
    """Convert a multi-page document to per-ink 1bit planes using the
    ``per_page`` ink specification method (DOMAIN.md §6.4.1 / §6.6).

    One page carries one ink, so no colour matching happens here -- the
    assignment is given, not guessed. That is exactly why this method is
    preferred when the artwork is already separated into layers: there is
    nothing to mis-detect.

    images: one (width, height, pixels) tuple per page, as returned by
        read_ppm. All pages must share the same dimensions; they are
        printed onto the same sheet and must register with each other.
    page_inks: ink name for each page, positionally. Length must match
        ``images``.

    Each page is binarised on its black (K) component, using the same
    formula as to_planes: the dark parts of the page get printed in that
    page's ink. This matches how the artwork is prepared in practice --
    a figure meant to print in white is drawn as solid black (§10.9.3).

    Returns a dict ink name -> bytes in the same packed format as the
    other entry points. Pass order is decided later from the palette's
    ``order`` (§4.3), not from page order.
    """
    if not images:
        raise ValueError("per_page needs at least one page")
    if len(images) != len(page_inks):
        raise ValueError(
            f"page/ink count mismatch: {len(images)} pages, {len(page_inks)} inks"
        )

    duplicates = {n for n in page_inks if page_inks.count(n) > 1}
    if duplicates:
        raise ValueError(
            f"an ink may only be assigned to one page; repeated: {sorted(duplicates)}"
        )

    width, height, _ = images[0]
    for index, (w, h, _) in enumerate(images):
        if (w, h) != (width, height):
            raise ValueError(
                f"page {index} is {w}x{h}, expected {width}x{height}; "
                "pages print onto one sheet and must register with each other"
            )

    row_bytes = (width + 7) // 8
    planes: dict[str, bytes] = {}

    for (w, h, pixels), name in zip(images, page_inks):
        buf = bytearray(row_bytes * h)
        for y in range(h):
            row_base = y * w * 3
            plane_row_base = y * row_bytes
            for x in range(w):
                idx = row_base + x * 3
                # K component of the ppmtomd separation: the amount of ink
                # common to all three channels. Same formula as to_planes.
                c = 255 - pixels[idx]
                m = 255 - pixels[idx + 1]
                yv = 255 - pixels[idx + 2]
                k = min(c, m, yv)
                if k >= 128:
                    buf[plane_row_base + (x >> 3)] |= 0x80 >> (x & 7)
        planes[name] = bytes(buf)

    return planes
