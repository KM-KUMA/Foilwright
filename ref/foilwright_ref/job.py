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
4. Build the planes of "coverage" inks (D-048) -- inks whose printed
   area is chosen per job ("none"/"artwork"/"full") rather than by pixel
   colour. Kept as a separate mechanism alongside the white mode, not
   merged into it (D-048: the working white path is not touched).

``raster.py``'s ``to_planes`` / ``to_planes_magic`` / ``to_planes_auto``
(golden-verified) are called as-is and never modified by this module.
"""

from __future__ import annotations

from collections import deque

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
      - "silhouette" (D-034): force the white ink's `auto_undercoat` to
        False (same as "magic"/"opaque"). Every pixel that is not
        reachable from the sheet's edges by walking pure-white pixels
        through 4-neighbours becomes white -- that includes pure-white
        pixels enclosed by the artwork (e.g. a white-filled eye layer),
        which "opaque" deliberately excludes. The caller (build_job_planes)
        adds this afterwards via apply_silhouette_white_mode. The direct
        magic_rgb match is kept here so it is included too, same as
        "auto"/"magic"/"opaque".
      - "alpha" (D-037): force the white ink's `auto_undercoat` to False
        (same as "magic"/"opaque"/"silhouette"). Every pixel with a
        non-zero alpha in Ghostscript's separate `pngalpha` rendering of
        the same page becomes white -- that alpha comes from a second
        image entirely (`alpha_image`, not `image`'s RGB), computed by
        compute_alpha_plane and merged in by the caller
        (build_job_planes) via apply_alpha_white_mode. The direct
        magic_rgb match is kept here so it is included too, same as
        "auto"/"magic"/"opaque"/"silhouette".

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
    if white_mode in ("magic", "opaque", "silhouette", "alpha"):
        return [
            with_auto_undercoat(ink, False) if ink is white_ink else ink
            for ink in palette
        ]
    raise ValueError(
        f"unknown white mode {white_mode!r}; expected one of "
        "'none', 'auto', 'magic', 'opaque', 'silhouette', 'alpha'"
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


def compute_silhouette_plane(image: tuple[int, int, bytes]) -> bytes:
    """Build a 1bit plane with a bit set for every pixel that is *not*
    reachable from the sheet's four edges by walking pure-white
    (255, 255, 255) pixels through 4-neighbours (DOMAIN.md §6.1 / §7.1 /
    D-034).

    Pure white reachable from an edge is the sheet's background (do not
    print here); pure white enclosed by non-white pixels -- e.g. a
    white-filled eye layer inside a closed outline -- is not reachable
    and gets a bit set, same as every non-pure-white pixel.

    Algorithm: a queue-based flood fill (BFS), seeded from every
    pure-white pixel on the sheet's four edges. No recursion (avoids
    stack overflow on large images -- D-034).

    image: (width, height, pixels) as returned by read_ppm.

    Returns a plane in the same packed format as
    compute_non_white_pixel_plane: each row MSB-first, padded to a byte
    boundary (row length = ceil(width/8) bytes), rows concatenated in
    image order.
    """
    width, height, pixels = image
    row_bytes = (width + 7) // 8

    reached = bytearray(width * height)

    def is_pure_white(x: int, y: int) -> bool:
        idx = (y * width + x) * 3
        return pixels[idx] == 255 and pixels[idx + 1] == 255 and pixels[idx + 2] == 255

    queue: deque[tuple[int, int]] = deque()

    def seed(x: int, y: int) -> None:
        i = y * width + x
        if reached[i]:
            return
        if not is_pure_white(x, y):
            return
        reached[i] = 1
        queue.append((x, y))

    for x in range(width):
        seed(x, 0)
        if height > 1:
            seed(x, height - 1)
    for y in range(height):
        seed(0, y)
        if width > 1:
            seed(width - 1, y)

    while queue:
        x, y = queue.popleft()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < width and 0 <= ny < height:
                seed(nx, ny)

    plane = bytearray(row_bytes * height)
    for y in range(height):
        row_base = y * width
        plane_row_base = y * row_bytes
        for x in range(width):
            if not reached[row_base + x]:
                byte_index = plane_row_base + (x >> 3)
                bit_mask = 0x80 >> (x & 7)
                plane[byte_index] |= bit_mask

    return bytes(plane)


def apply_silhouette_white_mode(
    image: tuple[int, int, bytes], inks: list[dict], planes: dict[str, bytes]
) -> dict[str, bytes]:
    """Apply the "silhouette" white mode (DOMAIN.md §7.1 / D-034) on top
    of an already-computed per-ink plane dict.

    "White" is identified the same way as apply_opaque_white_mode: the
    (at most one) ink with `auto_undercoat` set to True. If zero or more
    than one ink has it set, `planes` is returned unchanged.

    `planes` should already hold that ink's direct magic_rgb match (same
    precondition as apply_opaque_white_mode) -- D-034 requires silhouette
    to add the direct-match pixels too, same as "auto"/"magic"/"opaque".

    Returns a new dict; `planes` itself is not mutated. Mirrors
    JobAssembly.ApplySilhouetteWhite.
    """
    undercoat_names = [ink["name"] for ink in inks if ink.get("auto_undercoat")]
    if len(undercoat_names) != 1:
        return planes

    white_name = undercoat_names[0]
    silhouette_plane = compute_silhouette_plane(image)

    result = dict(planes)
    if white_name in result:
        merged = bytearray(result[white_name])
        for i, byte in enumerate(silhouette_plane):
            merged[i] |= byte
        result[white_name] = bytes(merged)
    else:
        result[white_name] = silhouette_plane
    return result


def compute_alpha_plane(width: int, height: int, rgba: bytes) -> bytes:
    """Build a 1bit plane with a bit set for every pixel whose alpha
    channel is non-zero (DOMAIN.md §7.1 / D-037).

    `alpha > 0` is the whole rule: Ghostscript's `pngalpha` device
    distinguishes "painted white" (alpha=255) from "nothing drawn"
    (alpha=0), and D-037 treats any non-zero alpha -- including the
    partial alpha of an anti-aliased edge -- as "print white here"
    (deliberately generous: the white is meant to slightly overshoot the
    colour on decals, DOMAIN.md §7.1.1's `opaque` rationale applies here
    too).

    width, height, rgba: as returned by png.read_png_rgba (rgba is
    row-major, 4 bytes per pixel: R, G, B, A).

    Returns a plane in the same packed format as
    compute_non_white_pixel_plane / compute_silhouette_plane: each row
    MSB-first, padded to a byte boundary (row length = ceil(width/8)
    bytes), rows concatenated in image order. Mirrors
    JobAssembly.ComputeAlphaPlane.
    """
    row_bytes = (width + 7) // 8
    plane = bytearray(row_bytes * height)

    for y in range(height):
        row_base = y * width * 4
        plane_row_base = y * row_bytes
        for x in range(width):
            alpha = rgba[row_base + x * 4 + 3]
            if alpha == 0:
                continue
            byte_index = plane_row_base + (x >> 3)
            bit_mask = 0x80 >> (x & 7)
            plane[byte_index] |= bit_mask

    return bytes(plane)


def apply_alpha_white_mode(
    alpha_image: tuple[int, int, bytes],
    inks: list[dict],
    planes: dict[str, bytes],
) -> dict[str, bytes]:
    """Apply the "alpha" white mode (DOMAIN.md §7.1 / D-037) on top of an
    already-computed per-ink plane dict.

    "White" is identified the same way as apply_opaque_white_mode /
    apply_silhouette_white_mode: the (at most one) ink with
    `auto_undercoat` set to True. If zero or more than one ink has it
    set, `planes` is returned unchanged.

    `planes` should already hold that ink's direct magic_rgb match (same
    precondition as apply_opaque_white_mode/apply_silhouette_white_mode)
    -- D-037 requires alpha to add the direct-match pixels too, same as
    "auto"/"magic"/"opaque"/"silhouette".

    alpha_image: (width, height, rgba) from a *separate* Ghostscript
    `pngalpha` rendering of the same page (png.read_png_rgba's return
    shape) -- not the `image` (ppmraw) used for colour. Only the alpha
    channel is used; the RGB in alpha_image is discarded (D-037: mixing
    pngalpha's RGB into colour output changes 18.4% of fully-painted
    pixels, measured 2026-08-20).

    Returns a new dict; `planes` itself is not mutated. Mirrors
    JobAssembly.ApplyAlphaWhite.
    """
    undercoat_names = [ink["name"] for ink in inks if ink.get("auto_undercoat")]
    if len(undercoat_names) != 1:
        return planes

    white_name = undercoat_names[0]
    width, height, rgba = alpha_image
    alpha_plane = compute_alpha_plane(width, height, rgba)

    result = dict(planes)
    if white_name in result:
        merged = bytearray(result[white_name])
        for i, byte in enumerate(alpha_plane):
            merged[i] |= byte
        result[white_name] = bytes(merged)
    else:
        result[white_name] = alpha_plane
    return result


#: Coverage modes a coverage ink (D-048) can be given for one job:
#: "none" (default -- build no plane at all), "artwork" (every pixel that
#: is not pure white), "full" (every pixel). Mirrors
#: JobAssembly.ValidCoverageModes.
VALID_COVERAGE_MODES = ("none", "artwork", "full")


def compute_full_coverage_plane(width: int, height: int) -> bytes:
    """Build a 1bit plane with a bit set for every pixel of a
    `width` x `height` sheet (D-048's "full" coverage mode).

    The padding bits past `width` in each row's last byte stay zero, same
    as every other plane builder here -- otherwise the two
    implementations would disagree on those bits and the emitted RGL
    would differ.

    Returns a plane in the same packed format as
    compute_non_white_pixel_plane. Mirrors
    JobAssembly.ComputeFullCoveragePlane.
    """
    row_bytes = (width + 7) // 8
    row = bytearray(row_bytes)
    for x in range(width):
        row[x >> 3] |= 0x80 >> (x & 7)
    return bytes(row) * height


def apply_coverage_modes(
    image: tuple[int, int, bytes],
    inks: list[dict],
    planes: dict[str, bytes],
    coverage_modes: dict[str, str],
) -> dict[str, bytes]:
    """Add a plane for each coverage ink (D-048) whose mode is not
    "none", on top of an already-computed per-ink plane dict.

    A coverage ink is one with `coverage` set to True in the palette. It
    has neither `magic_rgb` nor `channel` (config.py rejects that
    combination), so raster.py never builds a plane for it and there is
    nothing to merge with -- the plane computed here is simply stored.

    Inks without `coverage` are untouched, whatever `coverage_modes`
    says about them. An ink missing from `coverage_modes`, or mapped to
    "none", gets no plane at all -- so a caller that passes nothing
    produces byte-identical output to before D-048.

    No halftone and no colour correction are applied: a coverage ink is
    on or off per pixel (D-048 decision 4 / ppmtomd man:564-565).

    Returns a new dict; `planes` itself is not mutated. Mirrors
    JobAssembly.ApplyCoverageModes.
    """
    width, height, _ = image

    result = dict(planes)
    for ink in inks:
        if not ink.get("coverage"):
            continue
        mode = coverage_modes.get(ink["name"], "none")
        if mode == "none":
            continue
        if mode == "artwork":
            # Same rule as the "opaque" white mode uses, reusing the same
            # function rather than writing the pure-white test twice.
            result[ink["name"]] = compute_non_white_pixel_plane(image)
        elif mode == "full":
            result[ink["name"]] = compute_full_coverage_plane(width, height)
        else:  # pragma: no cover - build_job_planes validates first
            raise ValueError(
                f"unknown coverage mode {mode!r}; expected one of "
                "'none', 'artwork', 'full'"
            )
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
    alpha_image: tuple[int, int, bytes] | None = None,
    coverage_modes: dict[str, str] | None = None,
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
        DOMAIN.md §7.1 / D-027, "opaque" is D-032) / "silhouette" (every
        pixel not reachable from the sheet's edges through pure-white
        4-neighbours, plus direct magic_rgb matches -- D-034) / "alpha"
        (every pixel with non-zero alpha in `alpha_image`, plus direct
        magic_rgb matches -- D-037). Overrides the palette's
        `auto_undercoat` flag.

    alpha_image: (width, height, rgba) from a separate Ghostscript
        `pngalpha` rendering of the same page (png.read_png_rgba's return
        shape). Only consulted when white_mode == "alpha"; ignored
        otherwise. Required when white_mode == "alpha" (raises
        ValueError if None), and its width/height must match `image`'s
        (raises ValueError otherwise) -- D-037: colour comes from
        `image` (ppmraw) and white comes from `alpha_image` (pngalpha),
        and the two must describe the same page at the same resolution.

    coverage_modes: ink name -> "none" / "artwork" / "full" (D-048). Only
        consulted for inks with `coverage` set in the palette; other inks
        are unaffected whatever this says about them. An ink that is
        absent here, or mapped to "none", gets no plane at all -- so the
        default (None) reproduces the pre-D-048 output byte for byte.
        An unrecognised value raises ValueError, same as an unknown white
        mode: it is never silently downgraded to "none".

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
    if coverage_modes:
        # Fail fast, before any raster work, and check every entry --
        # including ones naming a non-coverage ink, which are ignored
        # later but are still a caller mistake worth surfacing (D-048:
        # an unknown value is never silently treated as "none").
        for ink_name, mode in coverage_modes.items():
            if mode not in VALID_COVERAGE_MODES:
                raise ValueError(
                    f"unknown coverage mode {mode!r} for ink {ink_name!r}; "
                    "expected one of 'none', 'artwork', 'full'"
                )

    if white_mode == "alpha":
        # Fail fast, before any raster work: alpha_image is a second
        # Ghostscript rendering the caller must have already produced (D-037).
        if alpha_image is None:
            raise ValueError("white_mode 'alpha' requires alpha_image (D-037)")
        alpha_width, alpha_height, _ = alpha_image
        image_width, image_height, _ = image
        if alpha_width != image_width or alpha_height != image_height:
            raise ValueError(
                f"alpha_image dimensions {alpha_width}x{alpha_height} do not "
                f"match image dimensions {image_width}x{image_height}"
            )

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

    if white_mode == "silhouette":
        # Same reasoning as "opaque" above, but the mask comes from
        # compute_silhouette_plane (D-034) instead of
        # compute_non_white_pixel_plane.
        planes = apply_silhouette_white_mode(image, palette, planes)

    if white_mode == "alpha":
        # Same reasoning as "opaque"/"silhouette" above, but the mask
        # comes from a *different* image entirely -- alpha_image, a
        # separate pngalpha rendering, not `image` (D-037). Validated
        # non-None and dimension-matched above.
        planes = apply_alpha_white_mode(alpha_image, palette, planes)

    if coverage_modes:
        # D-048: a separate mechanism from the white mode, deliberately
        # kept side by side with it rather than merged. Runs on the
        # *original* palette, like the white-mode helpers above, so the
        # coverage inks land at their own `order` in the loop below.
        planes = apply_coverage_modes(image, palette, planes, coverage_modes)

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
