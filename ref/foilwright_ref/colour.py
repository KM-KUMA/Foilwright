"""Colour correction: ppmtomd's ``colcorPhoto`` lookup-table path.

Reproduces the non-mono, ``opt_keepblack``-disabled branch of ppmtomd's
colour correction switch (vendor/ppmtomd-1.6/ppmtomd.c:2929-2960), the
16^3 -> 64^3 trilinear lookup-table expansion (``expand_lut``,
ppmtomd.c:3395-3444), and the ``initgamma`` table construction
(ppmtomd.c:1932-1957):

    c = initgamma[c] ; m = initgamma[m] ; y = initgamma[y]
    idx = ((c & 0xFC) << 12) | ((m & 0xFC) << 6) | (y & 0xFC)
    c, m, y, k = colconv[idx .. idx+3]

``opt_keepblack`` (ppmtomd.c:2944, "ensure a solid black stays 100% K") is
not reproduced: it defaults to disabled and D-029 explicitly excludes it
from this implementation's scope.

This module has no dependency on raster.py (one-directional: raster.py
imports colour.py, never the reverse).
"""

from __future__ import annotations

import math

VALID_COLOUR_CORRECTIONS = frozenset({"none", "plain", "photo"})

_LUT_BYTES_16 = 16 * 16 * 16 * 4
_LUT_BYTES_64 = 64 * 64 * 64 * 4


def load_photo_lut(path: str) -> bytes:
    """Read ppmtomd's 16x16x16x4 ``photo_colcor`` table (colour/README.md).

    Returns the raw 16,384-byte table, flattened as
    ``inlut[c][m][y][component]`` (c slowest-varying), unexpanded. Raises
    ValueError if the file is not exactly this size.
    """
    with open(path, "rb") as f:
        data = f.read()
    if len(data) != _LUT_BYTES_16:
        raise ValueError(
            f"{path}: expected a {_LUT_BYTES_16}-byte (16x16x16x4) photo "
            f"colour-correction table, got {len(data)} bytes"
        )
    return data


def expand_lut(inlut: bytes) -> bytes:
    """Port of ppmtomd's ``expand_lut`` (ppmtomd.c:3395-3444).

    Expands a 16x16x16x4 lookup table (``inlut``, as returned by
    ``load_photo_lut``) into a 64x64x64x4 table via trilinear
    interpolation between adjacent grid points, using the ppmtomd's
    integer arithmetic (division truncated toward zero via ``//``, which
    is exact here since every operand is non-negative).

    Reproduces ppmtomd's documented quirk (ppmtomd.c:3407-3409): when the
    origin index on any axis is 15 (the last grid point), the "next"
    corner on that axis is *not* looked up (there is no index 16) -- it
    is replaced by the origin point itself, rather than by wrapping or
    clamping the mathematically distinct neighbour. This must be
    reproduced exactly or the output near the 15/63 edge of the cube
    diverges from ppmtomd's.

    Returns a 1,048,576-byte table, flattened as
    ``outlut[i][j][k][component]`` (i slowest-varying, 64 entries per
    axis), which is directly indexable by
    ``((c & 0xFC) << 12) | ((m & 0xFC) << 6) | (y & 0xFC)`` -- i.e. index
    ``i`` corresponds to ``c >> 2`` and so on.
    """
    if len(inlut) != _LUT_BYTES_16:
        raise ValueError(
            f"expand_lut: expected a {_LUT_BYTES_16}-byte input table, "
            f"got {len(inlut)} bytes"
        )

    def in_at(i: int, j: int, k: int, m: int) -> int:
        return inlut[i * 1024 + j * 64 + k * 4 + m]

    outlut = bytearray(_LUT_BYTES_64)

    for i in range(16):
        for j in range(16):
            for k in range(16):
                # cube[ii][jj][kk][m]: the 16 corner points of this cell.
                # ppmtomd.c:3407-3409: at the top edge (index 15) the
                # "next" corner is the origin corner itself, not index 16
                # (which does not exist).
                cube = [
                    [
                        [
                            [
                                in_at(
                                    i + (0 if i == 15 else ii),
                                    j + (0 if j == 15 else jj),
                                    k + (0 if k == 15 else kk),
                                    m,
                                )
                                for m in range(4)
                            ]
                            for kk in range(2)
                        ]
                        for jj in range(2)
                    ]
                    for ii in range(2)
                ]

                for ii in range(4):
                    for jj in range(4):
                        for kk in range(4):
                            out_base = (
                                (i * 4 + ii) * 64 * 64 * 4
                                + (j * 4 + jj) * 64 * 4
                                + (k * 4 + kk) * 4
                            )
                            for m in range(4):
                                res = (
                                    cube[0][0][0][m] * (4 - ii) * (4 - jj) * (4 - kk)
                                    + cube[0][0][1][m] * (4 - ii) * (4 - jj) * kk
                                    + cube[0][1][0][m] * (4 - ii) * jj * (4 - kk)
                                    + cube[0][1][1][m] * (4 - ii) * jj * kk
                                    + cube[1][0][0][m] * ii * (4 - jj) * (4 - kk)
                                    + cube[1][0][1][m] * ii * (4 - jj) * kk
                                    + cube[1][1][0][m] * ii * jj * (4 - kk)
                                    + cube[1][1][1][m] * ii * jj * kk
                                )
                                outlut[out_base + m] = res // 64

    return bytes(outlut)


def build_gamma_table(gamma: float) -> tuple[int, ...]:
    """Port of ppmtomd's ``initgamma`` construction (ppmtomd.c:1949-1957).

        ii = i / 255
        ii = ii ** gamma           if gamma > 0
        ii = 1 - (1 - ii) ** -gamma  if gamma < 0
        table[i] = floor(255 * ii + 0.5)

    ``gamma == 0`` is not a valid input here (ppmtomd only ever reaches
    this computation after resolving 0 to a mode-dependent default; see
    ``default_gamma``). This mirrors ppmtomd's use of C ``double`` /
    ``pow`` exactly (rather than an integer-only reformulation, unlike
    the rest of this codebase's per-pixel arithmetic, D-015) because the
    golden fixtures were generated by that same floating-point code path,
    and Python ``float`` is also an IEEE-754 double.
    """
    if gamma == 0:
        raise ValueError("build_gamma_table: gamma must not be 0")

    table = [0] * 256
    for i in range(256):
        ii = i / 255.0
        if gamma > 0.0:
            ii = ii**gamma
        else:
            ii = 1.0 - (1.0 - ii) ** (-gamma)
        table[i] = math.floor(255.0 * ii + 0.5)
    return tuple(table)


def default_gamma(halftone: str, resolution: int) -> float:
    """The default ``initgam`` ppmtomd picks when ``-gamma`` is not given
    (ppmtomd.c:1932-1948), restricted to the non-nybble-mode case (nybble
    / multi-value mode is not implemented here).

    | dither              | resolution | initgam |
    |---------------------|------------|---------|
    | halftone/coarse     | 1200       | -0.9    |
    | halftone/coarse     | other      | 0.8     |
    | none                | any        | 1.2     |
    """
    if halftone in ("halftone", "coarse_halftone"):
        return -0.9 if resolution == 1200 else 0.8
    return 1.2
