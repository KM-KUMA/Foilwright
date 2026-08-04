#!/usr/bin/env python3
"""Foilwright — MD-5500 へ RGL ジョブを送る参照送出器(L0)

Copyright (C) 2026 JunkQuality (github.com/KM-KUMA/Foilwright)
SPDX-License-Identifier: GPL-3.0-or-later

DOMAIN §15 の ALPS USB バルクプロトコルを実装している。プリンタは RGL の
生ストリームを受け付けないため、独自のパケット層に載せて運ぶ必要がある。

    OUT "05 ff"                          送信要求
    IN  "06"                             許可
    OUT "02 01 {len16} {payload}"        len16 = ペイロード長 - 1(LE)
    IN  "06"                             受理

バルク IN を読んでよいのは応答を返すコマンドの直後だけ。応答が無い状態で
読むとインターフェースがウェッジし、usbipd detach/attach か物理再接続でしか
回復しない(§11.1.1)。

実行環境: WSL2 + usbipd-win でパススルーした Linux 側。
    usbipd bind --busid <BUSID> --force
    usbipd attach --busid <BUSID> --wsl
    sudo python3 tools/alps_send.py dumps/phase1_blackraster.bin
"""

import argparse
import time

import usb.core

VID, PID = 0x044E, 0x2002
EP_OUT, EP_IN = 0x01, 0x82
MAX_PAYLOAD = 32764  # 総転送 32768 バイト - ヘッダ 4 バイト

REQ_SEND = b"\x05\xff"
REQ_STATUS = b"\x05\x01"
ACK = b"\x06"


def open_device():
    dev = usb.core.find(idVendor=VID, idProduct=PID)
    if dev is None:
        raise SystemExit(f"プリンタが見つからない ({VID:04x}:{PID:04x})")
    # GET_DEVICE_ID。これを先に打たないとステータス応答が返らない(§11.4)
    try:
        dev.ctrl_transfer(0xA1, 0, 0, 0, 512, timeout=3000)
    except usb.core.USBError as e:
        print(f"警告: GET_DEVICE_ID に失敗 ({e})")
    return dev


def read_status(dev):
    """38 バイトのカセット状態を返す。ヘッダ 5 + 11 スロット x 3 バイト。"""
    dev.write(EP_OUT, REQ_STATUS, timeout=3000)
    return bytes(dev.read(EP_IN, 128, timeout=3000))


def send_job(dev, rgl, progress=None):
    for offset in range(0, len(rgl), MAX_PAYLOAD):
        chunk = rgl[offset : offset + MAX_PAYLOAD]

        dev.write(EP_OUT, REQ_SEND, timeout=5000)
        reply = bytes(dev.read(EP_IN, 8, timeout=5000))
        if reply != ACK:
            raise RuntimeError(f"送信要求への応答が異常: {reply.hex(' ')}")

        header = b"\x02\x01" + (len(chunk) - 1).to_bytes(2, "little")
        dev.write(EP_OUT, header + chunk, timeout=30000)
        reply = bytes(dev.read(EP_IN, 8, timeout=30000))
        if reply != ACK:
            raise RuntimeError(f"データへの応答が異常: {reply.hex(' ')}")

        if progress:
            progress(offset + len(chunk), len(rgl))


def format_status(raw):
    slots = " ".join(f"{raw[5 + i * 3]:02x}" for i in range(11))
    return f"header={raw[:5].hex(' ')} slots={slots}"


def main():
    ap = argparse.ArgumentParser(description="MD-5500 へ RGL ジョブを送る")
    ap.add_argument("job", help="RGL バイト列のファイル")
    ap.add_argument("--poll", type=int, default=0, help="送出後に状態を追う秒数")
    args = ap.parse_args()

    with open(args.job, "rb") as f:
        rgl = f.read()

    dev = open_device()
    print(f"送出前: {format_status(read_status(dev))}")

    started = time.time()
    send_job(dev, rgl, progress=lambda done, total: print(f"  {done}/{total} バイト"))
    print(f"送出完了 {len(rgl)} バイト / {time.time() - started:.2f} 秒")

    for elapsed in range(3, args.poll + 1, 3):
        time.sleep(3)
        try:
            print(f"+{elapsed}s: {format_status(read_status(dev))}")
        except usb.core.USBError as e:
            print(f"+{elapsed}s: 応答なし (errno={e.errno})")


if __name__ == "__main__":
    main()
