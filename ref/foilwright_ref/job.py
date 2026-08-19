"""L1/L2 boundary: choose an ink specification method (DOMAIN.md §6.6 /
D-016), build per-ink planes from the palette-derived information alone,
and decide which inks belong in the job.

This is the ``ref/`` counterpart of ``src/Foilwright.Core/JobAssembly.cs``
(D-033). It exists so that this layer's logic -- white-mode selection,
two-role ink merging, empty-plane exclusion -- has a second, independently
written implementation to catch against (D-006), with a byte-comparison
test doing the actual catching (``ref/tests/test_cross_language_match.py``).

Only the pure computation is ported here, not JobAssembly's input
validation, config-value checking, or exception wording (D-033): this
module raises plain ``ValueError`` where JobAssembly raises
``ArgumentException`` with a friendlier message, and does not expose
JobAssembly's ``ValidInkModes`` / ``ValidWhiteModes`` / ``ValidHalftones``
lists (those back CLI input validation in ``src/``, which has no ``ref/``
equivalent since ``ref/`` has no UI, DOMAIN.md §9.4).

The three responsibilities mirror JobAssembly.cs's module comment:

1. In "auto" mode, derive ``cmyk_map`` from the palette's ``channel``
   field (D-019) and merge the plane of a two-role ink (one with both
   ``magic_rgb`` and ``channel`` -- the default palette's ``black``) into
   a single plane (D-019's follow-up).
2. Exclude inks whose plane has no bit set at all: an empty pass still
   costs time and ribbon, and would demand a cassette that may not be
   loaded.
3. Apply the white mode (DOMAIN.md §7.1 / D-027). The setting overrides
   the palette's ``auto_undercoat`` -- that flag is only a default.
   "White" is identified as whichever ink has ``auto_undercoat: true``
   in the palette (never by name).

``raster.py``'s ``to_planes`` / ``to_planes_magic`` / ``to_planes_auto``
(golden-verified) are called as-is and never modified by this module.
"""

from __future__ import annotations

from . import raster


def plane_has_content(plane: bytes) -> bool:
    """Return True if any bit is set in `plane` (i.e. the ink prints
    something). Mirrors JobAssembly.PlaneHasContent."""
    return any(byte != 0 for byte in plane)


def apply_white_mode(palette: list[dict], white_mode: str) -> list[dict]:
    """Apply the white mode (DOMAIN.md §7.1 / D-027) to a palette,
    returning a new list of ink mappings.

    "White" is identified as the (at most one) ink with `auto_undercoat`
    set to True in `palette`. If zero or more than one ink has it set,
    the white-mode target is undefined and `palette` is returned
    unchanged (same rule as to_planes_magic/to_planes_auto's own
    `auto_undercoat` handling).

      - "none": remove the white ink from the palette entirely -- no
        plane is built for it at all, not even a direct magic_rgb match.
      - "auto": force the white ink's `auto_undercoat` to True (the
        setting overrides the palette's own value, even if it was False).
      - "magic": force the white ink's `auto_undercoat` to False. Only
        pixels that directly match its `magic_rgb` become white.
      - "opaque" (D-032): force the white ink's `auto_undercoat` to False
        (same as "magic"). The union-of-other-inks behaviour built into
        to_planes_magic/to_planes_auto does not apply here -- instead
        every non-pure-white pixel becomes white, which the caller
        (build_job_planes) adds afterwards via apply_opaque_white_mode.
        The direct magic_rgb match is kept here so it is included too,
        same as "auto"/"magic".

    Mirrors JobAssembly.ApplyWhiteMode. Ink mappings are never mutated in
    place; a modified copy is returned for the ink whose flag changes.
    """
    white_inks = [ink for ink in palette if ink.get("auto_undercoat")]
    if len(white_inks) != 1:
        return list(palette)
    white_ink = white_inks[0]

    def with_auto_undercoat(ink: dict, auto_undercoat: bool) -> dict:
        new_ink = dict(ink)
        new_ink["auto_undercoat"] = auto_undercoat
        return new_ink

    if white_mode == "none":
        return [ink for ink in palette if ink is not white_ink]
    if white_mode == "auto":
        return [
            with_auto_undercoat(ink, True) if ink is white_ink else ink
            for ink in palette
        ]
    if white_mode in ("magic", "opaque"):
        return [
            with_auto_undercoat(ink, False) if ink is white_ink else ink
            for ink in palette
        ]
    raise ValueError(
        f"unknown white mode {white_mode!r}; expected one of "
        "'none', 'auto', 'magic', 'opaque'"
    )


def compute_non_white_pixel_plane(image: tuple[int, int, bytes]) -> bytes:
    """Build a 1bit plane with a bit set for every pixel that is not pure
    white (255, 255, 255) (DOMAIN.md §6.1 / D-032).

    Pure white is excluded because DOMAIN.md §6.1 defines white as *not*
    (255, 255, 255) -- 255 is the "do not print here" value. Setting the
    bit for pure-white pixels too would flood the whole sheet with white.

    image: (width, height, pixels) as returned by read_ppm.

    Returns a plane in the same packed format as to_planes/to_planes_magic:
    each row MSB-first, padded to a byte boundary (row length =
    ceil(width/8) bytes), rows concatenated in image order.
    """
    width, height, pixels = image
    row_bytes = (width + 7) // 8
    plane = bytearray(row_bytes * height)

    for y in range(height):
        row_base = y * width * 3
        plane_row_base = y * row_bytes
        for x in range(width):
            idx = row_base + x * 3
            r = pixels[idx]
            g = pixels[idx + 1]
            b = pixels[idx + 2]
            if r == 255 and g == 255 and b == 255:
                # Pure white is "do not print here" (DOMAIN.md §6.1);
                # excluded from the opaque mode's target set.
                continue
            byte_index = plane_row_base + (x >> 3)
            bit_mask = 0x80 >> (x & 7)
            plane[byte_index] |= bit_mask

    return bytes(plane)


def apply_opaque_white_mode(
    image: tuple[int, int, bytes], inks: list[dict], planes: dict[str, bytes]
) -> dict[str, bytes]:
    """Apply the "opaque" white mode (DOMAIN.md §7.1 / D-032) on top of an
    already-computed per-ink plane dict.

    "White" is identified the same way as to_planes_magic/to_planes_auto:
    the (at most one) ink with `auto_undercoat` set to True. If zero or
    more than one ink has it set, `planes` is returned unchanged -- the
    white-mode target is undefined, same as apply_white_mode's rule.

    `planes` should already hold that ink's direct magic_rgb match (e.g.
    from calling to_planes_magic/to_planes_auto with that ink's
    `auto_undercoat` treated as False, so its own union step does not
    run) -- D-032 requires opaque to add the direct-match pixels too,
    same as "auto"/"magic" already do.

    Returns a new dict; `planes` itself is not mutated. Mirrors
    JobAssembly.ApplyOpaqueWhite.
    """
    undercoat_names = [ink["name"] for ink in inks if ink.get("auto_undercoat")]
    if len(undercoat_names) != 1:
        return planes

    white_name = undercoat_names[0]
    opaque_plane = compute_non_white_pixel_plane(image)

    result = dict(planes)
    if white_name in result:
        merged = bytearray(result[white_name])
        for i, byte in enumerate(opaque_plane):
            merged[i] |= byte
        result[white_name] = bytes(merged)
    else:
        result[white_name] = opaque_plane
    return result


def _build_auto_planes(
    image: tuple[int, int, bytes],
    palette: list[dict],
    halftone: str,
    colour_correction: str,
    resolution: int,
    photo_lut_path: str | None,
) -> dict[str, bytes]:
    """Build the "auto" ink-specification method's planes. `cmyk_map` is
    never hardcoded; it is derived from the palette's `channel` field
    (D-019 / DOMAIN.md §4.5). A two-role ink (both `magic_rgb` and
    `channel` -- the default palette's `black`) has its CMYK-side plane
    routed through a temporary key and OR-merged into the spot-ink plane
    afterwards, because to_planes_auto's own `cmyk_map` would otherwise
    let the CMYK-side plane collide with the spot-ink plane under the
    same name (D-019 follow-up). Mirrors JobAssembly.BuildAutoPlanes."""
    cmyk_map: dict[str, str] = {}
    two_role_temp_names: dict[str, str] = {}  # temp key -> actual ink name

    for ink in palette:
        channel = ink.get("channel")
        if channel is None:
            continue
        if ink.get("magic_rgb") is not None:
            temp_name = ink["name"] + "\0__cmyk_dup"
            cmyk_map[channel] = temp_name
            two_role_temp_names[temp_name] = ink["name"]
        else:
            cmyk_map[channel] = ink["name"]

    raw = raster.to_planes_auto(
        image,
        palette,
        cmyk_map,
        halftone,
        colour_correction,
        resolution,
        photo_lut_path,
    )

    merged = {name: buf for name, buf in raw.items() if name not in two_role_temp_names}

    for temp_name, actual_name in two_role_temp_names.items():
        cmyk_buf = raw[temp_name]
        # `merged[actual_name]` always exists: to_planes_auto pre-fills a
        # zeroed plane for every spot ink under its own key.
        spot_buf = bytearray(merged[actual_name])
        for i, byte in enumerate(cmyk_buf):
            spot_buf[i] |= byte
        merged[actual_name] = bytes(spot_buf)

    return merged


def build_job_planes(
    image: tuple[int, int, bytes],
    palette: list[dict],
    ink_mode: str,
    halftone: str = "none",
    white_mode: str = "auto",
    colour_correction: str = "photo",
    resolution: int = 600,
    photo_lut_path: str | None = None,
) -> tuple[list[dict], dict[str, bytes]]:
    """From an image and a palette, decide which inks actually belong in
    the job and build their planes, in the palette's execution order
    (ascending `order`, ties broken by file order -- DOMAIN.md §4.3 /
    §4.9; config.load_palette already returns inks in this order).

    ink_mode: "auto" or "spot_only". "per_page" needs multiple page
        inputs and is not handled here (the caller must reject it first).

    white_mode: "none" (build no white plane) / "auto" (default; union of
        every pixel any other ink prints = auto_undercoat) / "magic"
        (only pixels matching white's magic_rgb) / "opaque" (every pixel
        that is not pure (255,255,255), plus direct magic_rgb matches --
        DOMAIN.md §7.1 / D-027, "opaque" is D-032). Overrides the
        palette's `auto_undercoat` flag.

    Inks with an entirely blank plane are excluded from the result. If
    every ink ends up blank, both return values are empty.

    colour_correction: "none"/"plain"/"photo" (forwarded to
        raster.to_planes_auto). Default "photo" (D-029).
    resolution / photo_lut_path: only consulted when
        colour_correction == "photo".

    Returns (inks, planes): `inks` is the subset of `palette` (in
    palette order) with non-empty planes; `planes` maps ink name -> bytes
    for exactly those inks, in the same packed format as raster.py's
    to_planes* functions. Mirrors JobAssembly.BuildJobPlanes.
    """
    adjusted_palette = apply_white_mode(palette, white_mode)

    if ink_mode == "auto":
        planes = _build_auto_planes(
            image,
            adjusted_palette,
            halftone,
            colour_correction,
            resolution,
            photo_lut_path,
        )
    elif ink_mode == "spot_only":
        planes = raster.to_planes_magic(image, adjusted_palette)
    elif ink_mode == "per_page":
        raise ValueError(
            "ink mode 'per_page' needs multiple page inputs; the caller "
            "must reject it before calling build_job_planes"
        )
    else:
        raise ValueError(
            f"unknown ink mode {ink_mode!r}; expected one of "
            "'auto', 'per_page', 'spot_only'"
        )

    if white_mode == "opaque":
        # raster.py (golden-verified) is never touched: apply_white_mode
        # already forced the white ink's auto_undercoat to False for
        # "opaque", so `planes` only holds its direct magic_rgb match so
        # far (the basis D-032 requires: direct matches are included too,
        # same as "auto"/"magic"). The non-pure-white pixels are computed
        # straight from the image and OR-merged in here.
        planes = apply_opaque_white_mode(image, palette, planes)

    # The result always walks the *original* palette (before white-mode
    # exclusion): a "none"-excluded ink is absent from adjusted_palette
    # and therefore from `planes` too, so the `planes.get` below simply
    # returns None for it and it drops out here -- no white-mode-specific
    # special case needed.
    inks: list[dict] = []
    result_planes: dict[str, bytes] = {}
    for ink in palette:
        plane = planes.get(ink["name"])
        if plane is None:
            continue
        if not plane_has_content(plane):
            continue
        inks.append(ink)
        result_planes[ink["name"]] = plane

    return inks, result_planes
