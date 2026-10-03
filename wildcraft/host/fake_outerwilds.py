"""WildCraft FakeHost — Outer Wilds stand-in that speaks the SkyCraft protocol.

Creates Local\\SkyCraft_v1, streams a flat "Timber Hearth" floor, forwards real
keyboard/mouse into Minecraft's input ring, and prints the MC player puppet state.

Playable without Outer Wilds:

    # Terminal 1 — start Minecraft with the SkyCraft Fabric mod (window can stay visible
    # for debugging; for the real path use -Dskycraft.startHidden=true)
    #
    # Terminal 2:
    python wildcraft/host/fake_outerwilds.py

Controls (captured while this console is focused, or always if --global-input):
    WASD / Space / Shift / mouse  → Minecraft
    F12                           → save overlay PNG
    Esc (hold 1s) or Ctrl+C       → quit

Requires: Windows, Python 3.10+, SkyCraft Fabric mod running.
"""

from __future__ import annotations

import argparse
import ctypes
import mmap
import os
import struct
import sys
import time
import zlib
from ctypes import wintypes

# Keep in sync with protocol/skycraft_protocol.h (v10) and tools/fake_skyrim.py
MAGIC = 0x43594B53
VERSION = 10
NAME = os.environ.get("SKYCRAFT_LINK", "Local\\SkyCraft_v1")

OFF_SKY = 0x100
OFF_MC = 0x200
OFF_OVL = 0x300
OFF_OVL_HDR = 0x340
OFF_IN = 0x1000
OFF_ACTORS = 0x12000
OFF_EVENTS = 0x17000
OFF_ENTITIES = 0x1C000
OFF_COL = 0x20000
COL_BYTES = 32 << 20
OFF_PIX = OFF_COL + COL_BYTES
SLOT = 3840 * 2160 * 4
OFF_RENDER = OFF_PIX + SLOT * 3
RENDER_BYTES = 64 << 20
SIZE = OFF_RENDER + RENDER_BYTES
COL_DATA = COL_BYTES - 0x80

W, H = 1280, 720
FLOOR_Y = 80
# Far from SkyCraft Skyrim / fake_skyrim test origins
X0 = 200000

# SDL scancodes used by SkyCraft Fabric InputBridge
SDL = {
    "W": 26,
    "A": 4,
    "S": 22,
    "D": 7,
    "SPACE": 44,
    "LSHIFT": 225,
    "LCTRL": 224,
    "E": 8,
    "Q": 20,
    "T": 23,
    "1": 30,
    "2": 31,
    "3": 32,
    "4": 33,
    "5": 34,
    "6": 35,
    "7": 36,
    "8": 37,
    "9": 38,
    "F5": 62,
    "F3": 60,
}

# Virtual-key → SDL name
VK_MAP = {
    0x57: "W",
    0x41: "A",
    0x53: "S",
    0x44: "D",
    0x20: "SPACE",
    0xA0: "LSHIFT",  # VK_LSHIFT
    0x10: "LSHIFT",  # VK_SHIFT
    0xA2: "LCTRL",
    0x45: "E",
    0x51: "Q",
    0x54: "T",
    0x31: "1",
    0x32: "2",
    0x33: "3",
    0x34: "4",
    0x35: "5",
    0x36: "6",
    0x37: "7",
    0x38: "8",
    0x39: "9",
    0x74: "F5",
    0x72: "F3",
}

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
kernel32.GetTickCount64.restype = ctypes.c_uint64


def tick() -> int:
    return kernel32.GetTickCount64()


class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]


class Link:
    def __init__(self):
        self.m = mmap.mmap(-1, SIZE, tagname=NAME)
        self.m[0:0x100] = bytes(0x100)
        for off, n in (
            (OFF_SKY, 0x40),
            (OFF_OVL, 0x100),
            (OFF_IN, 0x80),
            (OFF_COL, 0x80),
            (OFF_ACTORS, 0x40),
            (OFF_EVENTS, 0x80),
            (OFF_ENTITIES, 0x40),
            (OFF_RENDER, 0x80),
        ):
            self.m[off : off + n] = bytes(n)
        struct.pack_into("<IIII", self.m, 0, MAGIC, VERSION, os.getpid(), 0)
        self.front = 2
        self.sky_seq = 0
        self.col_head = 0
        self.events: list[int] = []

    def heartbeat(self):
        struct.pack_into("<Q", self.m, 0x10, tick())
        self.drain_events()

    def drain_events(self):
        head = struct.unpack_from("<Q", self.m, OFF_EVENTS)[0]
        tail = struct.unpack_from("<Q", self.m, OFF_EVENTS + 0x40)[0]
        while tail < head:
            typ, form, a, b, c, d, flags = struct.unpack_from(
                "<IIffffI", self.m, OFF_EVENTS + 0x80 + (tail % 512) * 32
            )
            print(
                f"  [mc→host] event type={typ} form={form:08X} "
                f"a={a:.2f} b={b:.2f} c={c:.2f} d={d:.2f} flags={flags:#x}"
            )
            self.events.append(typ)
            tail += 1
        struct.pack_into("<Q", self.m, OFF_EVENTS + 0x40, tail)

    def mc_alive(self) -> bool:
        (beat,) = struct.unpack_from("<Q", self.m, 0x18)
        return beat != 0 and tick() - beat < 8000

    def write_sky(self, flags, pos, yaw, pitch, teleport_seq, epoch, world_id=0x57494C44):
        # world_id 'WILD' — Fabric only uses it for collision epoch / dimension switches
        self.sky_seq += 1
        struct.pack_into("<I", self.m, OFF_SKY, self.sky_seq * 2 - 1)
        struct.pack_into(
            "<IIIdddffIIIf",
            self.m,
            OFF_SKY + 4,
            flags,
            world_id,
            epoch,
            pos[0],
            pos[1],
            pos[2],
            yaw,
            pitch,
            teleport_seq,
            W,
            H,
            12.0,
        )
        struct.pack_into("<I", self.m, OFF_SKY, self.sky_seq * 2)

    def read_mc(self):
        raw = self.m[OFF_MC : OFF_MC + 0x68]
        (
            seq,
            flags,
            x,
            y,
            z,
            yaw,
            pitch,
            eye,
            sens,
            ack,
            gui,
            frame,
            fov,
            bph,
            bam,
            _,
            ex,
            ey,
            ez,
        ) = struct.unpack("<IIdddffffIIQfffIddd", raw)
        return {
            "flags": flags,
            "pos": (x, y, z),
            "yaw": yaw,
            "pitch": pitch,
            "eye": eye,
            "ack": ack,
            "frame": frame,
            "gui": gui,
        }

    def push_input(self, typ, code, a=0, b=0, c=0):
        a, b, c = (v - (1 << 32) if v >= 1 << 31 else v for v in (a, b, c))
        (head,) = struct.unpack_from("<Q", self.m, OFF_IN)
        struct.pack_into("<HHiii", self.m, OFF_IN + 0x80 + (head % 4096) * 16, typ, code, a, b, c)
        struct.pack_into("<Q", self.m, OFF_IN, head + 1)

    def write_col(self, typ, payload):
        msg = (8 + len(payload) + 7) & ~7
        pos = self.col_head % COL_DATA
        if pos + msg > COL_DATA:
            struct.pack_into("<II", self.m, OFF_COL + 0x80 + pos, 0, 0)
            self.col_head += COL_DATA - pos
            pos = 0
        base = OFF_COL + 0x80 + pos
        struct.pack_into("<II", self.m, base, typ, len(payload))
        self.m[base + 8 : base + 8 + len(payload)] = payload
        self.col_head += msg
        struct.pack_into("<Q", self.m, OFF_COL, self.col_head)

    def acquire_overlay(self):
        (state,) = struct.unpack_from("<I", self.m, OFF_OVL)
        if not state & 4:
            return None
        struct.pack_into("<I", self.m, OFF_OVL, self.front)
        self.front = state & 3
        w, h, flags = struct.unpack_from("<III", self.m, OFF_OVL_HDR + self.front * 0x40)
        start = OFF_PIX + self.front * SLOT
        return w, h, flags, bytes(self.m[start : start + w * h * 4])


FULL = [0xFFFFFFFFFFFFFFFF] * 8


def region_payload(rx, ry, rz, epoch, block_fn):
    blocks = []
    for by in range(8):
        for bz in range(8):
            for bx in range(8):
                x, y, z = rx * 8 + bx, ry * 8 + by, rz * 8 + bz
                bits = block_fn(x, y, z)
                if bits and any(bits):
                    blocks.append(struct.pack("<iiiI8Q", x, y, z, 0, *bits))
    head = struct.pack(
        "<iiiiiiII",
        rx * 8,
        ry * 8,
        rz * 8,
        rx * 8 + 7,
        ry * 8 + 7,
        rz * 8 + 7,
        epoch,
        len(blocks),
    )
    return head + b"".join(blocks)


def floor_tris_payload(rx, ry, rz, epoch):
    y = float(FLOOR_Y)
    tris = []
    if ry * 8 <= FLOOR_Y - 1 <= ry * 8 + 7:
        x0, z0 = rx * 8.0, rz * 8.0
        x1, z1 = x0 + 8.0, z0 + 8.0
        tris.append(struct.pack("<9fI", x0, y, z0, x1, y, z0, x1, y, z1, 0))
        tris.append(struct.pack("<9fI", x0, y, z0, x1, y, z1, x0, y, z1, 0))
    head = struct.pack(
        "<iiiiiiII",
        rx * 8,
        ry * 8,
        rz * 8,
        rx * 8 + 7,
        ry * 8 + 7,
        rz * 8 + 7,
        epoch,
        len(tris),
    )
    return head + b"".join(tris)


def hearth_world(x, y, z):
    """Flat Timber Hearth stand-in + a small rock staircase toward +X."""
    lx = x - X0
    if y == FLOOR_Y - 1:
        return FULL
    if 3 <= lx < 11 and -2 <= z <= 2 and y == FLOOR_Y + (lx - 3) // 2:
        return FULL if (lx - 3) % 2 == 1 else [0xFFFFFFFFFFFFFFFF] * 4 + [0] * 4
    return None


def save_png(path, w, h, rgba, bottom_up):
    rows = [rgba[r * w * 4 : (r + 1) * w * 4] for r in range(h)]
    if bottom_up:
        rows.reverse()
    raw = b"".join(b"\x00" + row for row in rows)

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


class InputBridge:
    """Poll Win32 keys/mouse and push SkyCraft input events."""

    def __init__(self, link: Link, sensitivity: float = 0.15):
        self.link = link
        self.sensitivity = sensitivity
        self.prev_keys: dict[int, bool] = {vk: False for vk in VK_MAP}
        self.prev_buttons = {1: False, 2: False, 3: False}
        self.last_cursor = POINT()
        user32.GetCursorPos(ctypes.byref(self.last_cursor))
        self.yaw = -90.0
        self.pitch = 10.0
        self._f12_was = False

    def poll(self, capture_mouse: bool) -> tuple[float, float]:
        # Keys
        for vk, name in VK_MAP.items():
            down = bool(user32.GetAsyncKeyState(vk) & 0x8000)
            was = self.prev_keys[vk]
            if down != was:
                self.link.push_input(1, SDL[name], 1 if down else 0)
                self.prev_keys[vk] = down

        # Mouse buttons: 1=L 2=R 3=M in SDL; Win32 VK 0x01, 0x02, 0x04
        for sdl_btn, vk in ((1, 0x01), (3, 0x02), (2, 0x04)):
            down = bool(user32.GetAsyncKeyState(vk) & 0x8000)
            was = self.prev_buttons[sdl_btn]
            if down != was:
                self.link.push_input(2, sdl_btn, 1 if down else 0)
                self.prev_buttons[sdl_btn] = down

        # Look
        if capture_mouse:
            cur = POINT()
            user32.GetCursorPos(ctypes.byref(cur))
            dx = cur.x - self.last_cursor.x
            dy = cur.y - self.last_cursor.y
            # Recenter to keep relative motion (fake "OW window focus")
            cx, cy = W // 2, H // 2
            user32.SetCursorPos(cx, cy)
            self.last_cursor.x, self.last_cursor.y = cx, cy
            if dx or dy:
                self.yaw = (self.yaw + dx * self.sensitivity) % 360.0
                self.pitch = max(-89.0, min(89.0, self.pitch + dy * self.sensitivity))
        else:
            user32.GetCursorPos(ctypes.byref(self.last_cursor))

        # F12 → overlay dump (handled by caller via edge detect)
        f12 = bool(user32.GetAsyncKeyState(0x7B) & 0x8000)
        dump = f12 and not self._f12_was
        self._f12_was = f12
        self._want_dump = dump
        return self.yaw, self.pitch

    @property
    def want_dump(self) -> bool:
        return getattr(self, "_want_dump", False)


def send_collision(link: Link, epoch: int):
    link.write_col(1, struct.pack("<I", epoch))
    for rx in range(X0 // 8 - 4, X0 // 8 + 4):
        for rz in range(-4, 4):
            for ry in range(FLOOR_Y // 8 - 1, FLOOR_Y // 8 + 3):
                link.write_col(3, floor_tris_payload(rx, ry, rz, epoch))
                link.write_col(2, region_payload(rx, ry, rz, epoch, hearth_world))


def main():
    ap = argparse.ArgumentParser(description="WildCraft FakeHost (Outer Wilds stand-in)")
    ap.add_argument("--seconds", type=float, default=0, help="Quit after N seconds (0 = until Ctrl+C)")
    ap.add_argument("--no-mouse-capture", action="store_true", help="Don't lock the cursor (WASD only)")
    ap.add_argument("--overlay", default="wildcraft_overlay.png", help="F12 overlay path")
    args = ap.parse_args()

    link = Link()
    print("WildCraft FakeHost up")
    print(f"  mapping: {NAME}")
    print(f"  spawn:   ({X0 + 0.5}, {FLOOR_Y}, 0.5)  [Timber Hearth stand-in]")
    print("  waiting for Minecraft (SkyCraft Fabric)...")
    print("  tip: focus this console for WASD; F12 saves HUD overlay PNG")

    epoch = 1
    sent = False
    spawn = (X0 + 0.5, float(FLOOR_Y), 0.5)
    tseq = int(time.time()) % 100000 + 2
    bridge = InputBridge(link)
    start = time.time()
    last_print = 0.0
    capture = not args.no_mouse_capture

    try:
        while True:
            if args.seconds and time.time() - start >= args.seconds:
                break
            link.heartbeat()
            yaw, pitch = bridge.poll(capture and link.mc_alive())
            mc = link.read_mc()
            # Look is authoritative from the host (same as SkyCraft Camera/Input path).
            # Position in SkyState is only used for the initial teleport (tseq); after ack,
            # Minecraft owns feet — we still echo spawn so we never re-teleport.
            link.write_sky(1, spawn, yaw, pitch, tseq, epoch)

            if link.mc_alive() and not sent:
                send_collision(link, epoch)
                sent = True
                print("collision sent (Timber Hearth flat patch)")

            if bridge.want_dump:
                frame = link.acquire_overlay()
                if frame:
                    w, h, flags, px = frame
                    save_png(args.overlay, w, h, px, flags & 1)
                    print(f"saved overlay {w}x{h} → {args.overlay}")
                else:
                    print("no overlay frame ready yet")

            if time.time() - last_print > 1.0:
                last_print = time.time()
                alive = "yes" if link.mc_alive() else "no"
                print(
                    f"t={time.time()-start:6.1f} mc={alive} flags={mc['flags']:#x} "
                    f"ack={mc['ack']}/{tseq} "
                    f"pos=({mc['pos'][0]:.2f},{mc['pos'][1]:.2f},{mc['pos'][2]:.2f}) "
                    f"look=({yaw:.0f},{pitch:.0f}) frame={mc['frame']}"
                )
            time.sleep(1 / 60)
    except KeyboardInterrupt:
        print("\nshutting down")
    print(f"done. events from MC: {link.events}")


if __name__ == "__main__":
    main()
