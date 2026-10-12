"""TraceExport가 만든 트레이스(JSON)를 끊김 없이 반복되는 GIF로 그린다.

  python tools/render/render_gif.py <trace.json> <out.gif> [--view iso|2d]

트레이스의 한 주기 [loopStart, loopStart + loopTicks)만 녹화한다.
로봇이 지나간 칸은 그 로봇 색으로 물들었다가 시간이 지나면 옅어진다. 옅어지는 창이 주기보다 짧아서 궤적도 주기적이다.
모든 효과는 트레이스 상태와 주기를 나누는 펄스 주기로만 정해져 프레임 0과 한 주기 뒤 프레임이 같다.
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
WHITE = (255, 255, 255)
CARGO = (199, 150, 82)
CARGO_TAPE = (240, 222, 180)
BAR_BG = (255, 255, 255)
DARK = (44, 50, 62)
WAIT_RED = (225, 29, 72)

PLACE_COLORS = {
    "pickup": (245, 190, 70),
    "dropoff": (238, 128, 70),
    "charger": (90, 185, 230),
    "parking": (200, 206, 216),
}

ROBOT_COLORS = [
    (37, 99, 235),
    (220, 38, 38),
    (22, 163, 74),
    (124, 58, 237),
    (219, 39, 119),
    (13, 148, 136),
    (8, 145, 178),
    (101, 163, 13),
    (192, 38, 211),
    (79, 70, 229),
    (180, 83, 9),
    (202, 138, 4),
]
TRAIL_MIX = 0.55
TRAIL_LEVELS = 6
FADE_TICKS = 300       # 궤적이 사라지는 데 걸리는 틱. 주기보다 짧아야 한다
PULSE_TICKS = 20       # 작업 중 설비 펄스 주기. 루프 길이를 나눠야 한다
DONE_TICKS = 16        # 완료 링·플래시가 이어지는 틱
GLOW_LEVELS = (0.3, 0.55, 0.75, 0.95)

HEADING_DEG = [-90.0, 0.0, 90.0, 180.0]  # N, E, S, W (격자 좌표, y 아래 증가)
EQUIP_SEARCH = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]


def mix(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def shade(c, f):
    return tuple(max(0, min(255, int(round(v * f)))) for v in c)


def rcolor(i):
    return ROBOT_COLORS[i % len(ROBOT_COLORS)]


def trail_color(robot, level):
    return mix(AISLE, mix(AISLE, rcolor(robot), TRAIL_MIX), level / TRAIL_LEVELS)


def snap_glow(v):
    return min(GLOW_LEVELS, key=lambda g: abs(g - v))


def ease(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)


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
    """트레이스와 프레임마다의 파생 상태(궤적, 설비 강조, 화물, 완료 링)."""

    def __init__(self, path):
        t = json.loads(Path(path).read_text(encoding="utf-8"))
        self.map_id = t["mapId"]
        self.w, self.h = t["width"], t["height"]
        self.grid = [[int(c) for c in row] for row in t["grid"]]
        self.places = {p["id"]: p for p in t["places"]}
        self.place_at = {(p["x"], p["y"]): p["kind"] for p in t["places"]}
        self.tick_ms = t["tickMs"]
        self.robots = t["robots"]
        self.ticks = t["ticks"]
        self.loop_start = t["loopStart"]
        self.loop_ticks = t["loopTicks"]
        if FADE_TICKS >= self.loop_ticks or self.loop_start < FADE_TICKS + DONE_TICKS:
            raise SystemExit("fade window must be shorter than the loop and covered by history")
        if self.loop_ticks % PULSE_TICKS:
            raise SystemExit("pulse period must divide the loop")
        self.comp = self._components()
        self.equip_cell = {pid: self._equipment_cell(p) for pid, p in self.places.items()
                           if p["kind"] in ("pickup", "dropoff")}
        self.visit = {}       # 칸 → (틱 인덱스, 로봇)
        self.work_end = {}    # 로봇 → (틱 인덱스, 장소)
        self._done = -1

    def blocked(self, x, y):
        return 0 <= x < self.w and 0 <= y < self.h and self.grid[y][x] == 0

    def _components(self):
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

    def _equipment_cell(self, place):
        """스테이션이 붙은 설비 블록의 칸. 인접 칸 중 처음 만나는 막힌 칸."""
        for dx, dy in EQUIP_SEARCH:
            c = (place["x"] + dx, place["y"] + dy)
            if c in self.comp:
                return c
        return None

    def equip_comp(self, pid):
        c = self.equip_cell.get(pid)
        return self.comp[c] if c else None

    def advance(self, k):
        for j in range(self._done + 1, k + 1):
            for i, r in enumerate(self.ticks[j]["robots"]):
                self.visit[(r["x"], r["y"])] = (j, i)
                if r.get("act") == "Move" and r.get("p", 0) >= 0.5:
                    self.visit[(r["tx"], r["ty"])] = (j, i)
                if j > 0:
                    prev = self.ticks[j - 1]["robots"][i]
                    if self.working(prev) and not r.get("at"):
                        self.work_end[i] = (j, prev["at"])
        self._done = max(self._done, k)

    def trail(self, k):
        """칸 → (로봇, 단계). 단계는 1..TRAIL_LEVELS, 오래될수록 작다."""
        out = {}
        for cell, (j, i) in self.visit.items():
            age = k - j
            if age < FADE_TICKS:
                out[cell] = (i, math.ceil((1 - age / FADE_TICKS) * TRAIL_LEVELS))
        return out

    def glow(self, k):
        """설비 연결 요소 → (로봇 색, 섞는 비율). 작업 중이면 펄스, 막 끝났으면 플래시 후 감쇠."""
        out = {}
        for i, (j, pid) in self.work_end.items():
            age = k - j
            if 0 <= age < DONE_TICKS:
                c = self.equip_comp(pid)
                if c is not None:
                    out[c] = (rcolor(i), snap_glow(0.95 * (1 - age / DONE_TICKS)))
        pulse = 0.5 + 0.5 * math.cos(2 * math.pi * (k % PULSE_TICKS) / PULSE_TICKS)
        for i, r in enumerate(self.ticks[k]["robots"]):
            if self.working(r):
                c = self.equip_comp(r["at"])
                if c is not None:
                    out[c] = (rcolor(i), snap_glow(0.6 + 0.3 * pulse))
        return out

    def ripples(self, k):
        """(장소, 로봇, 진행 0~1). 작업이 끝난 직후 스테이션에서 퍼지는 링."""
        out = []
        for i, (j, pid) in self.work_end.items():
            age = k - j
            if 0 <= age < DONE_TICKS:
                out.append((self.places[pid], i, age / DONE_TICKS))
        return out

    def kind(self, pid):
        return self.places[pid]["kind"] if pid else None

    def working(self, r):
        """스테이션에서 적재·하역 중이면 그 장소 id."""
        return r["at"] if self.kind(r.get("at")) in ("pickup", "dropoff") else None

    def cargo(self, r):
        """(크기 0~1, 이동 0~1). 이동 0 = 로봇 위, 1 = 설비 위.
        적재: 설비 위에 나타나 로봇으로 날아온다. 하역: 로봇에서 설비로 날아가 사라진다."""
        wp = r.get("wp", 0.0)
        if r["status"] == "Loading":
            if wp < 0.15:
                return ease(wp / 0.15), 1.0
            return 1.0, 1 - ease((wp - 0.15) / 0.5)
        if r["status"] == "Unloading":
            if wp < 0.85:
                return 1.0, ease((wp - 0.3) / 0.5)
            return 1 - ease((wp - 0.85) / 0.15), 1.0
        return (1.0 if self.kind(r.get("goal")) == "dropoff" else 0.0), 0.0

    @staticmethod
    def pose(r):
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


def footprint(cx, cy, ang, size):
    f = (math.cos(ang), math.sin(ang))
    s = (-f[1], f[0])
    hs = size / 2
    return [(cx + f[0] * a * hs + s[0] * b * hs, cy + f[1] * a * hs + s[1] * b * hs)
            for a, b in ((1, -1), (1, 1), (-1, 1), (-1, -1))], f, s


def nose_points(cx, cy, f, s):
    return [(cx + f[0] * 0.3, cy + f[1] * 0.3),
            (cx - f[0] * 0.13 + s[0] * 0.21, cy - f[1] * 0.13 + s[1] * 0.21),
            (cx - f[0] * 0.05, cy - f[1] * 0.05),
            (cx - f[0] * 0.13 - s[0] * 0.21, cy - f[1] * 0.13 - s[1] * 0.21)]


def draw_work_badge(d, x, y, progress, kind, color, font):
    """(x, y) = 배지 아래 중앙(슈퍼샘플 좌표). LOAD ▲ / UNLOAD ▼ 라벨과 진행 바."""
    label = "LOAD" if kind == "pickup" else "UNLOAD"
    fill = PLACE_COLORS[kind]
    tw = d.textlength(label, font=font)
    tri = 7 * SS
    pad = 4 * SS
    w = max(tw + tri + pad * 3, 40 * SS)
    h = 14 * SS
    bar_h = 5 * SS
    left, top = x - w / 2, y - h - bar_h - 2 * SS
    d.rounded_rectangle([left, top, left + w, top + h], radius=4 * SS, fill=fill, outline=DARK, width=SS)
    tx = left + pad
    cy = top + h / 2
    a = tri / 2
    pts = ([(tx + a, cy - a), (tx + tri, cy + a * 0.8), (tx, cy + a * 0.8)] if kind == "pickup"
           else [(tx + a, cy + a), (tx + tri, cy - a * 0.8), (tx, cy - a * 0.8)])
    d.polygon(pts, fill=DARK)
    d.text((tx + tri + pad, cy), label, font=font, fill=DARK, anchor="lm")
    by = top + h + 2 * SS
    d.rectangle([left, by, left + w, by + bar_h], fill=BAR_BG, outline=DARK, width=SS)
    inner = (w - 2 * SS) * max(0.0, min(1.0, progress))
    if inner >= 1:
        d.rectangle([left + SS, by + SS, left + SS + inner, by + bar_h - SS], fill=color)


def draw_wait_badge(d, x, y, font):
    """대기 중 표시. 빨간 바탕에 일시정지 기호와 WAIT."""
    tw = d.textlength("WAIT", font=font)
    pad = 4 * SS
    w, h = tw + 12 * SS + pad, 14 * SS
    left, top = x - w / 2, y - h
    d.rounded_rectangle([left, top, left + w, top + h], radius=4 * SS, fill=WAIT_RED, outline=DARK, width=SS)
    bx = left + pad
    d.rectangle([bx, top + 3 * SS, bx + 2 * SS, top + h - 3 * SS], fill=WHITE)
    d.rectangle([bx + 4 * SS, top + 3 * SS, bx + 6 * SS, top + h - 3 * SS], fill=WHITE)
    d.text((bx + 9 * SS, top + h / 2), "WAIT", font=font, fill=WHITE, anchor="lm")


class IsoView:
    TW, TH, ZU = 22, 15, 7  # 타일 폭, 타일 높이, 높이 1단위의 픽셀 (1배 기준)
    ROBOT_SIZE, ROBOT_H = 0.84, 0.9
    CARGO_SIZE, CARGO_H = 0.56, 0.75
    MARGIN = 18

    def __init__(self, sim, badge_font):
        self.sim = sim
        self.badge_font = badge_font
        w, h = sim.w, sim.h
        self.ox = self.MARGIN + h * self.TW / 2
        self.oy = self.MARGIN + 30
        self.width = int(self.MARGIN * 2 + (w + h) * self.TW / 2)
        self.height = int(self.oy + (w + h) * self.TH / 2 + 12 + self.MARGIN)
        self.block_h = {c: 0.6 + 0.35 * ((i * 7) % 5) / 4 for c, i in sim.comp.items()}
        self.items = sorted((x + y + 1.0, x, y) for (x, y) in sim.comp)

    def p(self, gx, gy, z=0.0):
        return ((self.ox + (gx - gy) * self.TW / 2) * SS,
                (self.oy + (gx + gy) * self.TH / 2 - z * self.ZU) * SS)

    def poly(self, d, pts, fill, outline=None, z=0.0):
        d.polygon([self.p(x, y, z) for x, y in pts], fill=fill, outline=outline)

    def cargo_world(self, r, cx, cy):
        """화물 중심(격자 x, y, 바닥 z)과 크기. 없으면 None."""
        scale, travel = self.sim.cargo(r)
        if scale < 0.05:
            return None
        if travel <= 0 or not self.sim.working(r):
            return cx, cy, self.ROBOT_H, scale, False
        ex, ey = self.sim.equip_cell[r["at"]]
        ez = self.block_h[(ex, ey)]
        x = cx + (ex + 0.5 - cx) * travel
        y = cy + (ey + 0.5 - cy) * travel
        z = self.ROBOT_H + (ez - self.ROBOT_H) * travel + math.sin(math.pi * travel) * 1.6
        return x, y, z, scale, True

    def draw(self, k):
        sim = self.sim
        img = Image.new("RGB", (self.width * SS, self.height * SS), BG)
        d = ImageDraw.Draw(img)
        w, h = sim.w, sim.h
        trail = sim.trail(k)
        glow = sim.glow(k)

        slab = 0.9
        d.polygon([self.p(0, h), self.p(w, h), self.p(w, h, -slab), self.p(0, h, -slab)], fill=SLAB_SOUTH)
        d.polygon([self.p(w, 0), self.p(w, h), self.p(w, h, -slab), self.p(w, 0, -slab)], fill=SLAB_EAST)

        for y in range(h):
            for x in range(w):
                if sim.grid[y][x] == 0:
                    continue
                base = trail_color(*trail[(x, y)]) if (x, y) in trail else AISLE
                self.poly(d, [(x, y), (x + 1, y), (x + 1, y + 1), (x, y + 1)], base, GRID)
                kind = sim.place_at.get((x, y))
                if kind:
                    m = 0.22
                    self.poly(d, [(x + m, y + m), (x + 1 - m, y + m), (x + 1 - m, y + 1 - m), (x + m, y + 1 - m)],
                              PLACE_COLORS[kind], shade(PLACE_COLORS[kind], 0.8))

        robots = []
        flying = []
        for i, r in enumerate(sim.ticks[k]["robots"]):
            cx, cy, ang = sim.pose(r)
            c = self.cargo_world(r, cx, cy)
            if c and c[4]:
                flying.append(c)
                c = None
            robots.append((cx + cy, i, r, cx, cy, ang, c))
        robots.sort(key=lambda t: (t[0], t[1]))

        ri = 0
        for depth, x, y in self.items:
            while ri < len(robots) and robots[ri][0] < depth:
                self.draw_robot(d, *robots[ri][1:])
                ri += 1
            self.draw_block(d, x, y, glow.get(sim.comp[(x, y)]))
        while ri < len(robots):
            self.draw_robot(d, *robots[ri][1:])
            ri += 1

        for x, y, z, scale, _ in flying:
            self.draw_cargo(d, x, y, z, 0.0, scale)
        for place, i, t in sim.ripples(k):
            self.ring(d, place["x"] + 0.5, place["y"] + 0.5, 0.4 + 2.2 * ease(t), rcolor(i), max(1, round(4 * (1 - t))))
        for _, i, r, cx, cy, _, _ in robots:
            if r.get("wx") is not None:
                wx, wy = r["wx"], r["wy"]
                self.poly(d, [(wx + 0.1, wy + 0.1), (wx + 0.9, wy + 0.1), (wx + 0.9, wy + 0.9), (wx + 0.1, wy + 0.9)],
                          None, WAIT_RED)
                d.line([self.p(wx + 0.25, wy + 0.25), self.p(wx + 0.75, wy + 0.75)], fill=WAIT_RED, width=2 * SS)
                d.line([self.p(wx + 0.75, wy + 0.25), self.p(wx + 0.25, wy + 0.75)], fill=WAIT_RED, width=2 * SS)
            x, y = self.p(cx, cy, self.ROBOT_H)
            if self.sim.working(r):
                draw_work_badge(d, x, y - 26 * SS, r.get("wp", 0.0), sim.kind(r["at"]), rcolor(i), self.badge_font)
            elif r["status"] == "Waiting":
                draw_wait_badge(d, x, y - 14 * SS, self.badge_font)

        return img.resize((self.width, self.height), Image.LANCZOS)

    def ring(self, d, cx, cy, radius, color, width):
        pts = [self.p(cx + radius * math.cos(a), cy + radius * math.sin(a))
               for a in (2 * math.pi * n / 40 for n in range(41))]
        d.line(pts, fill=color, width=width * SS, joint="curve")

    def draw_block(self, d, x, y, glow):
        sim = self.sim
        z = self.block_h[(x, y)]
        top, south, east, edge = BLOCK_TOP, BLOCK_SOUTH, BLOCK_EAST, BLOCK_EDGE
        if glow:
            color, t = glow
            top, south, east = mix(BLOCK_TOP, color, t), mix(BLOCK_SOUTH, color, t * 0.8), mix(BLOCK_EAST, color, t * 0.8)
            edge = shade(color, 0.55)
        if not sim.blocked(x, y + 1):
            d.polygon([self.p(x, y + 1), self.p(x + 1, y + 1), self.p(x + 1, y + 1, z), self.p(x, y + 1, z)], fill=south)
        if not sim.blocked(x + 1, y):
            d.polygon([self.p(x + 1, y), self.p(x + 1, y + 1), self.p(x + 1, y + 1, z), self.p(x + 1, y, z)], fill=east)
        self.poly(d, [(x, y), (x + 1, y), (x + 1, y + 1), (x, y + 1)], top, z=z)
        lw = SS * (2 if glow else 1)
        edges = [((x, y), (x + 1, y), sim.blocked(x, y - 1)),
                 ((x + 1, y), (x + 1, y + 1), sim.blocked(x + 1, y)),
                 ((x, y + 1), (x + 1, y + 1), sim.blocked(x, y + 1)),
                 ((x, y), (x, y + 1), sim.blocked(x - 1, y))]
        for a, b, inner in edges:
            if not inner:
                d.line([self.p(*a, z), self.p(*b, z)], fill=edge, width=lw)
        if not sim.blocked(x, y + 1) and not sim.blocked(x + 1, y):
            d.line([self.p(x + 1, y + 1), self.p(x + 1, y + 1, z)], fill=edge, width=SS)

    def box(self, d, pts, cx, cy, z0, z1, color, top_color=None):
        faces = []
        for k in range(4):
            a, b = pts[k], pts[(k + 1) % 4]
            nx, ny = (b[1] - a[1]), -(b[0] - a[0])
            ln = math.hypot(nx, ny) or 1
            nx, ny = nx / ln, ny / ln
            mx, my = (a[0] + b[0]) / 2 - cx, (a[1] + b[1]) / 2 - cy
            if nx * mx + ny * my < 0:
                nx, ny = -nx, -ny
            if nx + ny <= 0:
                continue
            light = 0.62 + 0.25 * max(0.0, nx * 0.3 + ny * 0.95)
            faces.append(((a[0] + b[0]) + (a[1] + b[1]), a, b, shade(color, light)))
        faces.sort(key=lambda f: f[0])
        for _, a, b, col in faces:
            d.polygon([self.p(*a, z0), self.p(*b, z0), self.p(*b, z1), self.p(*a, z1)], fill=col)
        self.poly(d, pts, top_color or mix(color, WHITE, 0.12), shade(color, 0.55), z=z1)

    def draw_cargo(self, d, x, y, z, ang, scale):
        size = self.CARGO_SIZE * scale
        pts, f, s = footprint(x, y, ang, size)
        self.box(d, pts, x, y, z, z + self.CARGO_H * scale, CARGO)
        zt = z + self.CARGO_H * scale
        tape = 0.09 * size
        self.poly(d, [(x - s[0] * size / 2 + f[0] * tape, y - s[1] * size / 2 + f[1] * tape),
                      (x + s[0] * size / 2 + f[0] * tape, y + s[1] * size / 2 + f[1] * tape),
                      (x + s[0] * size / 2 - f[0] * tape, y + s[1] * size / 2 - f[1] * tape),
                      (x - s[0] * size / 2 - f[0] * tape, y - s[1] * size / 2 - f[1] * tape)], CARGO_TAPE, z=zt)

    def draw_robot(self, d, i, r, cx, cy, ang, cargo):
        color = rcolor(i)
        hgt = self.ROBOT_H
        pts, f, s = footprint(cx, cy, ang, self.ROBOT_SIZE)
        sh, _, _ = footprint(cx + 0.06, cy + 0.06, ang, self.ROBOT_SIZE * 1.08)
        self.poly(d, sh, shade(BG, 0.78))
        self.box(d, pts, cx, cy, 0, hgt, color)
        self.poly(d, nose_points(cx, cy, f, s), WHITE, z=hgt)
        if cargo:
            x, y, z, scale, _ = cargo
            self.draw_cargo(d, x - f[0] * 0.1, y - f[1] * 0.1, z, ang, scale)


class TopView:
    CS = 14
    MARGIN = 14
    TOP = 34
    BOTTOM = 140
    ROBOT_SIZE = 0.8

    def __init__(self, sim, badge_font):
        self.sim = sim
        self.badge_font = badge_font
        self.width = self.MARGIN * 2 + sim.w * self.CS
        self.height = self.TOP + sim.h * self.CS + self.BOTTOM

    def p(self, gx, gy):
        return ((self.MARGIN + gx * self.CS) * SS, (self.TOP + gy * self.CS) * SS)

    def rect(self, d, x, y, m, fill, outline=None, width=1):
        a = self.p(x + m, y + m)
        b = self.p(x + 1 - m, y + 1 - m)
        d.rectangle([a[0], a[1], b[0] - 1, b[1] - 1], fill=fill, outline=outline, width=width)

    def cargo_icon(self, d, x, y, ang, size):
        pts, f, s = footprint(x, y, ang, size)
        d.polygon([self.p(*q) for q in pts], fill=CARGO, outline=shade(CARGO, 0.5))
        t = 0.1 * size
        d.polygon([self.p(x - s[0] * size / 2 + f[0] * t, y - s[1] * size / 2 + f[1] * t),
                   self.p(x + s[0] * size / 2 + f[0] * t, y + s[1] * size / 2 + f[1] * t),
                   self.p(x + s[0] * size / 2 - f[0] * t, y + s[1] * size / 2 - f[1] * t),
                   self.p(x - s[0] * size / 2 - f[0] * t, y - s[1] * size / 2 - f[1] * t)], fill=CARGO_TAPE)

    def draw(self, k):
        sim = self.sim
        img = Image.new("RGB", (self.width * SS, self.height * SS), BG)
        d = ImageDraw.Draw(img)
        trail = sim.trail(k)
        glow = sim.glow(k)
        for y in range(sim.h):
            for x in range(sim.w):
                if sim.grid[y][x] == 0:
                    continue
                base = trail_color(*trail[(x, y)]) if (x, y) in trail else AISLE
                self.rect(d, x, y, 0, base, GRID)
                kind = sim.place_at.get((x, y))
                if kind:
                    self.rect(d, x, y, 0.24, PLACE_COLORS[kind], shade(PLACE_COLORS[kind], 0.8))
        for (x, y) in sim.comp:
            a = self.p(x + 0.12, y + 0.12)
            b = self.p(x + 1.12, y + 1.12)
            d.rectangle([a[0], a[1], b[0] - 1, b[1] - 1], fill=shade(BG, 0.86))
        for (x, y), c in sim.comp.items():
            g = glow.get(c)
            self.rect(d, x, y, 0, mix(BLOCK_TOP, g[0], g[1]) if g else BLOCK_TOP)
        for (x, y), c in sim.comp.items():
            g = glow.get(c)
            edge, lw = (shade(g[0], 0.55), 2 * SS) if g else (BLOCK_EDGE, SS)
            if not sim.blocked(x, y - 1):
                d.line([self.p(x, y), self.p(x + 1, y)], fill=edge, width=lw)
            if not sim.blocked(x, y + 1):
                d.line([self.p(x, y + 1), self.p(x + 1, y + 1)], fill=edge, width=lw)
            if not sim.blocked(x - 1, y):
                d.line([self.p(x, y), self.p(x, y + 1)], fill=edge, width=lw)
            if not sim.blocked(x + 1, y):
                d.line([self.p(x + 1, y), self.p(x + 1, y + 1)], fill=edge, width=lw)

        for place, i, t in sim.ripples(k):
            cx, cy = self.p(place["x"] + 0.5, place["y"] + 0.5)
            rad = (0.4 + 2.0 * ease(t)) * self.CS * SS
            d.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], outline=rcolor(i), width=max(1, round(4 * (1 - t))) * SS)

        poses = []
        for i, r in enumerate(sim.ticks[k]["robots"]):
            cx, cy, ang = sim.pose(r)
            poses.append((i, r, cx, cy))
            color = rcolor(i)
            pts, f, s = footprint(cx, cy, ang, self.ROBOT_SIZE)
            d.polygon([self.p(x, y) for x, y in pts], fill=color, outline=shade(color, 0.55))
            d.polygon([self.p(x, y) for x, y in nose_points(cx, cy, f, s)], fill=WHITE)
            scale, travel = sim.cargo(r)
            if scale >= 0.05:
                if travel > 0 and sim.working(r):
                    ex, ey = sim.equip_cell[r["at"]]
                    x = cx + (ex + 0.5 - cx) * travel
                    y = cy + (ey + 0.5 - cy) * travel
                    self.cargo_icon(d, x, y, 0.0, 0.62 * scale * (1 + 0.35 * math.sin(math.pi * travel)))
                else:
                    self.cargo_icon(d, cx - f[0] * 0.1, cy - f[1] * 0.1, ang, 0.5 * scale)
        for i, r, cx, cy in poses:
            if r.get("wx") is not None:
                a, b = self.p(r["wx"] + 0.1, r["wy"] + 0.1), self.p(r["wx"] + 0.9, r["wy"] + 0.9)
                d.rectangle([a[0], a[1], b[0], b[1]], outline=WAIT_RED, width=2 * SS)
                d.line([a, b], fill=WAIT_RED, width=2 * SS)
                d.line([(a[0], b[1]), (b[0], a[1])], fill=WAIT_RED, width=2 * SS)
            x, y = self.p(cx, cy - 0.55)
            if sim.working(r):
                draw_work_badge(d, x, y, r.get("wp", 0.0), sim.kind(r["at"]), rcolor(i), self.badge_font)
            elif r["status"] == "Waiting":
                draw_wait_badge(d, x, y, self.badge_font)
        return img.resize((self.width, self.height), Image.LANCZOS)


def robot_line(sim, i, r):
    rid = sim.robots[i]["id"]
    if r["status"] == "Waiting":
        goal = f"-> {r['goal']}  " if r.get("goal") else ""
        cell = f"blocked at ({r['wx']},{r['wy']})" if r.get("wx") is not None else "blocked"
        return f"{rid}  {'Waiting':<10}{goal}{cell}"
    if r.get("at") and not sim.working(r):
        return f"{rid}  {r['status']:<10}@ {r['at']} (home)"
    if r.get("at"):
        place = sim.places[r["at"]]
        return f"{rid}  {r['status']:<10}@ {place['id']} ({place['equipmentId']})  {int(r.get('wp', 0) * 100):3d}%"
    if r.get("goal"):
        place = sim.places[r["goal"]]
        eq = f" ({place['equipmentId']})" if place.get("equipmentId") else ""
        load = "  [cargo]" if sim.kind(r["goal"]) == "dropoff" else ""
        return f"{rid}  {r['status']:<10}-> {place['id']}{eq}{load}"
    return f"{rid}  {r['status']}"


def draw_swatch(d, x, y, size, color):
    d.rectangle([x, y, x + size - 1, y + size - 1], fill=color, outline=shade(color, 0.6))


def clock(sim, k):
    t = (k - sim.loop_start) % sim.loop_ticks * sim.tick_ms / 1000
    return f"t = {t:5.1f}s / {sim.loop_ticks * sim.tick_ms / 1000:.0f}s loop"


def legend_items():
    return [(PLACE_COLORS["pickup"], "Pickup (LOAD)"), (PLACE_COLORS["dropoff"], "Dropoff (UNLOAD)"),
            (PLACE_COLORS["charger"], "Charger"), (PLACE_COLORS["parking"], "Parking"), (BLOCK_TOP, "Equipment"),
            (CARGO, "Cargo")]


def hud_iso(img, view, sim, k, fonts):
    d = ImageDraw.Draw(img)
    big, small = fonts
    m = view.MARGIN
    d.text((m, m - 4), f"{sim.map_id}  ·  {len(sim.robots)} robots", font=big, fill=TEXT)
    d.text((m, m + 18), clock(sim, k), font=small, fill=TEXT_DIM)

    x0 = int(view.width - m - 370)
    y = m - 2
    for i, r in enumerate(sim.ticks[k]["robots"]):
        draw_swatch(d, x0, y + 3, 11, rcolor(i))
        d.text((x0 + 18, y), robot_line(sim, i, r), font=small, fill=TEXT)
        y += 17

    items = legend_items()
    ly = view.height - m - 16 * len(items)
    for color, label in items:
        draw_swatch(d, m, ly + 3, 10, color)
        d.text((m + 16, ly), label, font=small, fill=TEXT_DIM)
        ly += 16


def hud_2d(img, view, sim, k, fonts):
    d = ImageDraw.Draw(img)
    big, small = fonts
    m = view.MARGIN
    d.text((m, 8), f"{sim.map_id}  ·  {len(sim.robots)} robots", font=big, fill=TEXT)
    right = clock(sim, k)
    d.text((view.width - m - d.textlength(right, font=small), 11), right, font=small, fill=TEXT_DIM)

    top = view.TOP + sim.h * view.CS + 8
    col_w = (view.width - 2 * m) // 2
    for i, r in enumerate(sim.ticks[k]["robots"]):
        x = m + (i % 2) * col_w
        y = top + (i // 2) * 17
        draw_swatch(d, x, y + 3, 11, rcolor(i))
        d.text((x + 18, y), robot_line(sim, i, r), font=small, fill=TEXT)

    x = m
    y = view.height - 22
    for color, label in legend_items():
        draw_swatch(d, x, y + 3, 10, color)
        d.text((x + 15, y), label, font=small, fill=TEXT_DIM)
        x += 15 + int(d.textlength(label, font=small)) + 16


def key_colors():
    """팔레트에 반드시 넣을 색. 면적이 작은 로봇 색이 양자화에서 뭉개지지 않게 한다."""
    colors = [BG, AISLE, GRID, BLOCK_TOP, BLOCK_SOUTH, BLOCK_EAST, BLOCK_EDGE, SLAB_SOUTH, SLAB_EAST, TEXT, TEXT_DIM,
              WHITE, DARK, shade(BG, 0.78), shade(BG, 0.86), CARGO_TAPE]
    colors += [CARGO, mix(CARGO, WHITE, 0.12), shade(CARGO, 0.55)] + [shade(CARGO, f / 100) for f in range(62, 90, 8)]
    for c in PLACE_COLORS.values():
        colors += [c, shade(c, 0.8)]
    colors += [WAIT_RED]
    for i, c in enumerate(ROBOT_COLORS):
        colors += [c, mix(c, WHITE, 0.12), shade(c, 0.55), shade(c, 0.7), shade(c, 0.85)]
        colors += [trail_color(i, lv) for lv in range(1, TRAIL_LEVELS + 1)]
        colors += [mix(BLOCK_TOP, c, g) for g in GLOW_LEVELS[-2:]]
    return list(dict.fromkeys(colors))


def save_gif(frames, out, frame_ms):
    # 모든 프레임에 같은 팔레트를 쓰고, 이전 프레임과 같은 픽셀은 투명(255)으로 비워 용량을 줄인다
    keys = key_colors()
    if len(keys) > 225:
        raise SystemExit(f"too many key colors: {len(keys)}")
    picks = [frames[j] for j in range(0, len(frames), max(1, len(frames) // 8))]
    sample = Image.new("RGB", (frames[0].width, frames[0].height * len(picks)))
    for n, f in enumerate(picks):
        sample.paste(f, (0, frames[0].height * n))
    rest_n = 255 - len(keys)
    rest = sample.quantize(colors=rest_n, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    flat = [v for c in keys for v in c] + rest.getpalette()[: rest_n * 3]
    flat += [0] * (255 * 3 - len(flat))
    pal_img = Image.new("P", (1, 1))
    pal_img.putpalette(flat + [255, 0, 255])
    palette = flat + [255, 0, 255]

    indexed = [np.asarray(f.quantize(palette=pal_img, dither=Image.Dither.NONE)) for f in frames]
    out_frames = []
    for k, cur in enumerate(indexed):
        arr = cur.copy()
        if k > 0:
            arr[cur == indexed[k - 1]] = 255
        im = Image.fromarray(arr, mode="P")
        im.putpalette(palette)
        out_frames.append(im)

    out_frames[0].save(out, save_all=True, append_images=out_frames[1:], duration=frame_ms, loop=0,
                       transparency=255, disposal=1, optimize=False)


def frame_ticks(sim, step, slow):
    """녹화할 틱 목록. 대기 장면 앞뒤는 slow 간격으로 촘촘히 뽑아 느리게 보여준다."""
    waiting = {k for k in range(sim.loop_start, sim.loop_start + sim.loop_ticks)
               if any(r["status"] == "Waiting" for r in sim.ticks[k]["robots"])}
    out = []
    k = sim.loop_start
    while k < sim.loop_start + sim.loop_ticks:
        out.append(k)
        near = any(j in waiting for j in range(k - 3, k + 8))
        k += slow if near else step
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("trace")
    ap.add_argument("out")
    ap.add_argument("--view", choices=["iso", "2d"], default="iso")
    ap.add_argument("--ticks-per-frame", type=int, default=4)
    ap.add_argument("--slow-ticks-per-frame", type=int, default=1, help="누군가 대기 중일 때의 틱 간격(느린 재생)")
    ap.add_argument("--frame-ms", type=int, default=60)
    ap.add_argument("--frames-dir", help="확인용 PNG를 저장할 디렉터리")
    ap.add_argument("--save-frames", default="0", help="--frames-dir에 저장할 프레임 번호(쉼표 구분)")
    args = ap.parse_args()

    sim = Sim(args.trace)
    badge_font = load_font(11 * SS, bold=True)
    view = IsoView(sim, badge_font) if args.view == "iso" else TopView(sim, badge_font)
    hud = hud_iso if args.view == "iso" else hud_2d
    fonts = (load_font(17, bold=True), load_font(13))

    ticks = frame_ticks(sim, args.ticks_per_frame, args.slow_ticks_per_frame)
    frames = []
    for k in ticks + [sim.loop_start + sim.loop_ticks]:   # 마지막 한 장(= 한 주기 뒤)은 이음매 검사용
        sim.advance(k)
        img = view.draw(k)
        hud(img, view, sim, k, fonts)
        frames.append(img)

    n = len(ticks)
    a = np.asarray(frames[0], dtype=np.int16)
    b = np.asarray(frames[n], dtype=np.int16)
    diff = np.abs(a - b)
    print(f"seam check: frame 0 vs one loop later: max diff {diff.max()}, "
          f"differing pixels {(diff.max(axis=2) > 0).sum()}")
    frames = frames[:n]
    waits = [f for f in range(n) if any(r["status"] == "Waiting" for r in sim.ticks[ticks[f]]["robots"])
             and (f == 0 or not any(r["status"] == "Waiting" for r in sim.ticks[ticks[f - 1]]["robots"]))]
    print(f"frames where a wait starts: {waits}")

    if args.frames_dir:
        d = Path(args.frames_dir)
        d.mkdir(parents=True, exist_ok=True)
        for j in (int(v) % n for v in args.save_frames.split(",")):
            frames[j].save(d / f"{args.view}-{j:04d}.png")

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    save_gif(frames, out, args.frame_ms)
    print(f"{out}: {len(frames)} frames, {len(frames) * args.frame_ms / 1000:.1f}s loop, "
          f"{out.stat().st_size / 1e6:.2f} MB")


if __name__ == "__main__":
    main()
