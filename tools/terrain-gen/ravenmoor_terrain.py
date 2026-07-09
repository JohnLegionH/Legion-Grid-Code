#!/usr/bin/env python3
# ravenmoor_terrain.py — procedural terrain generator for the Legion grid.
#
# Emits an OpenSim RAW32 (.r32) heightfield plus a 16-bit grayscale PNG preview
# and a matched RegionSettings fragment. Standalone tool (not server code);
# run it, then `terrain load` the .r32 on a region of matching dimensions.
#
# Pipeline:  fBm/ridged Perlin base  ->  macro shaping (valley/coast)
#            ->  vectorized virtual-pipes hydraulic erosion  ->  thermal erosion
#            ->  percentile-anchored remap to metres  ->  RAW32 / PNG / settings.
#
# Deps: numpy (required), scipy (erosion advection + smoothing), Pillow (PNG).
# Determinism: same --seed + params  =>  byte-identical .r32 (see README).
#
# See README.md for the console load sequence and preset descriptions.

import argparse
import struct
import sys
import time

import numpy as np

try:
    from scipy.ndimage import gaussian_filter
    _HAVE_SCIPY = True
except Exception:                                   # pragma: no cover
    _HAVE_SCIPY = False

try:
    from PIL import Image
    _HAVE_PIL = True
except Exception:                                   # pragma: no cover
    _HAVE_PIL = False

# OpenSim RegionSize granularity: the RAW32 loader infers a square dimension
# trimmed to a multiple of this (Constants.RegionSize = 256).
REGION_GRAIN = 256

# Stock OpenSim default terrain texture UUIDs (dirt / grass / mountain / rock).
# Sensible placeholders so a loaded region renders; swap for gothic textures later.
DEFAULT_TEXTURES = [
    "b8d3965a-ad78-bf43-699b-bff8eca6c975",   # low   (Texture1)
    "abb783e6-3e93-26c0-248a-247666855da3",   # low-mid(Texture2)
    "179cdabd-398a-9b6b-1391-4dc333ba321f",   # hi-mid(Texture3)
    "beb169c7-11ea-fff2-efe5-0f24dc881df2",   # high  (Texture4)
]
ZERO_UUID = "00000000-0000-0000-0000-000000000000"

# Elm test canvas (the primary target).
ELM_WATER = 20.0
ELM_MIN = -100.0
ELM_MAX = 100.0


# --------------------------------------------------------------------------- #
# Noise
# --------------------------------------------------------------------------- #
def _fade(t):
    # Perlin quintic smootherstep.
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def _perlin(res_y, res_x, cells_y, cells_x, rng):
    """Vectorized classic Perlin gradient noise in ~[-1,1] on a res_y x res_x grid,
    tiling `cells_y` x `cells_x` lattice cells. Gradients seeded from `rng`."""
    # Random unit gradients at (cells+1) lattice corners.
    ang = rng.uniform(0.0, 2.0 * np.pi, (cells_y + 1, cells_x + 1)).astype(np.float64)
    gy = np.sin(ang)
    gx = np.cos(ang)

    ys = np.linspace(0.0, cells_y, res_y, endpoint=False)
    xs = np.linspace(0.0, cells_x, res_x, endpoint=False)
    gridy, gridx = np.meshgrid(ys, xs, indexing="ij")

    y0 = np.floor(gridy).astype(np.int32)
    x0 = np.floor(gridx).astype(np.int32)
    y1 = y0 + 1
    x1 = x0 + 1
    ty = gridy - y0
    tx = gridx - x0

    def dot(iy, ix, dy, dx):
        return gx[iy, ix] * dx + gy[iy, ix] * dy

    n00 = dot(y0, x0, ty, tx)
    n10 = dot(y0, x1, ty, tx - 1.0)
    n01 = dot(y1, x0, ty - 1.0, tx)
    n11 = dot(y1, x1, ty - 1.0, tx - 1.0)

    u = _fade(tx)
    v = _fade(ty)
    nx0 = n00 + u * (n10 - n00)
    nx1 = n01 + u * (n11 - n01)
    return (nx0 + v * (nx1 - nx0)).astype(np.float32)


def fbm(size, base_cells, octaves, gain, lacunarity, rng, ridged=False):
    """Fractal Brownian motion (or ridged multifractal) normalized to [0,1]."""
    field = np.zeros((size, size), dtype=np.float32)
    amp = 1.0
    cells = float(base_cells)
    total = 0.0
    for _ in range(octaves):
        c = max(1, int(round(cells)))
        n = _perlin(size, size, c, c, rng)
        if ridged:
            n = 1.0 - np.abs(n)
            n = n * n
        field += amp * n
        total += amp
        amp *= gain
        cells *= lacunarity
    field /= total
    if not ridged:
        field = (field + 1.0) * 0.5     # perlin fBm is ~[-1,1] -> [0,1]
    # robust normalize
    lo, hi = np.percentile(field, 0.1), np.percentile(field, 99.9)
    field = np.clip((field - lo) / max(hi - lo, 1e-6), 0.0, 1.0)
    return field.astype(np.float32)


# --------------------------------------------------------------------------- #
# Macro shaping (spatially-coherent large-scale form)
# --------------------------------------------------------------------------- #
def valley_centreline(size, rng, meander):
    """S->N meandering channel centreline (x in [0,1] per row). One rng draw so the
    trough and the carved river share the same path."""
    ys = np.linspace(0.0, 1.0, size, dtype=np.float32)
    phase = rng.uniform(0.0, 2.0 * np.pi)
    centre = 0.5 + meander * (0.5 * np.sin(2.0 * np.pi * ys + phase)
                              + 0.25 * np.sin(4.0 * np.pi * ys + 2.0 * phase))
    # keep the channel on-map end to end so the river spans the full region
    return np.clip(centre, 0.08, 0.92).astype(np.float32)


def macro_valley(centre, size, depth):
    """Broad gaussian trough about the centreline; subtractive amount in [0,depth]."""
    xs = np.linspace(0.0, 1.0, size, dtype=np.float32)
    dist = np.abs(xs[None, :] - centre[:, None])
    trough = np.exp(-(dist * dist) / (2.0 * (0.16 ** 2)))
    return (depth * trough).astype(np.float32)


def carve_river_meters(heights, centre, size, top, bottom, half_width, bank_h):
    """Stamp a continuous river channel into the FINAL metre-space heightfield via
    elementwise minimum, so the bed is a clean connected surface (`top`..`bottom` m,
    descending downstream) independent of terrain noise. Banks rise `bank_h` m over
    the half-width to meet the surrounding land; cells beyond the channel untouched."""
    xs = np.linspace(0.0, 1.0, size, dtype=np.float32)
    dist = np.abs(xs[None, :] - centre[:, None])
    t = np.clip(dist / (2.0 * half_width), 0.0, 1.0)          # 0 centre .. 1 bank
    bed = np.linspace(top, bottom, size, dtype=np.float32)[:, None]   # descend N
    surface = bed + (t * t) * bank_h
    surface = np.where(dist < 2.0 * half_width, surface, 1e9).astype(np.float32)
    return np.minimum(heights, surface)


def macro_coast(size, base_cells, rng, strength):
    """Large-scale low-frequency mask that pushes broad areas down (irregular sea)."""
    big = fbm(size, max(2, base_cells // 4), 3, 0.5, 2.0, rng, ridged=False)
    return (strength * (big - 0.45)).astype(np.float32)    # signed bias


# --------------------------------------------------------------------------- #
# Erosion
# --------------------------------------------------------------------------- #
def _padN(a, mode):
    return np.pad(a, 1, mode=mode)


def hydraulic_erosion(b, iters, rain, Kc, Ks, Kd, Ke,
                      flow_rate=0.5, max_cap=4.0, max_erode=0.4, min_tilt=0.02):
    """Bounded, mass-conserving virtual-pipes hydraulic erosion.

    Every term is clamped so the field cannot diverge (an earlier unbounded pipe
    model + semi-Lagrangian advection blew up to +-1000 m, producing per-cell
    spikes): water moves at most `flow_rate`*depth and never more than half the
    local head (no overshoot); capacity is capped at `max_cap`; erosion/deposition
    is clamped to +-`max_erode` per step; sediment is transported by the SAME
    clamped water-flux fractions (conservative, no wild backtracing). Works in
    float64, returns float32. `b` is a working-scale height array."""
    b = b.astype(np.float64).copy()
    d = np.zeros_like(b)          # water column
    s = np.zeros_like(b)          # suspended sediment
    eps = 1e-6

    def gather(a, which):
        Pa = np.pad(a, 1, mode="constant")
        if which == "L": return Pa[1:-1, 0:-2]           # left nbr's rightward part
        if which == "R": return Pa[1:-1, 2:]
        if which == "T": return Pa[0:-2, 1:-1]
        return Pa[2:, 1:-1]                               # "B"

    for _ in range(iters):
        d += rain
        surf = b + d
        P = _padN(surf, "edge")
        oL = np.maximum(surf - P[1:-1, 0:-2], 0.0)
        oR = np.maximum(surf - P[1:-1, 2:], 0.0)
        oT = np.maximum(surf - P[0:-2, 1:-1], 0.0)
        oB = np.maximum(surf - P[2:, 1:-1], 0.0)
        otot = oL + oR + oT + oB + eps
        movable = np.minimum(flow_rate * d, 0.5 * otot)   # bounded, no overshoot
        fL = movable * oL / otot; fR = movable * oR / otot
        fT = movable * oT / otot; fB = movable * oB / otot
        fL[:, 0] = 0; fR[:, -1] = 0; fT[0, :] = 0; fB[-1, :] = 0
        ftot = fL + fR + fT + fB + eps

        # sediment moves with the same clamped flux (conservative)
        s_out = s * np.minimum(ftot / (d + eps), 1.0)
        soL = s_out * fL / ftot; soR = s_out * fR / ftot
        soT = s_out * fT / ftot; soB = s_out * fB / ftot

        outflow = fL + fR + fT + fB
        inflow = gather(fR, "L") + gather(fL, "R") + gather(fB, "T") + gather(fT, "B")
        d = d - outflow + inflow
        s = s - s_out + (gather(soR, "L") + gather(soL, "R")
                         + gather(soB, "T") + gather(soT, "B"))

        gy, gx = np.gradient(b)
        slope = np.sqrt(gx * gx + gy * gy)
        sin_tilt = np.maximum(slope / np.sqrt(1.0 + slope * slope), min_tilt)
        C = np.minimum(Kc * sin_tilt * outflow, max_cap)  # clamped capacity
        delta = np.where(C > s, Ks * (C - s), Kd * (C - s))
        delta = np.clip(delta, -max_erode, max_erode)     # bounded per step
        b = b - delta
        s = np.maximum(s + delta, 0.0)
        d *= (1.0 - Ke)                                   # evaporation

    return (b + s).astype(np.float32)                     # settle sediment


def thermal_erosion(b, iters, talus, factor=0.5):
    """Vectorized thermal weathering: height differences above the talus threshold
    relax toward it (scree/talus slopes). Symmetric loss/gain => mass conserved."""
    b = b.astype(np.float64).copy()
    for _ in range(iters):
        P = _padN(b, "edge")
        nbrs = [P[1:-1, 0:-2], P[1:-1, 2:], P[0:-2, 1:-1], P[2:, 1:-1]]
        loss = np.zeros_like(b); gain = np.zeros_like(b)
        for n in nbrs:
            down = np.maximum(b - n, 0.0)                # we are higher -> shed
            loss += np.where(down > talus, factor * 0.25 * (down - talus), 0.0)
            up = np.maximum(n - b, 0.0)                  # nbr higher -> receive
            gain += np.where(up > talus, factor * 0.25 * (up - talus), 0.0)
        b = b + gain - loss
    return b.astype(np.float32)


def detail_preserving_smooth(h, sigma, strength):
    """Blend toward a gaussian only where the field is locally ROUGH, so single-cell
    spikes are flattened while smooth ridge crests and slopes are preserved. The
    river is stamped after this, so it is untouched."""
    if not _HAVE_SCIPY or sigma <= 0:
        return h
    sm = gaussian_filter(h, sigma)
    rough = np.abs(h - sm)
    w = np.clip(rough / (rough.mean() * strength + 1e-6), 0.0, 1.0)
    return (h * (1.0 - w) + sm * w).astype(np.float32)


def slope_degrees(h, cell=1.0):
    """Per-cell terrain slope in degrees at `cell` metres/cell."""
    gy, gx = np.gradient(h.astype(np.float64), cell)
    return np.degrees(np.arctan(np.sqrt(gx * gx + gy * gy)))


def hillshade(h, azimuth_deg=315.0, altitude_deg=45.0, cell=1.0):
    """Lambertian hillshade (uint8) from a sun at `azimuth` (default NW) — exposes
    per-cell spikiness that flat grayscale height hides."""
    gy, gx = np.gradient(h.astype(np.float64), cell)
    slope = np.arctan(np.sqrt(gx * gx + gy * gy))
    aspect = np.arctan2(-gy, gx)
    az = np.radians(360.0 - azimuth_deg + 90.0)
    alt = np.radians(altitude_deg)
    shade = (np.sin(alt) * np.cos(slope) +
             np.cos(alt) * np.sin(slope) * np.cos(az - aspect))
    return (np.clip(shade, 0.0, 1.0) * 255.0).astype(np.uint8)


# --------------------------------------------------------------------------- #
# Height mapping
# --------------------------------------------------------------------------- #
def remap_percentile(field, anchors):
    """Map the field so that its value at percentile p equals `metres` for every
    (p, metres) anchor. Piecewise-linear, monotonic. `anchors` sorted by p."""
    ps = np.array([a[0] for a in anchors], dtype=np.float64)
    ms = np.array([a[1] for a in anchors], dtype=np.float64)
    src = np.percentile(field.astype(np.float64), ps)
    # enforce strictly increasing breakpoints for np.interp
    for i in range(1, len(src)):
        if src[i] <= src[i - 1]:
            src[i] = src[i - 1] + 1e-6
    out = np.interp(field.astype(np.float64), src, ms)
    return out.astype(np.float32)


# --------------------------------------------------------------------------- #
# Presets
# --------------------------------------------------------------------------- #
# Heights sized to Elm's -100..+100 limits with the fixed 20 m waterline.
# Design note: at 1 m/cell, RIDGED noise produces near-vertical per-cell spikes,
# and high-frequency octaves (short wavelength) are the spike source. So the base
# is low-frequency fBm (few big landforms; features < ~16 m come from erosion, not
# noise); gothic drama comes from valley DEPTH and ridge HEIGHT (vertical relief),
# not per-cell steepness. Erosion params feed the bounded hydraulic_erosion().
PRESETS = {
    "ravenmoor-valley": dict(
        ridged=False, base_cells=2, octaves=4, gain=0.5, lacunarity=2.0,
        macro="valley", macro_depth=0.55, macro_meander=0.4,
        river_top=19.4, river_bottom=18.4, river_hw=0.014, river_bank=6.0,
        hydro_iters=60, rain=0.02, Kc=0.6, Ks=0.3, Kd=0.2, Ke=0.02,
        thermal_iters=45, talus=1.0,
        smooth_sigma=1.8, smooth_strength=1.0,
        work_relief=52.0,
        anchors=[(1, 22), (6, 24), (40, 28), (62, 42),
                 (85, 72), (97, 88), (99.7, 94)],
    ),
    "highlands": dict(
        ridged=False, base_cells=2, octaves=4, gain=0.5, lacunarity=2.0,
        macro="none",
        hydro_iters=55, rain=0.02, Kc=0.5, Ks=0.3, Kd=0.25, Ke=0.02,
        thermal_iters=50, talus=0.8,
        smooth_sigma=1.8, smooth_strength=1.0,
        work_relief=45.0,
        anchors=[(0.5, 16.5), (5, 22), (30, 33), (60, 48), (90, 63), (99, 70)],
    ),
    "islefjord": dict(
        ridged=False, base_cells=2, octaves=4, gain=0.5, lacunarity=2.0,
        macro="coast", macro_strength=1.0,
        hydro_iters=55, rain=0.02, Kc=0.55, Ks=0.3, Kd=0.22, Ke=0.02,
        thermal_iters=55, talus=0.9,
        smooth_sigma=2.2, smooth_strength=1.2,
        work_relief=48.0,
        anchors=[(30, 2), (45, 12), (56, 19), (66, 30),
                 (85, 60), (98, 85), (99.8, 92)],
    ),
}


# --------------------------------------------------------------------------- #
# Output writers
# --------------------------------------------------------------------------- #
def write_raw32(path, heights):
    """RAW32: headerless, row-major (y outer, x inner), float32 little-endian.
    `heights` is indexed [y, x]; C-order flatten yields the loader's read order."""
    data = np.ascontiguousarray(heights, dtype="<f4").tobytes()
    with open(path, "wb") as fh:
        fh.write(data)
    return len(data)


def write_png16(path, heights):
    if not _HAVE_PIL:
        return False
    lo = float(heights.min()); hi = float(heights.max())
    norm = (heights - lo) / max(hi - lo, 1e-6)
    # north-up preview: image row 0 = highest y
    img16 = np.flipud((norm * 65535.0).astype(np.uint16))
    Image.fromarray(img16, mode="I;16").save(path)
    return True


def _fmt(v):
    return "{:.4f}".format(float(v))


def write_settings(path, region, heights, bands, water, textures):
    lo, hi = bands
    stats_min = float(heights.min()); stats_max = float(heights.max())
    lines = []
    lines.append("# RegionSettings for '{}' — generated by ravenmoor_terrain.py".format(region))
    lines.append("# Heightfield: min={:.2f} max={:.2f} mean={:.2f}".format(
        stats_min, stats_max, float(heights.mean())))
    lines.append("#")
    lines.append("# Texture blend bands (matched to heightfield percentiles):")
    lines.append("#   ElevationLow  (below -> Texture1) = {:.2f} m".format(lo))
    lines.append("#   ElevationHigh (above -> Texture4) = {:.2f} m".format(hi))
    lines.append("#   WaterHeight                       = {:.2f} m".format(water))
    lines.append("#   Texture1..4 = default dirt/grass/mountain/rock (placeholders)")
    lines.append("#")
    lines.append("# In-world equivalent: Region/Estate -> Ground Textures + Terrain tabs,")
    lines.append("# or bundle the XML fragment below as settings/{}.xml in an OAR.".format(region))
    lines.append("")
    lines.append("[human-readable]")
    lines.append("ElevationLow(SW,NW,SE,NE)  = {0}, {0}, {0}, {0}".format(_fmt(lo)))
    lines.append("ElevationHigh(SW,NW,SE,NE) = {0}, {0}, {0}, {0}".format(_fmt(hi)))
    lines.append("WaterHeight                = {}".format(_fmt(water)))
    lines.append("TerrainRaiseLimit          = {}".format(_fmt(ELM_MAX)))
    lines.append("TerrainLowerLimit          = {}".format(_fmt(ELM_MIN)))
    for i, t in enumerate(textures, 1):
        lines.append("Texture{}                   = {}".format(i, t))
    lines.append("")
    lines.append("[oar-fragment]  # settings/{}.xml — per RegionSettingsSerializer".format(region))
    lo_s, hi_s = _fmt(lo), _fmt(hi)
    xml = """<?xml version="1.0" encoding="utf-16"?>
<RegionSettings>
  <General>
    <AllowDamage>False</AllowDamage>
    <AllowLandResell>True</AllowLandResell>
    <AllowLandJoinDivide>True</AllowLandJoinDivide>
    <BlockFly>False</BlockFly>
    <BlockLandShowInSearch>False</BlockLandShowInSearch>
    <BlockTerraform>False</BlockTerraform>
    <DisableCollisions>False</DisableCollisions>
    <DisablePhysics>False</DisablePhysics>
    <DisableScripts>False</DisableScripts>
    <MaturityRating>1</MaturityRating>
    <RestrictPushing>False</RestrictPushing>
    <AgentLimit>40</AgentLimit>
    <ObjectBonus>1</ObjectBonus>
  </General>
  <GroundTextures>
    <Texture1>{t1}</Texture1>
    <Texture2>{t2}</Texture2>
    <Texture3>{t3}</Texture3>
    <Texture4>{t4}</Texture4>
    <PBR1>{z}</PBR1>
    <PBR2>{z}</PBR2>
    <PBR3>{z}</PBR3>
    <PBR4>{z}</PBR4>
    <ElevationLowSW>{lo}</ElevationLowSW>
    <ElevationLowNW>{lo}</ElevationLowNW>
    <ElevationLowSE>{lo}</ElevationLowSE>
    <ElevationLowNE>{lo}</ElevationLowNE>
    <ElevationHighSW>{hi}</ElevationHighSW>
    <ElevationHighNW>{hi}</ElevationHighNW>
    <ElevationHighSE>{hi}</ElevationHighSE>
    <ElevationHighNE>{hi}</ElevationHighNE>
  </GroundTextures>
  <Terrain>
    <WaterHeight>{water}</WaterHeight>
    <TerrainRaiseLimit>{raise_}</TerrainRaiseLimit>
    <TerrainLowerLimit>{lower}</TerrainLowerLimit>
    <UseEstateSun>True</UseEstateSun>
    <FixedSun>False</FixedSun>
    <SunPosition>0</SunPosition>
  </Terrain>
</RegionSettings>
""".format(t1=textures[0], t2=textures[1], t3=textures[2], t4=textures[3],
           z=ZERO_UUID, lo=lo_s, hi=hi_s, water=_fmt(water),
           raise_=_fmt(ELM_MAX), lower=_fmt(ELM_MIN))
    lines.append(xml)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))


# --------------------------------------------------------------------------- #
# Generation
# --------------------------------------------------------------------------- #
def generate(size, seed, preset_name, overrides):
    if preset_name not in PRESETS:
        raise SystemExit("unknown preset '{}'; choose from {}".format(
            preset_name, ", ".join(sorted(PRESETS))))
    p = dict(PRESETS[preset_name])
    p.update({k: v for k, v in overrides.items() if v is not None})

    rng = np.random.default_rng(seed)

    t0 = time.time()
    base = fbm(size, p["base_cells"], p["octaves"], p["gain"], p["lacunarity"],
               rng, ridged=p["ridged"])
    t_noise = time.time() - t0

    macro = p.get("macro", "none")
    centre = None
    if macro == "valley":
        centre = valley_centreline(size, rng, p.get("macro_meander", 1.0))
        base = np.clip(base - macro_valley(centre, size, p["macro_depth"]), 0.0, 1.0)
    elif macro == "coast":
        base = np.clip(base + macro_coast(size, p["base_cells"], rng,
                                          p["macro_strength"]), 0.0, 1.0)

    work = base * p["work_relief"]

    t0 = time.time()
    work = hydraulic_erosion(work, p["hydro_iters"], p["rain"], p["Kc"], p["Ks"],
                             p["Kd"], p["Ke"])
    t_hydro = time.time() - t0

    t0 = time.time()
    work = thermal_erosion(work, p["thermal_iters"], p["talus"])
    t_thermal = time.time() - t0

    heights = remap_percentile(work, p["anchors"])

    # Detail-preserving smoothing removes per-cell (1-2 m) spikes while keeping
    # ridge crests/slopes; metre space, before the river carve.
    heights = detail_preserving_smooth(heights, p.get("smooth_sigma", 0.0),
                                       p.get("smooth_strength", 1.0))

    if macro == "valley":
        # Stamp the trunk river in metre space (after remap+smooth) so its bed is a
        # clean, continuous sub-waterline surface end to end, immune to bumpiness.
        # Erosion still shaped the valley walls and tributaries feeding toward it.
        heights = carve_river_meters(heights, centre, size, p["river_top"],
                                     p["river_bottom"], p["river_hw"], p["river_bank"])
    timings = dict(noise=t_noise, hydraulic=t_hydro, thermal=t_thermal)
    return heights, p, timings


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #
def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Procedural terrain generator for the Legion grid (RAW32).")
    ap.add_argument("--size", type=int, default=1024,
                    help="square region size; multiple of 256 (default 1024, Elm)")
    ap.add_argument("--seed", type=int, default=1, help="RNG seed (deterministic)")
    ap.add_argument("--out", default="terrain.r32", help="output .r32 path")
    ap.add_argument("--preset", default="ravenmoor-valley",
                    help="preset: " + ", ".join(sorted(PRESETS)))
    ap.add_argument("--region", default="Elm",
                    help="region name stamped into the settings fragment")
    ap.add_argument("--water", type=float, default=ELM_WATER, help="water height (m)")
    # exposed tunables (override preset)
    ap.add_argument("--hydro-iters", type=int, default=None)
    ap.add_argument("--thermal-iters", type=int, default=None)
    ap.add_argument("--rain", type=float, default=None)
    ap.add_argument("--talus", type=float, default=None)
    ap.add_argument("--work-relief", type=float, default=None)
    ap.add_argument("--no-png", action="store_true", help="skip PNG preview")
    args = ap.parse_args(argv)

    if args.size % REGION_GRAIN != 0 or args.size < REGION_GRAIN:
        raise SystemExit(
            "--size must be a multiple of {} (>= {}); got {}. The RAW32 loader "
            "infers a square dimension trimmed to a multiple of {}.".format(
                REGION_GRAIN, REGION_GRAIN, args.size, REGION_GRAIN))

    overrides = dict(hydro_iters=args.hydro_iters, thermal_iters=args.thermal_iters,
                     rain=args.rain, talus=args.talus, work_relief=args.work_relief)

    if not _HAVE_SCIPY:
        print("WARNING: scipy not found — erosion runs without sediment advection "
              "/ smoothing; results differ. Install scipy for full quality.",
              file=sys.stderr)

    print("[gen] size={} seed={} preset={}".format(args.size, args.seed, args.preset))
    t0 = time.time()
    heights, p, timings = generate(args.size, args.seed, args.preset, overrides)
    total = time.time() - t0

    # --- sanity report ---
    hmin, hmax, hmean = float(heights.min()), float(heights.max()), float(heights.mean())
    frac_wet = float((heights < args.water).mean()) * 100.0
    print("[stats] min={:.2f} max={:.2f} mean={:.2f} m  |  below water({:.0f}m)={:.1f}%"
          .format(hmin, hmax, hmean, args.water, frac_wet))
    print("[time] noise={:.2f}s hydraulic={:.2f}s thermal={:.2f}s total={:.2f}s"
          .format(timings["noise"], timings["hydraulic"], timings["thermal"], total))

    # --- slope distribution (per-cell, 1 m/cell) — spikiness gate ---
    slope = slope_degrees(heights, cell=1.0)
    s_med, s_p90, s_p99 = np.percentile(slope, [50, 90, 99])
    pct_steep = float((slope > 55.0).mean()) * 100.0
    print("[slope] median={:.1f} p90={:.1f} p99={:.1f} deg  |  >55deg={:.2f}%  max={:.1f}"
          .format(s_med, s_p90, s_p99, pct_steep, float(slope.max())))

    spiky = pct_steep > 2.0
    if spiky:
        print("[WARN] SPIKY: {:.2f}% of cells exceed 55 deg (>2% gate) — terrain will "
              "look pointy at avatar scale; retune before loading.".format(pct_steep),
              file=sys.stderr)
    if hmax > ELM_MAX or hmin < ELM_MIN:
        print("[WARN] heightfield exceeds Elm terrain limits {:.0f}..{:.0f} m "
              "(min={:.2f} max={:.2f}) — clamp or retune before load."
              .format(ELM_MIN, ELM_MAX, hmin, hmax), file=sys.stderr)
    if hmax < args.water:
        print("[WARN] entire field is below the water level ({:.0f} m) — all sea."
              .format(args.water), file=sys.stderr)

    # --- write outputs ---
    nbytes = write_raw32(args.out, heights)
    expected = args.size * args.size * 4
    print("[raw32] wrote {} ({} bytes; expected {})".format(args.out, nbytes, expected))
    assert nbytes == expected, "RAW32 size mismatch"

    if not args.no_png:
        png_path = args.out + ".png"
        if write_png16(png_path, heights):
            print("[png] wrote {} (16-bit grayscale, north-up)".format(png_path))
        else:
            print("[png] Pillow not available — skipped preview", file=sys.stderr)
        # hillshade exposes per-cell spikiness that flat grayscale hides
        if _HAVE_PIL:
            shade = np.flipud(hillshade(heights, azimuth_deg=315.0))
            hs_path = args.out + ".hillshade.png"
            Image.fromarray(shade, mode="L").save(hs_path)
            print("[png] wrote {} (NW-sun hillshade — judge spikiness here)".format(hs_path))

    set_path = args.out + ".settings.txt"
    band_lo = float(np.percentile(heights, 8))
    band_hi = float(np.percentile(heights, 92))
    write_settings(set_path, args.region, heights, (band_lo, band_hi),
                   args.water, DEFAULT_TEXTURES)
    print("[settings] wrote {} (bands {:.1f}..{:.1f} m)".format(set_path, band_lo, band_hi))

    return 0


if __name__ == "__main__":
    sys.exit(main())
