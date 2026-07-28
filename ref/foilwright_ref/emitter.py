"""L1 emitter: ink planes + machine profile -> MD command byte stream.

This reproduces the subset of ppmtomd 1.6's RGL command generation that
the golden fixtures under tests/golden/ exercise:

- single page, single pass, single transfer-mode group (transfer mode
  is always "colourPlane" = 0x04; ppmtomd.c:1312-1313)
- no curl correction, no LF/print-head adjustment, no glossy finish,
  no cassette barcode list, no x/y shift (none of the golden command
  lines use the options that would turn these on)
- PackBits compression exactly as ppmtomd.c:2362-2452 (packbits())
- ppmtomd's "extra planes go to a scratch buffer and get spliced back
  in with a backfeed command" behaviour (ppmtomd.c:2092-2138 for the
  fd routing, 2244-2296 for the backfeed/splice), which is why a
  default (no -colours) job emits blank Cyan/Magenta/Yellow planes
  even when only Black carries ink.

No model-specific branching lives here (DOMAIN.md §4.4): all of the
above is either fixed protocol behaviour or driven by `profile`.
"""

from __future__ import annotations

ESC = 0x1B
_RESOLUTION_CODES = {300: 0x02, 600: 0x03, 1200: 0x04}


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
        "media_byte1": int, "media_byte2": int,
        "inks": [ {"name": str, "printer_code": int}, ... ]  # print order;
            this is the full set of active inks for the job (an entry
            here with an all-blank plane still gets a (blank) selection
            command emitted, matching ppmtomd's default -colours
            behaviour of always driving C/M/Y/K).
        "width": int, "height": int,  # image pixel dimensions
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

    out = bytearray()

    # rgl_init_page (ppmtomd.c:2484-2564), reduced to the fields every
    # golden fixture uses.
    out += bytes([ESC, 0x25, 0x80, 0x41])  # select RGL mode
    # ppmtomd's sprintf("\033*t%cR", ...) is 5 bytes, but the send count
    # passed to out_function is 6 (ppmtomd.c:2489-2490), so a spurious
    # trailing NUL (the sprintf string terminator) is sent too. This is
    # a genuine ppmtomd quirk that every golden fixture bakes in.
    out += bytes([ESC, 0x2A, 0x74, res_code, 0x52, 0x00])  # output resolution
    out += bytes([ESC, 0x26, 0x6C, job["media_byte1"], job["media_byte2"], 0x4D])
    out += bytes([ESC, 0x26, 0x6C, paper["code"], 0, 0x41])
    out += bytes([ESC, 0x26, 0x6C, page_length % 256, page_length // 256, 0x50])
    out += bytes([ESC, 0x26, 0x61, page_width % 256, page_width // 256, 0x4D])

    # changemode block (ppmtomd.c:2189-2245), fixed for our supported
    # scope: curl correction off, transfer mode colourPlane (0x04),
    # print mode unchanged from its default (byMediaMode), so this
    # fires exactly once, before the first ink.
    out += bytes([ESC, 0x1A, 0, 0, 0x43])  # curl correction (off)
    out += bytes([ESC, 0x2A, 0x72, 0x04, 0x55])  # transfer mode = colourPlane
    out += bytes([ESC, 0x2A, 0x72, 0, 0x41])  # start raster graphics

    inks = job["inks"]
    last_index = len(inks) - 1

    def _select_and_rows(index: int) -> bytes:
        ink = inks[index]
        flag = 0x80 if index == last_index else 0x00
        buf = bytearray([ESC, 0x1A, ink["printer_code"], flag, 0x72])
        buf += _emit_plane_rows(planes[ink["name"]], width, height)
        return bytes(buf)

    # The first (direct) ink's bytes land immediately on the stream;
    # every subsequent ink is buffered separately by ppmtomd and
    # spliced back in afterwards behind a backfeed command
    # (ppmtomd.c:2272-2296), which only happens when there is more
    # than one active ink.
    out += _select_and_rows(0)
    if len(inks) > 1:
        for index in range(1, len(inks)):
            out += bytes([ESC, 0x1A, 0, 0, 0x0C])  # backfeed
            out += _select_and_rows(index)

    # job end (ppmtomd.c:2332-2345)
    out += bytes([ESC, 0x2A, 0x72, 0x43])  # end raster graphics
    out += bytes([0x0C])  # form feed
    out += bytes([ESC, 0x25, 0, 0x58])  # end RGL mode
    out += bytes([ESC, 0x65])  # printer reset

    return bytes(out)
