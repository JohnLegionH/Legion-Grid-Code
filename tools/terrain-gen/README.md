# ravenmoor_terrain.py — procedural terrain generator (Legion grid)

Standalone Python tool that synthesizes region terrain for the Ravenmoor gothic
aesthetic and emits an OpenSim **RAW32** (`.r32`) heightfield, plus a 16-bit
grayscale PNG preview and a matched RegionSettings fragment. It is a **tool, not
server code** — run it on your workstation, then `terrain load` the `.r32` on a
region whose dimensions match the file.

## Pipeline

```
fBm / ridged Perlin base  ->  macro shaping (valley trough / coast)
  ->  vectorized virtual-pipes hydraulic erosion  ->  thermal (talus) erosion
  ->  percentile-anchored remap to metres  ->  [valley: stamp trunk river]
  ->  RAW32 (.r32) + 16-bit PNG + settings.txt
```

**Erosion approach:** a fully-vectorized **virtual-pipes** hydraulic model (Mei et
al.) — pure numpy for the flow/flux core, scipy `map_coordinates` for
semi-Lagrangian sediment advection — plus vectorized thermal weathering. Chosen
over particle/droplet erosion because it stays O(cells) per iteration and
completes 1024² in seconds (droplet erosion crawls at that size in Python). No
coarse-simulate-then-upsample was needed.

**Runtime (this machine, numpy 1.26 / scipy 1.16):**

| size  | noise | hydraulic | thermal | total |
|-------|-------|-----------|---------|-------|
| 256²  | 0.05s | 0.25s     | 0.03s   | ~0.3s |
| 512²  | 0.23s | 2.6s      | 0.5s    | ~3.3s |
| 1024² | 0.9s  | 9.9s      | 1.9s    | ~13s  |

## Requirements

```
pip install numpy scipy pillow
```

numpy is required. scipy powers sediment advection + de-alias smoothing (without
it the tool still runs but erosion quality drops and output differs — it warns).
Pillow is only for the PNG preview.

## Presets

Heights are sized to the **Elm** test canvas: 1024×1024 var-region, terrain limits
**-100..+100 m**, fixed **20 m** waterline.

| preset | character | height envelope |
|--------|-----------|-----------------|
| **ravenmoor-valley** ★ | deep carved valley, steep gothic ridges, one continuous dark river eroded to ~18.4–19.4 m so the 20 m water fills it end-to-end through the valley floor | river ~18–19, floor ~22–30, ridges ~80–94 |
| **highlands** | rolling eroded uplands with scattered tarns dipping below the waterline | ~16–70, most 30–70 |
| **islefjord** | water-dominant dramatic coast; most of the field submerged with steep fjord walls | ~2–92, ~57% below 20 m |

The flagship river is verified to be a **single connected channel spanning the
full region** (not scattered pools) across seeds.

## Usage

```
python ravenmoor_terrain.py --size 1024 --seed 1 --preset ravenmoor-valley --out ravenmoor.r32
```

Outputs three files next to `--out`:
- `ravenmoor.r32` — the heightfield (1024² = **4,194,304 bytes**; load target)
- `ravenmoor.r32.png` — 16-bit grayscale preview (north-up), judge without the viewer
- `ravenmoor.r32.settings.txt` — matched `ElevationLow/High` bands, `WaterHeight`,
  texture UUIDs; both human-readable and as a `settings/<Region>.xml` OAR fragment

### Suggested first run

```
python ravenmoor_terrain.py --preset ravenmoor-valley --seed 1 --out ravenmoor_s1.r32
```

(1024² Elm defaults.) Then iterate seeds 2, 3, … and compare the `.png` previews.

### CLI

```
--size N          square region size, multiple of 256 (default 1024; use 256/512 to iterate fast)
--seed N          RNG seed — deterministic (default 1)
--out FILE.r32    output path (default terrain.r32)
--preset NAME     ravenmoor-valley | highlands | islefjord (default ravenmoor-valley)
--region NAME     region name stamped into the settings fragment (default Elm)
--water M         water height in metres for stats/settings (default 20)
--no-png          skip the PNG preview
# preset overrides (tunables):
--hydro-iters N   --thermal-iters N   --rain F   --talus F   --work-relief F
```

Sanity report prints min/max/mean, percent below water, and per-stage timings.
It **WARNs** if any cell exceeds Elm's -100..+100 limits, or if the whole field
sits below the waterline.

## Loading on a region (console sequence)

The RAW32 loader **hard-rejects dimension mismatches** — the file must be exactly
the region's size (Elm = 1024×1024 = 4,194,304 bytes).

```
change region Elm
terrain load ravenmoor.r32     # must match region dims exactly, or it errors
terrain bake                   # when the result is a keeper (sets the revert baseline)
terrain revert                 # undo back to the last bake while iterating
```

Terrain loaded into a running region is pushed to connected clients immediately
and persisted to the `terrain` table by the periodic terrain-save tick (a few
seconds) and on shutdown; `terrain bake` + `save oar` make it durable.

Set the texture blend bands + waterline from `*.settings.txt` via **Region/Estate
→ Ground Textures + Terrain** tabs, or bundle the XML fragment as
`settings/<Region>.xml` in an OAR (a later slice).

## Verify loop (operator)

1. Generate `ravenmoor-valley` at 2–3 seeds (`--seed 1/2/3`).
2. `terrain load` each on Elm (or a spare 1024 region).
3. Fly the valley — judge the river, ridge silhouettes, erosion channels.
4. Iterate seed / `--work-relief` / `--hydro-iters`; `terrain revert` between tries.
5. Keeper → `terrain bake`, apply the settings bands.

## Determinism

Same `--seed` + params → **byte-identical** `.r32` (verified via SHA-256). All
randomness derives from `numpy.random.default_rng(seed)`; rain is uniform (not
random); no wall-clock or `Math.random` inputs. Cross-machine byte-identity
additionally assumes the same numpy/scipy build (elementwise float ops are
deterministic; there are no order-dependent BLAS reductions in the pipeline).

## Not in this slice

Texture assets, vegetation, OAR packaging (settings fragment only), server-side
code. See the Slice-0 recon for the follow-on slices (S2 vegetation, S3 prim
structure grammar, S4 mesh).
