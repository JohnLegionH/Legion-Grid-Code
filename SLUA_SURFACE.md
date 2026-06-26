# SLua Surface Recon — the front-end's target spec (READ-ONLY)

**Purpose:** pin SL's *actual* SLua scripting surface (from source/authoritative docs) so the Tier-1 Luau front-end emits calls/events/types matching SL — not a guess. Convergence = a SLua script written for SL runs on Phlox unchanged, so the front-end must recognize SL's real syntax and route it onto Phlox's existing 674-fn table + 41-event dispatch + VM (back-half already proven, see `SLUA_TIER1.md` §8).
**Method:** read-only. No edits/build/run. Sources listed per claim; **source-verified vs inferred separated in §7.**

## Sources consulted
- **`github.com/secondlife/slua`** (reachable). The repo is the **VM/serialization fork** ("SLua = ServerLua, a friendly fork of Luau … serializable relocatable scripted entities", with **"Ares"** = modified Eris). Dirs: `Analysis, Ast, CLI, CodeGen, Compiler, LSLBuiltins, VM, …`. **Key finding: the repo does NOT contain the `ll`-table runtime registration or the `LLEvents` implementation** — `LSLBuiltins/src/LSLBuiltins.cpp` (the only LSL-specific source) handles **constants + LSL type mapping only** (`{"integer", LSLIType::LST_INTEGER}`, `lua_pushvector`, etc.), not function binding. The `ll` function binding + event plumbing live **simulator-side (closed source)**. So the repo verifies the *type system + serialization*; the **user-facing call/event syntax is authoritatively defined by the SL wiki**, which is what a script author (and thus our front-end) targets.
- **SL wiki:** `SLua_Alpha`, `Luau_Examples`, `SLua_FAQ`, and **`User:SuzannaLinn_Resident/LuaMovingTo`** (the LSL→SLua migration guide — the most explicit authority on the mapping). These four agree on the surface below.

---

## 1. The `ll` function namespace  — **source-verified**
- **Call form:** `ll.<Name>(args)` — a global table `ll` whose members are the LSL library functions **with the `ll` prefix dropped**.
  - `llSay(0,"x")` → **`ll.Say(0, "x")`**;  `llGetPos()` → `ll.GetPos()`;  `llGetColor(ALL_SIDES)` → `ll.GetColor(ALL_SIDES)`;  `llHTTPRequest(...)` → `ll.HTTPRequest(...)`. (All four sources concur.)
- **Population/exposure:** `ll` is a global table injected into the script environment by the simulator (registration code not in the open repo). For our purposes it's "a global table; member access `ll.Name` = a library call."
- **Return values:** standard Luau call returns; LSL single-return functions return one value, void functions return none. Luau multi-return exists but LSL-derived `ll` funcs are single/zero-return (no evidence of multi-return `ll` funcs).

**Mapping onto Phlox (verified against our table):** Phlox `Defaults.SystemMethods` holds the functions as **`llSay`, `llAbs`, …** (532 `ll*` names confirmed in-tree, of the 674 total). So the front-end resolves a SLua call by **prepending `ll` to the member name** → `ll.Say` ⇒ `llSay` ⇒ look up in `Defaults.SystemMethods` → `TableIndex` → emit `syscall llSay`. **This is the entire `ll`-routing job — no VM/table change.**
- *Caveat (note, not Tier-1 blocker):* Phlox also exposes `iw*` (InWorldz) and `os*` (OSSL) extension namespaces. SL's SLua has no `iw`; whether SL exposes OSSL-equivalents under another table is irrelevant to SL-parity. Tier-1 targets **`ll.*` only**; decide `os*`/`iw*` namespacing later.

## 2. The event model — **source-verified** (two coexisting forms)
SLua has **two** ways to handle events; they differ in **parameter delivery**:

**(A) Global-function form — LSL-parity, canonical for migration.** *(LuaMovingTo: "Events are written as global functions (without the `local` keyword). They have the same names and parameters.")*
```lua
function touch_start(num_detected) ... end
function listen(channel, name, id, message) ... end
function timer() ... end
```
- **Names = LSL event names** (`state_entry`, `touch_start`, `timer`, `listen`, `http_response`, `control`, `run_time_permissions`, …).
- **Params = LSL params, same order/arity** (scalars). Detected data via the usual `ll.DetectedKey(i)`, `ll.DetectedName(i)`, … (themselves just `ll.*` calls).

**(B) `LLEvents` form — dynamic registration.** *(SLua_Alpha, Luau_Examples.)*
```lua
LLEvents:on("touch_start", function(detected: {DetectedEvent})
    local who = detected[1]:getKey()
end)
-- also seen as a direct property: function LLEvents.touch_start(detected: {DetectedEvent}) ... end
```
- Enables **runtime/multiple** handlers (no LSL state machinery needed).
- Delivers detected data as an **array of `DetectedEvent` objects** with methods (`:getKey()`, …) instead of scalar params.

**Mapping onto Phlox (the key Tier-1 insight):** Phlox already dispatches via `SupportedEventList` (41 events, **same names**) → `PostedEvent { EventType, Args }` → `RuntimeState.DoEvent(EventInfo, …, args)` → handler `args=N`. **Form (A) maps 1:1**: a SLua global `function touch_start(num_detected)` becomes a Phlox `.evt default/touch_start: args=1` whose arg is exactly what Phlox already posts. **Zero new dispatch code, zero new opcodes.** Form (B)'s `DetectedEvent` *objects* are the only part needing new wrapper work (build object-with-methods over the detected args) → **defer to Tier-2**; Tier-1 targets form (A).

## 3. Script structure & entry — **source-verified (with one flagged nuance)**
- **No `default { }` / state block.** Script is **flat**: top-level statements + global event functions (or `LLEvents:on` registrations).
- **Entry/init:** **top-level code runs on rez** — it IS the `state_entry` equivalent. *(LuaMovingTo minimal example drops `state_entry` entirely and puts the init call at top level.)*
- **Flagged nuance:** one wiki example also wrote `function state_entry() … end` and then **called it explicitly** (`state_entry()`), implying `state_entry` is not auto-fired in form (A) and top-level code is the real init. **Confirm on first run:** whether a global `state_entry` auto-fires, or only top-level code does. (Doesn't block Tier-1: use top-level init.)
- LSL **states** (`state running;`) have no direct SLua equivalent in the trivial subset; state changes are out of Tier-1 scope.

## 4. Types & literals (trivial subset) — **source-verified**
- **`number`** = Luau **double**; holds integer and float values in one type. Standard literals (`0`, `2.0`, `"str"`).
- **`integer`** = a **distinct** 32-bit type, added by SLua **only for LSL-API compatibility**. *(SLua_FAQ: integers are "strictly for compatibility with existing LSL APIs" and **"don't have a literal form in Luau scripts."**)* You get one via the **`integer(x)` constructor** when an `ll` function's signature requires it; otherwise everything is `number`. Repo-confirmed type tag `LST_INTEGER`.
- **`vector(x,y,z)`**, **`rotation(x,y,z,s)`** (synonym **`quaternion(...)`**), **`uuid("…")`** (key) — constructor functions producing native values; member access `.x/.y/.z` (repo: `lua_pushvector`).
- **Constants** (`PRIM_TEXT`, `PERMISSION_TAKE_CONTROLS`, `ALL_SIDES`, …) are **compile-time globals** (repo `LSLBuiltins.cpp` parses them; they carry their LSL types, e.g. integer constants).

## 5. Does the trivial subset force any VM work? — **NO (one codegen subtlety)**
The trivial subset (locals, arithmetic, `if`/`while`, one `ll.Say`, one event) needs **no VM change** — consistent with the back-half proof (no new opcodes, no new serialization). **The one front-end subtlety to get right:**

> **`number`↔`integer` coercion at the `ll`-call boundary.** Phlox is statically typed (`iconst/iadd` vs `fconst/fadd`; distinct int/float). SLua arithmetic on `number` is double-typed. But `ll.Say(0, …)`'s channel param is **integer**, and the literal `0` is a `number`. So the front-end must **coerce `number`→`integer` (and vice-versa) when emitting syscall args to match each `ll` function's parameter types** — exactly what SLua's own `ll` binding does implicitly. **Phlox already has the int/float cast opcodes** (LSL does this coercion today), so this is **front-end codegen, not a VM change.** The front-end needs each `ll` function's param-type signature (available from `Defaults.SystemMethods` / `ISystemAPI`) to know when to cast.

A second, smaller codegen note: SLua **top-level `local`s captured by event functions** (closures/upvalues) are the natural home for script state. The trivial-subset front-end lowers a captured top-level `local` to a **Phlox global slot** (`gload/gstore`) — a straightforward closure→global lowering, no VM work. (Avoid full first-class closures in Tier-1.)

## 6. Revised Tier-1 trivial-subset target (correct SLua syntax)
The smallest **real** SLua script exercising locals, arithmetic, control flow, one `ll.*` call, and one event — this is the concrete thing the Tier-1 front-end must compile:
```lua
local count = 0                       -- top-level local (script state -> Phlox global slot)

function touch_start(num_detected)    -- event = global function, LSL name + LSL params (form A)
    count = count + 1                 -- locals + arithmetic (number; coerce at ll boundary)
    if count < 10 then                -- control flow
        ll.Say(0, "touched")          -- one ll.* call; 0:number -> integer coercion
    end
end

ll.Say(0, "ready")                    -- top-level init (state_entry equivalent), runs on rez
```
Front-end work to compile this: parse top-level `local` + `function name(...)` + `if` + `ll.Name(...)`; map `ll.Say`→`llSay`→TableIndex; emit the `touch_start` handler as `.evt default/touch_start: args=1`; lower `count` to a global; coerce the `0` literal to integer for the `llSay` channel param. Everything downstream (assemble → run → serialize) is the **proven back-half**.

## 7. Source-verified vs inferred
**Source-verified (repo and/or ≥2 agreeing wiki authorities):**
- `ll.<Name>` call form, prefix dropped → maps to Phlox `ll<Name>` (repo type system + 4 wiki sources + our table).
- Event **form (A)** global functions with LSL names + LSL params (LuaMovingTo explicit + Luau_Examples).
- Event **form (B)** `LLEvents:on(...)` with `{DetectedEvent}` objects (SLua_Alpha + Luau_Examples).
- Event names = LSL names; flat script, no `default{}`; top-level code = init (LuaMovingTo).
- `number`=double, distinct `integer` type with **no literal form**, `integer(x)` constructor (FAQ + LuaMovingTo + repo `LST_INTEGER`).
- `vector()/rotation()/quaternion()/uuid()` constructors (LuaMovingTo + Luau_Examples + repo `lua_pushvector`).
- "Ares"/serialization is the VM fork's job, **not** the front-end's (repo README) — consistent with our back-half proof.

**Inferred / needs first-run confirmation (NOT presented as fact):**
- Whether a global `function state_entry()` **auto-fires** vs top-level-code-only init (wiki examples differ). *Tier-1 uses top-level init regardless.*
- Exact **per-`ll`-function param/return type signatures** as SL defines them — we take them from **Phlox's own `ISystemAPI`/`Defaults` signatures** (which is what we route onto); if SL's published signature for a given function differs, that surfaces per-function during testing, not as a structural risk.
- The **`ll`-table registration mechanism** (how the simulator injects `ll`) is **not in the open repo** — irrelevant to the front-end (we route to Phlox's table), noted for completeness.
- The full **`DetectedEvent` object API** (method set beyond `:getKey()`) — only needed for event form (B), **deferred to Tier-2**.

## 8. Net effect on the Tier-1 plan
- **Confirmed cheap:** the front-end routes `ll.Name`→existing syscall and global-function events→existing dispatch, both **1:1** onto Phlox. No VM/opcode/serialization changes for the trivial subset (back-half proven).
- **One real codegen task:** `number`/`integer` typing/coercion at `ll`-call args (Phlox has the cast opcodes; needs the param-type table).
- **Two deliberate deferrals to Tier-2:** `LLEvents:on` + `DetectedEvent` objects; first-class closures/states.
- **Front-end target is now pinned to SL's real surface** (§6 script), not a guess — `SLUA_RECON.md`'s flagged uncertainties (ll binding / LLEvents / calling convention) are resolved here. Ready to build the front-end against this spec.
