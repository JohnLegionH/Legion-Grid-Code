# SLua Conformance Audit vs SL/Luau — the gate before the Tranquillity port

**Method:** read/analyze the deployed front-end (`SLuaCompiler.cs`) + VM + stdlib (`LuaLib.cs`) against ground truth — `secondlife/slua` repo, SL wiki (`SLua_Alpha`), Luau core semantics, and this grid's SL-faithful LSL opcodes. Offline tests where behavior needed confirming. **No fixes applied — this is the divergence map.** Date: 2026-06-27.

---

## 1. Executive summary
SLua on Legion is **broadly SL-conformant for the language core** (types, truthiness, operators, closures, metatables, patterns, vectors/rotations all match SL/Luau), but has **two classes of real conformance bugs that will make common SL scripts fail unchanged**:

1. **Script structure (`state_entry`) — rejects SL's canonical example.** SL's own basic script defines `function state_entry()` *and* has top-level code that calls it; this build throws `"top-level code and an explicit state_entry() are both present; not supported"`. An SL author's default script does not compile here. **Highest-priority bug.**
2. **Standard-library coverage gap.** Only 8 `math.*` (no `sin/cos/tan/atan/…`), only `table.insert` of `table.*`, no `pcall/error/assert/print`, no `bit32`/`utf8`/`os`, no `integer()` constructor. The reported `math.sin`/`math.cos` failures are the tip of this. SLua advertises "the Luau Standard Library"; we ship a small subset.

Everything else is either a **MATCH** or a **documented necessary divergence** (the `--!slua` header, keeping Form-1 events). Net: the engine is sound; the gaps are **breadth (stdlib) + one structural rule**, not deep semantics. Closing them is additive front-end/stdlib work (no hard VM work), consistent with the rest of SLua.

---

## 2. Bucketed audit (MATCH / NECESSARY-DIVERGENCE / BUG-TO-FIX)

| # | Area | Verdict | Evidence (SL vs this build) |
|---|------|---------|------|
| 1 | **Script detection / header** | NECESSARY-DIVERGENCE | SL: viewer compiler dropdown, no in-script marker. Here: `IsLuaScript` routes if source trim-starts with `--!slua`, `--!lua`, or **any `--`** (LSL never starts with `--`, so safe). *SL author must add a `--!slua` first line.* |
| 2 | **Script structure / `state_entry`** | **BUG-TO-FIX** | SL (`SLua_Alpha`): `function state_entry() … end` is a **plain function**, **not** auto-fired; top-level code runs on rez and **calls `state_entry()` explicitly**. Here: `state_entry` is treated as an auto-fired Form-1 event, and a script with both a `state_entry` function and top-level code is **rejected** (`SLuaCompiler.cs:1236`). SL's canonical script fails to compile. |
| 3 | **`ll.*` namespace** | MATCH | `ll.Name(args)` → `llName` → `Defaults.SystemMethods` (all ~532 `ll*` funcs present incl. `SetAlpha`/`SetScale`/`SetColor`/`TargetOmega`/`ParticleSystem`/`GetPos`/`SetPos`). PascalCase, prefix dropped — matches SL. |
| 4a | **Events — Form-2 `LLEvents:on`** | MATCH | `LLEvents:on("evt", fn)` + `{DetectedEvent}` array supported. |
| 4b | **Events — Form-1 globals** | NECESSARY-DIVERGENCE | SL **deprecated** global-function handlers (Nov 2025); Legion **keeps** them. *Harmless: SL scripts using Form-2 work; legacy Form-1 also works here.* |
| 4c | **DetectedEvent method names** | CAN'T-FULLY-VERIFY | `getKey`/`getName` verified vs wiki; `getPos/getOwner/getGroup/getType/getVel/getRot/getLinkNumber` inferred from `llDetected*`. Simulator-side; matched to best available. |
| 4d | **`LLTimers:once/off`** | BUG-TO-FIX (gap) | SL exposes an `LLTimers` object. Here: absent — timers only via Form-1 `timer()` + `ll.SetTimerEvent`. An SL script using `LLTimers:once(...)` fails. |
| 5a | **Types** | MATCH | number(double-ish), boolean, nil, string, table, vector, rotation, function all present; `type()` → `"vector"`/`"rotation"`/`"number"`/… matches Luau. |
| 5b | **Truthiness / `..` / `==` / tostring** | MATCH | Only `nil`/`false` falsy (`LuaIsTruthy`); `..` coerces number/string only (Luau-correct, vectors need `tostring`); `==` type-aware; tostring matches Luau number format + SL 5-digit vector format. |
| 5c | **`integer()` constructor** | BUG-TO-FIX (small) | SL has `integer(x)` (FAQ: integers have no literal form). Here: absent. Mostly masked by auto number→int coercion at the `ll.*` boundary, but an explicit `integer(x)` call fails to compile. |
| 5d | **`uuid()` / key type** | BUG-TO-FIX (small) | SL has `uuid("…")`. Here: keys are plain strings (works for comparison/passing), but a `uuid(...)` constructor call fails. |
| 6 | **Stdlib completeness** | **BUG-TO-FIX (major)** | See §3 table. `math.*`: 8 of ~25 (no trig/exp/log/pow/…). `table.*`: only `insert` (statement-only). `string.*`: core 12 present, missing `reverse`/`split`/`pack`. No `bit32`/`utf8`/`os`/`buffer`. No `print`. JSON/base64 only via `ll.Json*`/`ll.*Base64*`, not SL's `lljson.*`/`llbase64.*` namespaces. |
| 7 | **Pattern matching** | MATCH (one gap) | `find/match/gmatch/gsub` faithful Lua patterns (verified). `gsub` string + function repl; **table repl deferred** (minor vs SL). |
| 8 | **Closures / first-class fns** | MATCH (flagged) | Lua/Luau semantics correct. Flagged: cross-closure cell **sharing** not preserved across a serialize boundary; loop-var capture per-iteration. SL-relevant only across region-cross/save with shared upvalues — rare. |
| 8b | **Metatables** | MATCH (flagged) | `__index`(chain)/`__newindex`/operators/`__call`/`__tostring`/`__len`/`__eq` correct; `T.__index=T` round-trips. Flagged: indirect multi-table cycles broken to nil on serialize; cross-instance metatable sharing not preserved. |
| 9 | **Numbers / indexing** | MATCH | 1-based indexing; `#` operator; int vs number invisible to scripts (`type()`→"number" for both). |
| 10 | **Error behavior** | **BUG-TO-FIX** | No `pcall`/`xpcall`/`error`/`assert` (Luau core) — scripts can't trap/raise errors. *Good:* unsupported features fail at **compile time with a clear message** (`"unsupported … in the Tier-2 subset"`), not silently. Runtime errors shout + halt (LSL-like). |
| 11 | **Vectors / rotations** | MATCH (documented divergences) | SL-verified (SLUA-VEC): construct, `.x/.y/.z(.s)`, `+ - * / % == -`, dot-vs-scale, SL tostring, serialize. Divergences (all documented): **immutable** (Luau-correct, LSL-divergent), `%`-cross (SL may use `vector.cross`), no `[1]/[2]/[3]` indexing, no `vector.*` library (use `ll.VecMag/VecNorm/VecDist`). |

---

## 3. Stdlib coverage detail (the math.sin/cos entry point → whole-library map)

**`math.*` — present (8):** `floor, ceil, abs, min, max, sqrt, random, randomseed` + values `pi`, `huge`.
**`math.*` — MISSING (Luau has):** `sin, cos, tan, asin, acos, atan, atan2, sinh, cosh, tanh, exp, log, log10, pow, fmod, modf, frexp, ldexp, deg, rad, round, sign, clamp, noise, map`, value `tiny`. → **trig + transcendental + clamp/round/sign are the painful gaps.**

**`string.*` — present (12):** `format, sub, len, upper, lower, rep, byte, char, find, match, gmatch, gsub`.
**`string.*` — MISSING:** `reverse, split, pack, unpack, packsize`. (Luau `string.split` is common.)

**`table.*` — present (1):** `insert` (statement-only, simple-name target, 2-arg only).
**`table.*` — MISSING:** `remove, concat, sort, unpack, find, clear, create, clone, move, freeze, isfrozen`. → **big gap; `table.concat`/`table.remove`/`table.sort` are everyday.**

**Whole libraries MISSING:** `bit32` (SL confirms it ships), `utf8`, `os` (time/clock/date), `buffer`, the Luau `vector` **library** (we have the `vector()` constructor + ops, not `vector.magnitude/normalize/dot/cross`), `coroutine` (SL ships create/resume/status), `lljson` (JSON only via `ll.JsonGetValue` etc.), `llbase64` (only via `ll.StringToBase64`/`ll.Base64ToString`).
**Globals MISSING:** `print, pcall, xpcall, error, assert, select, next, rawget, rawset, rawequal, rawlen, unpack/table.unpack, typeof, newproxy`. **Present:** `type, tostring, tonumber, pairs, ipairs, setmetatable, getmetatable`.

---

## 4. BUG-TO-FIX list (prioritized — accidental divergences that break "an SL script runs here")
1. **`state_entry`/top-level structure (#2)** — rejects SL's canonical script. *Fix direction (for review, not done):* when top-level code is present, treat `state_entry` as a normal user function (SL semantics: not auto-fired; author calls it), so the default SL script compiles and doesn't double-fire.
2. **`math.*` trig + transcendentals (#6)** — `sin/cos/tan/atan/atan2/exp/log/pow/fmod/rad/deg/round/clamp/sign`. Pure `LuaLib` additions (map to `System.Math`). **The reported failure.**
3. **`table.*` library (#6)** — `remove/concat/sort/unpack` + `table.insert` as expression/3-arg. Everyday data manipulation.
4. **`pcall`/`error`/`assert` (#10)** — Luau core error handling; scripts that guard with `pcall` won't compile.
5. **`print` (#6)** — trivial; map to debug/owner channel.
6. **`bit32` library (#6)** — SL ships it; bit ops otherwise need LSL `ll.*` integer ops.
7. **`integer()` / `uuid()` constructors (#5c/#5d)** — small; constructors SL scripts may call explicitly.
8. **`LLTimers:once/off` (#4d)** — SL timer object; or document "use `ll.SetTimerEvent` + `timer()`".
9. **`string.split`/`string.reverse` (#6)**, **`gsub` table-repl (#7)** — common-ish.
10. **Library namespaces `lljson.*`/`llbase64.*` (#6)** — alias to existing `ll.Json*`/`ll.*Base64*`.

(Lower libraries `utf8`/`os`/`buffer`/`coroutine`/`vector` library + Luau `select/next/raw*` — decide per demand; `os.time`/`coroutine` are the likeliest wanted.)

## 5. NECESSARY-DIVERGENCE list → "porting an SL script to Legion" guide
- **Header:** add a first line `--!slua` (server compiles by marker, not a viewer dropdown).
- **Events:** Form-1 global handlers also work (Legion keeps them; SL deprecated) — no change needed either way.
- **Vectors:** immutable — replace `v.x = 5` with `v = vector(5, v.y, v.z)`; cross product is `a % b`; component access `.x/.y/.z` only (no `v[1]`); magnitude/normalize via `ll.VecMag`/`ll.VecNorm`.
- **`..` with a vector:** use `tostring(v)` explicitly (Luau, not a divergence, but a common porting snag).

## 6. Can't-verify (simulator-side / undocumented — matched to best available)
- Full `DetectedEvent` method set beyond `getKey`/`getName` (mapped to `llDetected*`).
- Whether SL auto-fires anything on rez besides top-level code (wiki implies **no** — top-level code is rez; `state_entry()` is called manually). This build's auto-fire of a `state_entry` function is itself a divergence (see #2).
- Exact SL `tostring`/number formatting edge cases (matched to Luau + this grid's LSL string-cast).
- `%`-as-cross vs a `vector.cross` library function (both plausible; provided `%`).

## 7. Recommendation
**Before declaring SL-conformant / before the Tranquillity port, fix the "runs-unchanged" breakers:**
- **(a) the `state_entry` structural rule** (#2) — small front-end change, highest impact;
- **(b) the stdlib breadth** — at minimum `math` trig/transcendentals, `table.remove/concat/sort/unpack`, `pcall/error/assert`, `print` (#6/#10). These are pure `LuaLib`/front-end additions, no VM work, and turn "a typical SL script" from *fails* to *runs*.

**Acceptable as-documented (port-guide, not bugs):** the `--!slua` header, Form-1 retention, vector immutability/`%`-cross, the flagged serialization-sharing edges.

**Defer pending demand:** `bit32`/`utf8`/`os`/`buffer`/`coroutine`/Luau `vector` library, `lljson`/`llbase64` namespaces, `LLTimers`, `integer()`/`uuid()` constructors, `gsub` table-repl.

**Bottom line:** the SLua *engine* is SL-conformant; the remaining gap is **library breadth + one structural rule** — a well-scoped, VM-free batch. Recommend one "conformance" pass (state_entry + stdlib breadth + pcall/print) before the port, then re-run this audit as the gate.
