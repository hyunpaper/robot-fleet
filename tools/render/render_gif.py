"""TraceExport가 만든 트레이스(JSON)를 GIF로 그린다.

  python tools/render/render_gif.py <trace.json> <out.gif> [--view iso|2d]

경로는 미리 그리지 않고, 로봇이 지나간 칸이 그 로봇 색으로 물든다(같은 칸은 마지막 로봇 색).
"""

import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

SS = 2  # 슈퍼샘플링 배율

BG = (233, 236, 241)
AISLE = (247, 248, 250)
GRID = (222, 226, 232)
BLOCK_TOP = (150, 160, 175)
BLOCK_SOUTH = (112, 122, 138)
BLOCK_EAST = (95, 104, 120)
BLOCK_EDGE = (78, 86, 100)
SLAB_SOUTH = (196, 201, 209)
SLAB_EAST = (180, 186, 196)
TEXT = (40, 46, 56)
TEXT_DIM = (110, 118, 130)

PLACE_COLORS = {
    "pickup": (245, 190, 70),
    "dropoff": (238, 128, 70),
    "charger": (90, 185, 230),
    "parking": (200, 206, 216),
}
PLACE_LABELS = {"pickup": "Pickup", "dropoff": "Dropoff", "charger": "Charger", "parking": "Parking"}

ROBOT_COLORS = [
    (37, 99, 235),
    (220, 38, 38),
    (22, 163, 74),
    (124, 58, 237),
    (219, 39, 119),
    (13, 148, 136),
]
TRAIL_MIX = 0.5

STATUS_LABELS = {
    "Idle": "Idle",
    "Moving": "Moving",
    "Waiting": "Waiting",
    "Loading": "Loading",
    "Unloading": "Unloading",
    "Charging": "Charging",
    "Error": "Error",
}

HEADING_DEG = [-90.0, 0.0, 90.0, 180.0]  # N, E, S, W (격자 좌표, y 아래 증가)


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def shade(c, f):
    return tuple(max(0, min(255, int(round(v * f)))) for v in c)


def load_font(size, bold=False):
    names = (["consolab.ttf", "DejaVuSansMono-Bold.ttf", "Menlo.ttc"] if bold
             else ["consola.ttf", "DejaVuSansMono.ttf", "Menlo.ttc"])
    for name in names:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default(size)


class Sim:
    """트레이스와 틱 단위 궤적 상태."""

    def __init__(self, path):
        t = json.loads(Path(path).read_text(encoding="utf-8"))
        self.map_id = t["mapId"]
        self.w, self.h = t["width"], t["height"]
        self.grid = [[int(c) for c in row] for row in t["grid"]]
        self.places = t["places"]
        self.place_at = {(p["x"], p["y"]): p["kind"] for p in self.places}
        self.tick_ms = t["tickMs"]
        self.robots = t["robots"]
        self.ticks = t["ticks"]
        self.trail = {}
        self._painted_until = -1

    def blocked(self, x, y):
        return 0 <= x < self.w and 0 <= y < self.h and self.grid[y][x] == 0

    def advance_trail(self, tick_index):
        for k in range(self._painted_until + 1, tick_index + 1):
            for i, r in enumerate(self.ticks[k]["robots"]):
                self.trail[(r["x"], r["y"])] = i
                if r.get("act") == "Move" and r.get("p", 0) >= 0.5:
                    self.trail[(r["tx"], r["ty"])] = i
        self._painted_until = max(self._painted_until, tick_index)

    @staticmethod
    def pose(r):
        """(연속 x, 연속 y, 진행 방향 각도). 칸 중심 기준."""
        x, y = r["x"] + 0.5, r["y"] + 0.5
        ang = HEADING_DEG[r["h"]]
        act, p = r.get("act"), r.get("p", 0.0)
        if act == "Move":
            x += (r["tx"] - r["x"]) * p
            y += (r["ty"] - r["y"]) * p
        elif act == "Turn":
            d = (r["th"] - r["h"]) % 4
            ang += {1: 90.0, 2: 180.0, 3: -90.0}.get(d, 0.0) * p
        return x, y, math.radians(ang)

    def components(self):
        """막힌 칸의 연결 요소 번호. 설비 블록마다 높이를 달리 주는 데 쓴다."""
        comp = {}
        n = 0
        for y in range(self.h):
            for x in range(self.w):
                if self.grid[y][x] != 0 or (x, y) in comp:
                    continue
                stack = [(x, y)]
                comp[(x, y)] = n
                while stack:
                    cx, cy = stack.pop()
                    for nx, ny in ((cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1)):
                        if self.blocked(nx, ny) and (nx, ny) not in comp:
                            comp[(nx, ny)] = n
                            stack.append((nx, ny))
                n += 1
        return comp


def footprint(cx, cy, ang, size):
    f = (math.cos(ang), math.sin(ang))
    s = (-f[1], f[0])
    hs = size / 2
    return [(cx + f[0] * a * hs + s[0] * b * hs, cy + f[1] * a * hs + s[1] * b * hs)
            for a, b in ((1, -1), (1, 1), (-1, 1), (-1, -1))], f, s


class IsoView:
    TW, TH, ZU = 22, 15, 7  # 타일 폭, 타일 높이, 높이 1단위의 픽셀 (1배 기준)
    ROBOT_SIZE, ROBOT_H = 0.84, 0.9
    MARGIN = 18

    def __init__(self, sim):
        self.sim = sim
        w, h = sim.w, sim.h
        self.ox = self.MARGIN + h * self.TW / 2
        self.oy = self.MARGIN + 30
        self.width = int(self.MARGIN * 2 + (w + h) * self.TW / 2)
        self.height = int(self.oy + (w + h) * self.TH / 2 + 12 + self.MARGIN)
        comp = sim.components()
        self.block_h = {c: 0.6 + 0.35 * ((i * 7) % 5) / 4 for c, i in comp.items()}
        self.items = sorted(((x + y + 1.0, x, y) for (x, y) in comp))

    def p(self, gx, gy, z=0.0):
        return ((self.ox + (gx - gy) * self.TW / 2) * SS,
                (self.oy + (gx + gy) * self.TH / 2 - z * self.ZU) * SS)

    def poly(self, d, pts, fill, outline=None, z=0.0):
        d.polygon([self.p(x, y, z) for x, y in pts], fill=fill, outline=outline)

    def draw(self, tick_index):
        sim = self.sim
        img = Image.new("RGB", (self.width * SS, self.height * SS), BG)
        d = ImageDraw.Draw(img)
        w, h = sim.w, sim.h

        # 바닥판 두께
        slab = 0.9
        d.polygon([self.p(0, h), self.p(w, h), self.p(w, h, -slab), self.p(0, h, -slab)], fill=SLAB_SOUTH)
        d.polygon([self.p(w, 0), self.p(w, h), self.p(w, h, -slab), self.p(w, 0, -slab)], fill=SLAB_EAST)

        for y in range(h):
            for x in range(w):
                if sim.grid[y][x] == 0:
                    continue
                base = AISLE
                if (x, y) in sim.trail:
                    base = mix(AISLE, ROBOT_COLORS[sim.trail[(x, y)] % len(ROBOT_COLORS)], TRAIL_MIX)
                self.poly(d, [(x, y), (x + 1, y), (x + 1, y + 1), (x, y + 1)], base, GRID)
                kind = sim.place_at.get((x, y))
                if kind:
                    m = 0.22
                    self.poly(d, [(x + m, y + m), (x + 1 - m, y + m), (x + 1 - m, y + 1 - m), (x + m, y + 1 - m)],
                              PLACE_COLORS[kind], shade(PLACE_COLORS[kind], 0.8))

        robots = []
        for i, r in enumerate(sim.ticks[tick_index]["robots"]):
            cx, cy, ang = sim.pose(r)
            robots.append((cx + cy, i, cx, cy, ang))
        robots.sort()

        ri = 0
        for depth, x, y in self.items:
            while ri < len(robots) and robots[ri][0] < depth:
                self.draw_robot(d, *robots[ri][1:])
                ri += 1
            self.draw_block(d, x, y)
        while ri < len(robots):
            self.draw_robot(d, *robots[ri][1:])
            ri += 1

        return img.resize((self.width, self.height), Image.LANCZOS)

    def draw_block(self, d, x, y):
        sim = self.sim
        z = self.block_h[(x, y)]
        if not sim.blocked(x, y + 1):
            d.polygon([self.p(x, y + 1), self.p(x + 1, y + 1), self.p(x + 1, y + 1, z), self.p(x, y + 1, z)],
                      fill=BLOCK_SOUTH)
        if not sim.blocked(x + 1, y):
            d.polygon([self.p(x + 1, y), self.p(x + 1, y + 1), self.p(x + 1, y + 1, z), self.p(x + 1, y, z)],
                      fill=BLOCK_EAST)
        self.poly(d, [(x, y), (x + 1, y), (x + 1, y + 1), (x, y + 1)], BLOCK_TOP, z=z)
        lw = SS
        edges = [((x, y), (x + 1, y), sim.blocked(x, y - 1)),
                 ((x + 1, y), (x + 1, y + 1), sim.blocked(x + 1, y)),
                 ((x, y + 1), (x + 1, y + 1), sim.blocked(x, y + 1)),
                 ((x, y), (x, y + 1), sim.blocked(x - 1, y))]
        for a, b, inner in edges:
            if not inner:
                d.line([self.p(*a, z), self.p(*b, z)], fill=BLOCK_EDGE, width=lw)
        if not sim.blocked(x, y + 1) and not sim.blocked(x + 1, y):
            d.line([self.p(x + 1, y + 1), self.p(x + 1, y + 1, z)], fill=BLOCK_EDGE, width=lw)

    def draw_robot(self, d, i, cx, cy, ang):
        color = ROBOT_COLORS[i % len(ROBOT_COLORS)]
        hgt = self.ROBOT_H
        pts, f, s = footprint(cx, cy, ang, self.ROBOT_SIZE)
        # 그림자
        sh, _, _ = footprint(cx + 0.06, cy + 0.06, ang, self.ROBOT_SIZE * 1.08)
        self.poly(d, sh, shade(BG, 0.78))
        faces = []
        for k in range(4):
            a, b = pts[k], pts[(k + 1) % 4]
            nx, ny = (b[1] - a[1]), -(b[0] - a[0])
            ln = math.hypot(nx, ny) or 1
            nx, ny = nx / ln, ny / ln
            mx, my = (a[0] + b[0]) / 2 - cx, (a[1] + b[1]) / 2 - cy
            if nx * mx + ny * my < 0:
                nx, ny = -nx, -ny
            vis = nx + ny
            if vis <= 0:
                continue
            light = 0.62 + 0.25 * max(0.0, nx * 0.3 + ny * 0.95)
            faces.append(((a[0] + b[0]) + (a[1] + b[1]), a, b, shade(color, light)))
        faces.sort()
        for _, a, b, col in faces:
            d.polygon([self.p(*a), self.p(*b), self.p(*b, hgt), self.p(*a, hgt)], fill=col)
        top = mix(color, (255, 255, 255), 0.12)
        self.poly(d, pts, top, shade(color, 0.55), z=hgt)
        nose = [(cx + f[0] * 0.27, cy + f[1] * 0.27),
                (cx - f[0] * 0.12 + s[0] * 0.19, cy - f[1] * 0.12 + s[1] * 0.19),
                (cx - f[0] * 0.04, cy - f[1] * 0.04),
                (cx - f[0] * 0.12 - s[0] * 0.19, cy - f[1] * 0.12 - s[1] * 0.19)]
        self.poly(d, nose, (255, 255, 255), z=hgt)



class TopView:
    CS = 14
    MARGIN = 14
    TOP = 34
    BOTTOM = 86
    ROBOT_SIZE = 0.78

    def __init__(self, sim):
        self.sim = sim
        self.width = self.MARGIN * 2 + sim.w * self.CS
        self.height = self.TOP + sim.h * self.CS + self.BOTTOM

    def p(self, gx, gy):
        return ((self.MARGIN + gx * self.CS) * SS, (self.TOP + gy * self.CS) * SS)

    def rect(self, d, x, y, m, fill, outline=None):
        a = self.p(x + m, y + m)
        b = self.p(x + 1 - m, y + 1 - m)
        d.rectangle([a[0], a[1], b[0] - 1, b[1] - 1], fill=fill, outline=outline)

    def draw(self, tick_index):
        sim = self.sim
        img = Image.new("RGB", (self.width * SS, self.height * SS), BG)
        d = ImageDraw.Draw(img)
        for y in range(sim.h):
            for x in range(sim.w):
                if sim.grid[y][x] == 0:
                    continue
                base = AISLE
                if (x, y) in sim.trail:
                    base = mix(AISLE, ROBOT_COLORS[sim.trail[(x, y)] % len(ROBOT_COLORS)], TRAIL_MIX)
                self.rect(d, x, y, 0, base, GRID)
                kind = sim.place_at.get((x, y))
                if kind:
                    self.rect(d, x, y, 0.24, PLACE_COLORS[kind], shade(PLACE_COLORS[kind], 0.8))
        # 설비: 그림자 → 윗면
        for y in range(sim.h):
            for x in range(sim.w):
                if sim.grid[y][x] == 0:
                    a = self.p(x + 0.12, y + 0.12)
                    b = self.p(x + 1.12, y + 1.12)
                    d.rectangle([a[0], a[1], b[0] - 1, b[1] - 1], fill=shade(BG, 0.86))
        for y in range(sim.h):
            for x in range(sim.w):
                if sim.grid[y][x] == 0:
                    self.rect(d, x, y, 0, BLOCK_TOP)
        lw = SS
        for y in range(sim.h):
            for x in range(sim.w):
                if sim.grid[y][x] != 0:
                    continue
                if not sim.blocked(x, y - 1):
                    d.line([self.p(x, y), self.p(x + 1, y)], fill=BLOCK_EDGE, width=lw)
                if not sim.blocked(x, y + 1):
                    d.line([self.p(x, y + 1), self.p(x + 1, y + 1)], fill=BLOCK_EDGE, width=lw)
                if not sim.blocked(x - 1, y):
                    d.line([self.p(x, y), self.p(x, y + 1)], fill=BLOCK_EDGE, width=lw)
                if not sim.blocked(x + 1, y):
                    d.line([self.p(x + 1, y), self.p(x + 1, y + 1)], fill=BLOCK_EDGE, width=lw)

        for i, r in enumerate(sim.ticks[tick_index]["robots"]):
            cx, cy, ang = sim.pose(r)
            color = ROBOT_COLORS[i % len(ROBOT_COLORS)]
            pts, f, s = footprint(cx, cy, ang, self.ROBOT_SIZE)
            d.polygon([self.p(x, y) for x, y in pts], fill=color, outline=shade(color, 0.55))
            nose = [(cx + f[0] * 0.3, cy + f[1] * 0.3),
                    (cx - f[0] * 0.14 + s[0] * 0.22, cy - f[1] * 0.14 + s[1] * 0.22),
                    (cx - f[0] * 0.05, cy - f[1] * 0.05),
                    (cx - f[0] * 0.14 - s[0] * 0.22, cy - f[1] * 0.14 - s[1] * 0.22)]
            d.polygon([self.p(x, y) for x, y in nose], fill=(255, 255, 255))
        return img.resize((self.width, self.height), Image.LANCZOS)


def robot_line(sim, i, r):
    rid = sim.robots[i]["id"]
    status = STATUS_LABELS.get(r["status"], r["status"])
    goal = r.get("goal")
    stops = sim.robots[i]["stops"]
    if goal:
        where = f"-> {goal}"
    elif r["done"] == len(stops):
        where = f"@ {stops[-1]}"
    elif r["done"] > 0:
        where = f"@ {stops[r['done'] - 1]}"
    else:
        where = ""
    return f"{rid}  {status:<10}{where:<9} {r['done']}/{len(stops)}"


def draw_swatch(d, x, y, size, color):
    d.rectangle([x, y, x + size - 1, y + size - 1], fill=color, outline=shade(color, 0.6))


def hud_iso(img, view, sim, tick_index, fonts):
    d = ImageDraw.Draw(img)
    big, small = fonts
    tick = sim.ticks[tick_index]["tick"]
    m = view.MARGIN
    d.text((m, m - 4), f"{sim.map_id}  ·  {len(sim.robots)} robots", font=big, fill=TEXT)
    d.text((m, m + 18), f"tick {tick:05d}   t = {tick * sim.tick_ms / 1000:6.1f}s", font=small, fill=TEXT_DIM)

    x0 = int(view.width - m - 300)
    y = m - 2
    for i, r in enumerate(sim.ticks[tick_index]["robots"]):
        draw_swatch(d, x0, y + 3, 11, ROBOT_COLORS[i % len(ROBOT_COLORS)])
        d.text((x0 + 18, y), robot_line(sim, i, r), font=small, fill=TEXT)
        y += 17

    lx = m
    ly = view.height - m - 16 * 5
    legend = [(PLACE_COLORS[k], PLACE_LABELS[k]) for k in ("pickup", "dropoff", "charger", "parking")]
    legend.append((BLOCK_TOP, "Equipment"))
    for color, label in legend:
        draw_swatch(d, lx, ly + 3, 10, color)
        d.text((lx + 16, ly), label, font=small, fill=TEXT_DIM)
        ly += 16


def hud_2d(img, view, sim, tick_index, fonts):
    d = ImageDraw.Draw(img)
    big, small = fonts
    tick = sim.ticks[tick_index]["tick"]
    m = view.MARGIN
    d.text((m, 8), f"{sim.map_id}  ·  {len(sim.robots)} robots", font=big, fill=TEXT)
    right = f"tick {tick:05d}   t = {tick * sim.tick_ms / 1000:6.1f}s"
    d.text((view.width - m - d.textlength(right, font=small), 11), right, font=small, fill=TEXT_DIM)

    top = view.TOP + sim.h * view.CS + 8
    col_w = (view.width - 2 * m) // 2
    for i, r in enumerate(sim.ticks[tick_index]["robots"]):
        x = m + (i % 2) * col_w
        y = top + (i // 2) * 17
        draw_swatch(d, x, y + 3, 11, ROBOT_COLORS[i % len(ROBOT_COLORS)])
        d.text((x + 18, y), robot_line(sim, i, r), font=small, fill=TEXT)

    legend = [(PLACE_COLORS[k], PLACE_LABELS[k]) for k in ("pickup", "dropoff", "charger", "parking")]
    legend.append((BLOCK_TOP, "Equipment"))
    x = m
    y = view.height - 22
    for color, label in legend:
        draw_swatch(d, x, y + 3, 10, color)
        d.text((x + 15, y), label, font=small, fill=TEXT_DIM)
        x += 15 + int(d.textlength(label, font=small)) + 18


def frame_indices(n_ticks, ticks_per_frame):
    idx = list(range(0, n_ticks, ticks_per_frame))
    if idx[-1] != n_ticks - 1:
        idx.append(n_ticks - 1)
    return idx


def key_colors():
    """팔레트에 반드시 넣을 색. 면적이 작은 로봇 색이 양자화에서 뭉개지지 않게 한다."""
    colors = [BG, AISLE, GRID, BLOCK_TOP, BLOCK_SOUTH, BLOCK_EAST, BLOCK_EDGE, SLAB_SOUTH, SLAB_EAST, TEXT, TEXT_DIM,
              (255, 255, 255), shade(BG, 0.78), shade(BG, 0.86)]
    for c in PLACE_COLORS.values():
        colors += [c, shade(c, 0.8), shade(c, 0.6)]
    for c in ROBOT_COLORS:
        colors += [c, mix(c, (255, 255, 255), 0.12), mix(AISLE, c, TRAIL_MIX), shade(c, 0.55), shade(c, 0.6)]
        colors += [shade(c, f / 100) for f in range(62, 90, 5)]
    return list(dict.fromkeys(colors))


def save_gif(frames, out, frame_ms, hold_first_ms, hold_last_ms):
    # 모든 프레임에 같은 팔레트를 쓰고, 이전 프레임과 같은 픽셀은 투명(255)으로 비워 용량을 줄인다
    keys = key_colors()
    sample = Image.new("RGB", (frames[0].width, frames[0].height * 3))
    for k, f in enumerate((frames[0], frames[len(frames) // 2], frames[-1])):
        sample.paste(f, (0, frames[0].height * k))
    rest = sample.quantize(colors=255 - len(keys), method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    flat = [v for c in keys for v in c] + rest.getpalette()[: (255 - len(keys)) * 3]
    pal_img = Image.new("P", (1, 1))
    pal_img.putpalette(flat + [0] * (256 * 3 - len(flat)))
    palette = flat[: 255 * 3] + [0] * (255 * 3 - len(flat)) + [255, 0, 255]

    indexed = [np.asarray(f.quantize(colors=255, palette=pal_img, dither=Image.Dither.NONE)) for f in frames]
    out_frames = []
    for k, cur in enumerate(indexed):
        arr = cur.copy()
        if k > 0:
            arr[cur == indexed[k - 1]] = 255
        im = Image.fromarray(arr, mode="P")
        im.putpalette(palette)
        out_frames.append(im)

    durations = [frame_ms] * len(out_frames)
    durations[0] = hold_first_ms
    durations[-1] = hold_last_ms
    out_frames[0].save(out, save_all=True, append_images=out_frames[1:], duration=durations, loop=0,
                       transparency=255, disposal=1, optimize=False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("trace")
    ap.add_argument("out")
    ap.add_argument("--view", choices=["iso", "2d"], default="iso")
    ap.add_argument("--ticks-per-frame", type=int, default=4)
    ap.add_argument("--frame-ms", type=int, default=60)
    ap.add_argument("--hold-first-ms", type=int, default=800)
    ap.add_argument("--hold-last-ms", type=int, default=2000)
    ap.add_argument("--frames-dir", help="확인용으로 일부 프레임을 PNG로 저장할 디렉터리")
    args = ap.parse_args()

    sim = Sim(args.trace)
    view = IsoView(sim) if args.view == "iso" else TopView(sim)
    hud = hud_iso if args.view == "iso" else hud_2d
    fonts = (load_font(17, bold=True), load_font(13))

    idx = frame_indices(len(sim.ticks), args.ticks_per_frame)
    frames = []
    for k in idx:
        sim.advance_trail(k)
        img = view.draw(k)
        hud(img, view, sim, k, fonts)
        frames.append(img)

    if args.frames_dir:
        d = Path(args.frames_dir)
        d.mkdir(parents=True, exist_ok=True)
        for q in (0.1, 0.35, 0.65, 1.0):
            j = min(len(frames) - 1, int(q * (len(frames) - 1)))
            frames[j].save(d / f"{args.view}-{j:04d}.png")

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    save_gif(frames, out, args.frame_ms, args.hold_first_ms, args.hold_last_ms)
    total = args.frame_ms * (len(frames) - 2) + args.hold_first_ms + args.hold_last_ms
    print(f"{out}: {len(frames)} frames, {total / 1000:.1f}s, {out.stat().st_size / 1e6:.2f} MB")


if __name__ == "__main__":
    main()
