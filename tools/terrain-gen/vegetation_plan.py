#!/usr/bin/env python3
# vegetation_plan.py — biome-driven tree placement planner for generated terrain.
#
# Re-runs the SAME seed+preset synthesis as ravenmoor_terrain.py and reuses its
# in-memory byproducts (altitude, slope, hydraulic moisture/flow, river centreline
# — the data advantage the live scene lacks) to place trees in coherent, clustered
# stands per biome rules. Emits a JSON plan + a hillshade preview PNG with species-
# coloured dots for human review BEFORE anything rezzes.
#
# The companion C# module (GeneratedVegetationModule) reads the JSON and rezzes via
# IVegetationModule.AddTree. See README "Vegetation pass".
#
# Deterministic: same seed+params => identical plan.

import argparse
import json
import sys

import numpy as np

import ravenmoor_terrain as rt

# Species codes verified against OpenMetaverse.Tree (OpenMetaverseTypes.dll) by
# reflection — do NOT guess these. Cypress1/2 are the tall dark gothic conifers
# (the module scales them x8x8x20); Pine1/2 fill the stands; WinterPine/WinterAspen
# give the dead/winter mood; Oak is the broadleaf riverbank.
TREE = {
    "Pine1": 0, "Oak": 1, "Dogwood": 4, "Cypress1": 7, "Cypress2": 8, "Pine2": 9,
    "WinterPine1": 11, "WinterAspen": 12, "WinterPine2": 13, "Eucalyptus": 14,
}
SPECIES_RGB = {                       # preview dot colours
    "Cypress1": (30, 70, 40), "Cypress2": (20, 55, 35),
    "Pine1": (40, 90, 55), "Pine2": (35, 80, 50),
    "Oak": (110, 140, 70), "WinterPine1": (90, 110, 120),
    "WinterPine2": (80, 100, 110), "WinterAspen": (150, 150, 160),
}

# Biome rules per preset. Bands are non-overlapping altitude/slope niches, evaluated
# in list order (first match wins). scale is the pre-adapt multiplier passed to
# AddTree (the module multiplies by ~8, ~8x8x20 for Cypress); ~0.7-1.2 => ~6-10 m
# broadleaf / ~14-24 m cypress spires.
BIOMES = {
    "ravenmoor-valley": dict(
        water=20.0, dead_species="WinterAspen", dead_frac=0.04,
        clearing_count=3, clearing_radius=30.0,
        bands=[
            dict(name="riverbank", alt=(20.5, 24.0), slope_max=34,
                 near_river=45.0, species=["Oak"], scale=(0.5, 0.8),
                 cluster_r=16, per_cluster=10, weight=0.8),
            dict(name="floor", alt=(24.0, 30.0), slope_max=38,
                 species=["Cypress1", "Pine1", "Oak"], scale=(0.7, 1.1),
                 cluster_r=22, per_cluster=24, weight=0.85),
            dict(name="forest", alt=(30.0, 64.0), slope_max=42,
                 species=["Cypress1", "Cypress2", "Pine1", "Pine2"],
                 scale=(0.7, 1.25), cluster_r=26, per_cluster=34, weight=1.0),
            dict(name="treeline", alt=(64.0, 74.0), slope_max=38,
                 species=["WinterPine1", "WinterPine2"], scale=(0.4, 0.62),
                 cluster_r=20, per_cluster=7, weight=0.3),
        ],
    ),
    "highlands": dict(
        water=20.0, dead_species="WinterAspen", dead_frac=0.05,
        clearing_count=2, clearing_radius=28.0,
        bands=[
            dict(name="lowland", alt=(20.5, 34.0), slope_max=30,
                 species=["Oak", "Pine1"], scale=(0.6, 1.0),
                 cluster_r=24, per_cluster=22, weight=0.9),
            dict(name="upland", alt=(34.0, 58.0), slope_max=32,
                 species=["Pine1", "Pine2", "WinterPine1"], scale=(0.5, 0.95),
                 cluster_r=22, per_cluster=18, weight=0.7),
        ],
    ),
    "islefjord": dict(
        water=20.0, dead_species="WinterPine2", dead_frac=0.05,
        clearing_count=2, clearing_radius=26.0,
        bands=[
            dict(name="shore", alt=(20.5, 30.0), slope_max=34,
                 species=["Pine1", "Cypress1"], scale=(0.6, 1.0),
                 cluster_r=20, per_cluster=16, weight=0.9),
            dict(name="wall", alt=(30.0, 60.0), slope_max=48,
                 species=["Cypress1", "Cypress2", "WinterPine1"], scale=(0.6, 1.1),
                 cluster_r=22, per_cluster=20, weight=0.8),
        ],
    ),
}


def _slope_falloff(slp, smax):
    # 1 well under the cap, ramping to 0 at the cap
    return np.clip((smax - slp) / max(smax * 0.4, 1e-3), 0.0, 1.0)


def build_maps(fields, size, biome):
    """Per-cell band id (-1 unsuitable), suitability weight, and river distance."""
    alt = fields["heights"]
    slp = fields["slope"]
    moist = fields["moisture"]
    centre = fields["centre"]
    if centre is not None:
        xs = np.arange(size, dtype=np.float32)
        dist_river = np.abs(xs[None, :] - centre[:, None] * size)
    else:
        dist_river = np.full((size, size), 1e9, dtype=np.float32)

    band_id = np.full((size, size), -1, dtype=np.int16)
    weight = np.zeros((size, size), dtype=np.float32)
    for i, b in enumerate(biome["bands"]):
        lo, hi = b["alt"]
        m = (band_id < 0) & (alt >= lo) & (alt < hi) & (slp <= b["slope_max"])
        if "near_river" in b:
            m &= (dist_river <= b["near_river"]) | (moist >= 0.6)
        w = b["weight"] * _slope_falloff(slp, b["slope_max"])
        # forest denser where moister; treeline sparser
        w = w * (0.6 + 0.4 * moist)
        band_id = np.where(m, i, band_id)
        weight = np.where(m, w, weight)
    return band_id, weight, dist_river


def pick_clearings(biome, band_id, weight, alt, slp, size, rng, forced):
    """N open sites (future abbey sites): lowest-slope forest-band spots, spaced out,
    or explicit --clearings coords. Returns list of (x, y, r)."""
    r = biome["clearing_radius"]
    if forced:
        return [dict(x=float(a), y=float(b), r=float(c)) for a, b, c in forced]
    # candidate = mid-band, low slope, decent weight
    cand = (band_id >= 1) & (slp < 12.0) & (weight > 0.4)
    ys, xs = np.where(cand)
    if len(xs) == 0:
        return []
    order = np.argsort(slp[ys, xs])           # flattest first
    chosen = []
    for k in order:
        cx, cy = float(xs[k]), float(ys[k])
        if all((cx - c["x"]) ** 2 + (cy - c["y"]) ** 2 > (3 * r) ** 2 for c in chosen):
            chosen.append(dict(x=cx, y=cy, r=r))
            if len(chosen) >= biome["clearing_count"]:
                break
    return chosen


def in_clearing(x, y, clearings):
    for c in clearings:
        if (x - c["x"]) ** 2 + (y - c["y"]) ** 2 <= c["r"] ** 2:
            return True
    return False


def plan(size, seed, preset, region, target, hard_cap, forced_clearings, water_over):
    heights, p, timings, fields = rt.generate(size, seed, preset, {}, capture=True)
    if preset not in BIOMES:
        raise SystemExit("no biome rules for preset '{}'".format(preset))
    biome = BIOMES[preset]
    water = water_over if water_over is not None else biome["water"]

    rng = np.random.default_rng([seed, 0x7EED])
    band_id, weight, dist_river = build_maps(fields, size, biome)
    clearings = pick_clearings(biome, band_id, weight, fields["heights"],
                               fields["slope"], size, rng, forced_clearings)

    # --- cluster seeding: weighted dart-throwing with Poisson min-distance ---
    flat_w = weight.ravel()
    if flat_w.sum() <= 0:
        raise SystemExit("no suitable cells for vegetation in this preset/seed")
    prob = flat_w / flat_w.sum()
    avg_per_cluster = float(np.mean([b["per_cluster"] for b in biome["bands"]]))
    n_clusters = max(1, int(target / max(avg_per_cluster, 1.0)))
    idx_pool = rng.choice(flat_w.size, size=min(n_clusters * 12, flat_w.size),
                          replace=False, p=prob)
    centres = []
    min_sep = 18.0
    for idx in idx_pool:
        cy, cx = divmod(int(idx), size)
        if in_clearing(cx, cy, clearings):
            continue
        if all((cx - a) ** 2 + (cy - b) ** 2 > min_sep ** 2 for a, b in centres):
            centres.append((cx, cy))
        if len(centres) >= n_clusters:
            break

    # --- scatter trees around each cluster (coherent single-species stands) ---
    occ = np.zeros((size, size), dtype=bool)      # coarse anti-stacking occupancy
    trees = []
    min_spacing = 3.0
    for (cx, cy) in centres:
        b = biome["bands"][int(band_id[cy, cx])]
        dominant = rng.choice(b["species"])
        rad = b["cluster_r"]
        for _ in range(int(rng.poisson(b["per_cluster"]))):
            ang = rng.uniform(0, 2 * np.pi)
            rr = abs(rng.normal(0, rad * 0.5))
            x = min(max(cx + rr * np.cos(ang), 0.5), size - 0.5)   # clamp in-region
            y = min(max(cy + rr * np.sin(ang), 0.5), size - 0.5)
            ix, iy = int(x), int(y)
            if band_id[iy, ix] < 0 or in_clearing(ix, iy, clearings):
                continue
            gx, gy = int(x / min_spacing), int(y / min_spacing)
            if occ[gy % size, gx % size]:
                continue
            occ[gy % size, gx % size] = True
            # 12% chance of a secondary species from the same band for variety
            sp = dominant if rng.random() > 0.12 else rng.choice(b["species"])
            slo, shi = b["scale"]
            s = float(rng.uniform(slo, shi))
            trees.append(dict(species=sp, code=TREE[sp],
                              x=round(float(x), 2), y=round(float(y), 2),
                              z=round(float(fields["heights"][iy, ix]), 2),
                              sx=round(s, 3), sy=round(s, 3), sz=round(s, 3),
                              rot=round(float(rng.uniform(0, 2 * np.pi)), 4)))
            if len(trees) >= hard_cap:
                break
        if len(trees) >= hard_cap:
            break

    # --- dead/winter sprinkle for mood (isolated, weighted by forest suitability) ---
    dead_sp = biome["dead_species"]
    n_dead = min(int(len(trees) * biome["dead_frac"]), hard_cap - len(trees))
    if n_dead > 0:
        picks = rng.choice(flat_w.size, size=n_dead * 3, replace=False, p=prob)
        added = 0
        for idx in picks:
            cy, cx = divmod(int(idx), size)
            if band_id[cy, cx] < 0 or in_clearing(cx, cy, clearings):
                continue
            trees.append(dict(species=dead_sp, code=TREE[dead_sp],
                              x=float(cx), y=float(cy),
                              z=round(float(fields["heights"][cy, cx]), 2),
                              sx=0.6, sy=0.6, sz=0.7,
                              rot=round(float(rng.uniform(0, 2 * np.pi)), 4)))
            added += 1
            if added >= n_dead:
                break

    return heights, fields, biome, water, clearings, trees, p


# --------------------------------------------------------------------------- #
def write_plan_json(path, meta, clearings, trees):
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(dict(meta=meta, clearings=clearings, trees=trees), fh, indent=1)


def write_preview(path, heights, trees, clearings, size):
    try:
        from PIL import Image, ImageDraw
    except Exception:
        return False
    shade = np.flipud(rt.hillshade(heights, azimuth_deg=315.0))    # north-up
    img = Image.fromarray(shade, mode="L").convert("RGB")
    dr = ImageDraw.Draw(img)

    def toimg(x, y):
        return int(x), int(size - 1 - y)      # match north-up flip

    for c in clearings:                       # clearings as red rings
        cx, cy = toimg(c["x"], c["y"]); r = int(c["r"])
        dr.ellipse([cx - r, cy - r, cx + r, cy + r], outline=(200, 40, 40))
    for t in trees:
        cx, cy = toimg(t["x"], t["y"])
        col = SPECIES_RGB.get(t["species"], (0, 200, 0))
        dr.point((cx, cy), fill=col)
    img.save(path)
    return True


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Biome-driven tree placement planner for generated terrain.")
    ap.add_argument("--size", type=int, default=1024)
    ap.add_argument("--seed", type=int, default=2)
    ap.add_argument("--preset", default="ravenmoor-valley")
    ap.add_argument("--region", default="Elm")
    ap.add_argument("--out", default="vegetation.json")
    ap.add_argument("--target-trees", type=int, default=3000,
                    help="approximate tree count (default 3000)")
    ap.add_argument("--max-trees", type=int, default=5000, help="hard cap")
    ap.add_argument("--water", type=float, default=None)
    ap.add_argument("--clearings", default=None,
                    help='explicit clearings "x,y,r;x,y,r" (else auto-selected)')
    ap.add_argument("--group-uuid", default="a7e91d0c-9e00-4c11-8ecb-9a11e6470000",
                    help="GroupID stamped on every generated tree (must match the "
                         "C# module's GENERATED_VEG_GROUP)")
    args = ap.parse_args(argv)

    if args.size % rt.REGION_GRAIN != 0 or args.size < rt.REGION_GRAIN:
        raise SystemExit("--size must be a multiple of 256")

    forced = None
    if args.clearings:
        forced = [tuple(float(v) for v in grp.split(","))
                  for grp in args.clearings.split(";") if grp.strip()]

    print("[plan] size={} seed={} preset={} target={}".format(
        args.size, args.seed, args.preset, args.target_trees))
    heights, fields, biome, water, clearings, trees, p = plan(
        args.size, args.seed, args.preset, args.region,
        args.target_trees, args.max_trees, forced, args.water)

    # --- density / count report ---
    from collections import Counter
    counts = Counter(t["species"] for t in trees)
    area_km2 = (args.size / 1000.0) ** 2
    print("[plan] {} trees ({:.0f}/km^2)  clearings={}".format(
        len(trees), len(trees) / max(area_km2, 1e-6), len(clearings)))
    print("[species] " + ", ".join("{}={}".format(k, counts[k])
                                    for k in sorted(counts)))
    if len(trees) >= args.max_trees:
        print("[WARN] hit hard cap {} — raise --max-trees or lower --target-trees"
              .format(args.max_trees), file=sys.stderr)
    if len(trees) < args.target_trees * 0.5:
        print("[WARN] only {} trees (< half of target {}) — few suitable cells for "
              "this preset/seed".format(len(trees), args.target_trees), file=sys.stderr)

    meta = dict(generator="vegetation_plan.py", region=args.region,
                preset=args.preset, seed=args.seed, size=args.size,
                water=water, count=len(trees), group_uuid=args.group_uuid)
    write_plan_json(args.out, meta, clearings, trees)
    print("[plan] wrote {}".format(args.out))

    prev = args.out + ".preview.png"
    if write_preview(prev, heights, trees, clearings, args.size):
        print("[preview] wrote {} (hillshade + species dots; red rings = clearings)"
              .format(prev))
    else:
        print("[preview] Pillow not available — skipped", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
