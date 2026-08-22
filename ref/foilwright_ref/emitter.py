"""L1 emitter: ink planes + machine profile -> MD command byte stream.

This reproduces the subset of ppmtomd 1.6's RGL command generation that
the golden fixtures under tests/golden/ exercise:

- single page, single transfer-mode group ("colourPlane" = 0x04 by
  default, ppmtomd.c:1312-1313, automatically upgraded to "multiPlane"
  = 0x08 past four inks, ppmtomd.c:1780-1783); an ink's `passes` (DOMAIN
  §6.2) repeats that ink's colour-selection + raster N times within the
  job, for overprinting (opaque white hiding power; DOMAIN §4.3)
- the cassette list (ESC & l {count} 00 C + barcodes) that multiPlane
  requires (ppmtomd.c:2526-2544; goldens g25-g27)
- no LF/print-head adjustment, no glossy finish, no overlay mode (none
  of the golden command lines use the options that would turn these on)
- PackBits compression exactly as ppmtomd.c:2362-2452 (packbits())
- ppmtomd's "extra planes go to a scratch buffer and get spliced back
  in with a backfeed command" behaviour (ppmtomd.c:2092-2138 for the
  fd routing, 2244-2296 for the backfeed/splice), which is why a
  default (no -colours) job emits blank Cyan/Magenta/Yellow planes
  even when only Black carries ink. The same backfeed/splice structure
  is reused for repeated passes of the same ink (observed in a real
  `ppmtomd -colours C=White,M=White` capture: two White selections
  separated by one backfeed, final flag only on the second).

No model-specific branching lives here (DOMAIN.md §4.4): all of the
above is either fixed protocol behaviour or driven by `profile`.

`passes` >= 2 is verified byte-exact against a real ppmtomd golden
capture: `tests/golden/g21_c1_white_twice_md5000_600.bin` (passes=2)
and `g22_c1_white_thrice_md5000_600.bin` (passes=3), captured
2026-08-19 once WSL was available again (see `tests/test_golden.py`'s
`test_g21_white_twice_md5000_600` / `test_g22_white_thrice_md5000_600`
for how `-colours C=White,M=White[,Y=White]` stands in for `passes` in
a real ppmtomd run).
"""

from __future__ import annotations

ESC = 0x1B
_RESOLUTION_CODES = {300: 0x02, 600: 0x03, 1200: 0x04}

# Transfer modes (mddata.h `transferMode`). These decide the shape of the
# whole data section, not just a flag: the single-plane modes carry no
# colour-selection commands at all.
#
# Only the three below are implemented. The cassette modes send the
# selection command with 'c' instead of 'r' (ppmtomd.c:2262-2263), and the
# raster modes use a different data layout.
_TRANSFER_MODES = {
    "black_raster": 0x00,  # single plane, no colour selection (-black)
    "colour_plane": 0x04,  # one selection + rows per ink (ppmtomd default)
    "multi_plane": 0x08,  # same shape, but for 5..7 inks (see below)
}

# A colourPlane job holds at most four printing colours; past that ppmtomd
# switches the whole job to multiPlane (ppmtomd.c:1780-1783). It is not a
# different data layout -- the selection commands and rasters are byte for
# byte the same -- only the mode byte changes and an extra cassette list
# goes out during initialisation (ppmtomd.c:2526-2544).
MAX_COLOUR_PLANE_INKS = 4

# The print head can hold seven cartridges in one pass; ppmtomd refuses
# anything beyond that rather than printing something wrong
# (ppmtomd.c:1778 "Too many printing colours").
MAX_PRINTING_COLOURS = 7


def _packbits(row: bytes) -> tuple[int, bytes]:
    """Port of ppmtomd's packbits() (ppmtomd.c:2362-2452).

    `row` is an already bit-packed (MSB-first), byte-aligned raster row.
    Returns (n, data): if n >= 0, `data` (length n) is the row with
    trailing zero bytes trimmed and should be sent uncompressed; if
    n < 0, `data` (length -n) is the PackBits-compressed encoding and
    should be sent compressed. n == 0 means the row is entirely blank
    and nothing should be sent for it at all.
    """
    num = len(row)
    while num > 0 and row[num - 1] == 0:
        num -= 1
    outu = row[:num]
    if num == 0:
        return 0, b""

    runcnt = [0] * num
    start = 0
    runcnt[0] = 0
    for i in range(1, num):
        if outu[i] == outu[i - 1]:
            if runcnt[start] <= 0 and runcnt[start] > -127:
                runcnt[start] -= 1
            else:
                start = i
                runcnt[start] = 0
        else:
            if runcnt[start] >= 0 and runcnt[start] < 127:
                runcnt[start] += 1
            else:
                start = i
                runcnt[start] = 0

    outc = bytearray()
    i = 0
    while i < num:
        count = runcnt[i]
        frm = i
        if count >= 0:
            while True:
                nxt = i + 1 + runcnt[i]
                if nxt >= num or runcnt[nxt] < 0 or count + runcnt[nxt] + 1 > 127:
                    break
                count += runcnt[nxt] + 1
                i = nxt
        nxt = i + 1 + (-runcnt[i] if runcnt[i] < 0 else runcnt[i])
        outc.append(count & 0xFF)
        if count >= 0:
            j = frm
            c = count
            while c >= 0:
                outc.append(outu[j])
                j += 1
                c -= 1
        else:
            outc.append(outu[frm])
        i = nxt

    if len(outc) < num:
        return -len(outc), bytes(outc)
    return num, outu


def _emit_plane_rows(plane: bytes, width: int, height: int) -> bytes:
    row_bytes = (width + 7) // 8
    out = bytearray()
    compression_state = None  # None == ppmtomd's -1 "unset" sentinel
    rowstoskip = 0
    for row in range(height):
        raw = plane[row * row_bytes : (row + 1) * row_bytes]
        n, data = _packbits(raw)
        if n == 0:
            rowstoskip += 1
            continue
        mode = 0 if n >= 0 else 1
        if compression_state != mode:
            out += bytes([ESC, 0x2A, 0x62, 2 if mode else 0, 0, 0x4D])
            compression_state = mode
        if rowstoskip:
            out += bytes([ESC, 0x2A, 0x62, rowstoskip % 256, rowstoskip // 256, 0x59])
            rowstoskip = 0
        length = n if n >= 0 else -n
        vw = 0x56 if row == height - 1 else 0x57
        out += bytes([ESC, 0x2A, 0x62, length % 256, length // 256, vw]) + data
    return bytes(out)


def emit_job(planes: dict[str, bytes], job: dict) -> bytes:
    """Build the MD command byte stream for one page.

    planes: ink name -> packed 1bit plane bytes (from raster.to_planes).
    job describes one print job completely, mixing no machine-profile
    lookup logic in here (DOMAIN.md §4.4 -- no model-specific branching):
    {
        "resolution": 300 | 600 | 1200,
        "paper": {
            "code": int (ppmtomd paper size code; 4 = A4),
            "width": int (dots, at the 600dpi baseline),
            "length": int (dots, at the 600dpi baseline),
            "left_margin": int, "top_margin": int,  # unused here so far
        },
        "media": {"byte1": int, "byte2": int},  # from config.load_media_table
        "inks": [ {"name": str, "printer_code": int}, ... ]  # print order;
            this is the full set of active inks for the job (an entry
            here with an all-blank plane still gets a (blank) selection
            command emitted, matching ppmtomd's default -colours
            behaviour of always driving C/M/Y/K). Each entry also needs
            a "barcode" (the cassette's barcode number, DOMAIN §6.5) once
            there are more than MAX_COLOUR_PLANE_INKS of them, because
            multi_plane sends the cassette list.
        "width": int, "height": int,  # image pixel dimensions
        "x_shift": int, "y_shift": int,  # optional, default 0; dots
    }
    """
    resolution = job["resolution"]
    res_code = _RESOLUTION_CODES[resolution]
    width = job["width"]
    height = job["height"]

    paper = job["paper"]
    page_width = paper["width"]
    page_length = paper["length"]
    if resolution == 300:
        page_width //= 2
        page_length //= 2
    elif resolution == 1200:
        page_width *= 2

    inks = job["inks"]

    # Transfer mode. ppmtomd starts from colourPlane and upgrades the whole
    # job to multiPlane once there are more than four printing colours
    # (ppmtomd.c:1780-1783); an explicitly requested single-plane mode is
    # left alone, exactly as there.
    #
    # `passes` (DOMAIN §6.2) repeats one ink, i.e. one cassette, so it does
    # not add a printing colour and is deliberately not counted here. The
    # count is the number of cassettes the job asks the user to load, which
    # is also what the cassette list below enumerates. (ppmtomd has no
    # `passes`; the g21/g22 fixtures reach the same byte shape by handing
    # the same ink to several components, so no golden decides this.)
    mode_name = job.get("transfer_mode", "colour_plane")
    if mode_name == "colour_plane" and len(inks) > MAX_COLOUR_PLANE_INKS:
        mode_name = "multi_plane"
    mode = _TRANSFER_MODES[mode_name]
    if mode != _TRANSFER_MODES["black_raster"] and len(inks) > MAX_PRINTING_COLOURS:
        raise ValueError(
            f"too many printing colours: {len(inks)} inks, "
            f"the print head holds at most {MAX_PRINTING_COLOURS}"
        )

    out = bytearray()

    # rgl_init_page (ppmtomd.c:2484-2564), reduced to the fields every
    # golden fixture uses.
    out += bytes([ESC, 0x25, 0x80, 0x41])  # select RGL mode
    # ppmtomd's sprintf("\033*t%cR", ...) is 5 bytes, but the send count
    # passed to out_function is 6 (ppmtomd.c:2489-2490), so a spurious
    # trailing NUL (the sprintf string terminator) is sent too. This is
    # a genuine ppmtomd quirk that every golden fixture bakes in.
    out += bytes([ESC, 0x2A, 0x74, res_code, 0x52, 0x00])  # output resolution
    media = job["media"]
    out += bytes([ESC, 0x26, 0x6C, media["byte1"], media["byte2"], 0x4D])
    out += bytes([ESC, 0x26, 0x6C, paper["code"], 0, 0x41])
    out += bytes([ESC, 0x26, 0x6C, page_length % 256, page_length // 256, 0x50])
    out += bytes([ESC, 0x26, 0x61, page_width % 256, page_width // 256, 0x4D])

    # Cassette list, sent only in multiPlane (ppmtomd.c:2526-2544):
    # ESC & l {count} 00 C followed by one cassette barcode number per ink,
    # in print order. The barcode numbering is a different scheme from the
    # colour-selection byte (DOMAIN §6.5), so it comes from the palette
    # entry's own `barcode` field and is never derived from printer_code.
    if mode == _TRANSFER_MODES["multi_plane"]:
        barcodes = []
        for ink in inks:
            barcode = ink.get("barcode")
            if barcode is None:
                raise ValueError(
                    f"ink '{ink.get('name')}': 'barcode' is required with "
                    f"more than {MAX_COLOUR_PLANE_INKS} inks (the cassette "
                    "list command carries it)"
                )
            if (
                not isinstance(barcode, int)
                or isinstance(barcode, bool)
                or not 0 <= barcode <= 255
            ):
                raise ValueError(
                    f"ink '{ink.get('name')}': 'barcode' must be an integer "
                    f"in 0..255, got {barcode!r}"
                )
            barcodes.append(barcode)
        out += bytes([ESC, 0x26, 0x6C, len(barcodes), 0, 0x43]) + bytes(barcodes)

    # x/y offsets, in dots at the output resolution. ppmtomd emits a
    # command only when the shift is positive (ppmtomd.c:2546-2555).
    # A negative shift means "start the raster partway in", which ppmtomd
    # implements by trimming the image data rather than by any command
    # (ppmtomd.c:2659) -- not implemented here, so reject it loudly rather
    # than silently printing in the wrong place.
    #
    # Subtracting the paper's unprintable margins (ppmtomd's -autoshift) is
    # the caller's job: this layer receives the final shift, so that the
    # margin values stay with the paper table where they belong.
    x_shift = job.get("x_shift", 0)
    y_shift = job.get("y_shift", 0)
    if x_shift < 0 or y_shift < 0:
        raise NotImplementedError(
            f"negative shift (x={x_shift}, y={y_shift}) trims the raster "
            "instead of emitting a command; not implemented"
        )
    if x_shift > 0:
        out += bytes([ESC, 0x26, 0x61, x_shift % 256, x_shift // 256, 0x4C])
    if y_shift > 0:
        out += bytes([ESC, 0x26, 0x6C, y_shift % 256, y_shift // 256, 0x45])

    # changemode block (ppmtomd.c:2189-2245). Print mode stays at its
    # default (byMediaMode), so this fires exactly once, before the
    # first ink.
    #
    # Curl correction: 0 applies it, 1 suppresses it (ppmtomd's
    # -nocurlcorrection). Decal stock needs it suppressed -- the sheet
    # must stay flat (DOMAIN §10.10.4).
    curl = 1 if job.get("no_curl_correction") else 0
    out += bytes([ESC, 0x1A, curl, 0, 0x43])

    out += bytes([ESC, 0x2A, 0x72, mode, 0x55])
    out += bytes([ESC, 0x2A, 0x72, 0, 0x41])  # start raster graphics

    if mode == _TRANSFER_MODES["black_raster"]:
        # Single-plane modes carry no colour-selection command at all:
        # the mode itself says which ribbon to use, so there is nothing
        # to select and nothing to backfeed between. Verified against
        # ppmtomd -black, where the byte stream is identical to the
        # colourPlane one minus 4 selections and 3 backfeeds (35 bytes).
        if len(inks) != 1:
            raise ValueError(f"black_raster carries exactly one plane, got {len(inks)}")
        out += _emit_plane_rows(planes[inks[0]["name"]], width, height)
    else:
        # `passes` (DOMAIN §6.2): repeat an ink's (colour-selection +
        # raster) that many times. Default 1 when omitted, matching
        # config.load_palette's default (config.py:181). This expansion
        # happens purely at the emitter's output-shape level: to_planes
        # still produces one plane per ink, unchanged.
        occurrences: list[dict] = []
        for ink in inks:
            passes = ink.get("passes", 1)
            if not isinstance(passes, int) or isinstance(passes, bool) or passes < 1:
                raise ValueError(
                    f"ink '{ink.get('name')}': 'passes' must be an integer "
                    f">= 1, got {passes!r}"
                )
            occurrences.extend([ink] * passes)

        last_index = len(occurrences) - 1

        def _select_and_rows(index: int) -> bytes:
            ink = occurrences[index]
            flag = 0x80 if index == last_index else 0x00
            buf = bytearray([ESC, 0x1A, ink["printer_code"], flag, 0x72])
            buf += _emit_plane_rows(planes[ink["name"]], width, height)
            return bytes(buf)

        # The first (direct) occurrence's bytes land immediately on the
        # stream; every subsequent occurrence -- whether a different ink
        # or a repeated pass of the same one -- is buffered separately
        # by ppmtomd and spliced back in afterwards behind a backfeed
        # command (ppmtomd.c:2272-2296), which only happens when there
        # is more than one occurrence in total.
        out += _select_and_rows(0)
        if len(occurrences) > 1:
            for index in range(1, len(occurrences)):
                out += bytes([ESC, 0x1A, 0, 0, 0x0C])  # backfeed
                out += _select_and_rows(index)

    # job end (ppmtomd.c:2332-2345)
    out += bytes([ESC, 0x2A, 0x72, 0x43])  # end raster graphics
    out += bytes([0x0C])  # form feed
    out += bytes([ESC, 0x25, 0, 0x58])  # end RGL mode
    out += bytes([ESC, 0x65])  # printer reset

    return bytes(out)
