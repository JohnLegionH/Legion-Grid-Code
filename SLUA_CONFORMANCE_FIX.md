# SLua Conformance Fix Pass — clearing the BUG-TO-FIX list

**Branch:** `slua-tier2-tables`. **Status:** built + proven offline (ALL PASS); deploy pending. Additive — front-end + `LuaLib` stdlib + 3 opcodes + 1 exception type; LSL and all prior SLua unaffected. Closes the `SLUA_CONFORMANCE.md` runs-unchanged breakers.

---

## 1. Design decisions
- **FIX 1 — `state_entry` (front-end only).** SL treats `function state_entry()` as a **plain function** (not auto-fired); top-level code is the rez handler and the author calls `state_entry()` themselves. New rule: **when top-level code is present, `state_entry` is reclassified out of the event set into user functions** (no auto-fire, no double-fire) and the both-present rejection is removed. **With NO top-level code, a bare `function state_entry()` still auto-fires** (LSL-parity convenience for existing flat scripts). The synthesized rez `.evt default/state_entry` (from top-level code) and the user `.def state_entry` coexist in separate symbol spaces; the explicit `state_entry()` call resolves to the function. No double-fire by construction.
- **FIX 2 — stdlib.** `math.*`/`string.*`/`table.*` single-return funcs are pure `LuaLib` (map to `System.Math` / `LSLTable` ops); `math.modf`/`table.unpack` ride the existing multi-result path (`CallMulti` + `luacallm`/`adjustm`). `table.*` is now routed through the normal `LibCall` path (so `table.insert` gains 2- and 3-arg + expression use; the old statement-only special-case is retired).
- **Error handling (the items that touch the interpreter — flagged).** `error`/`pcall`/`assert` needed VM-level handling. Added: a `LuaError` exception carrying the raw error value; `luaerror` (raise), `luapcall` (protected call), `luasort` (in-place sort invoking a comparator closure). `pcall` traps `LuaError`/`CheckException`/`VMException`, unwinds frames/operands to the call site (balancing `MemInfo.CompleteCall` exactly as `Op_Ret` does), and returns `(false, err)`; on success `(true, firstResult)`. `assert` is lowered in the front-end via `luaerror`. `print` is lowered to `llOwnerSay` (tab-separated `tostring` of args). **This is additive (new opcodes + a protected-invoke), not a change to any existing op's semantics.**

**Decisions/divergences flagged:** `pcall` returns `(true, firstResult)` on success (not Luau's full multi-return) — covers the canonical `local ok, r = pcall(...)`. `print` separator is the engine's `\t` (which the assembler renders as 4 spaces, an InWorldz/Halcyon convention) → debug to the owner channel.

## 2. What was built / changed
- **New file** (tracked csproj): `Types/LuaError.cs`.
- `Types/OpCodes.cs` — `luaerror`, `luapcall`, `luasort` (appended).
- `VM/Interpreter.cs` + `VM/Interpreter.Actions.cs` — dispatch + `Op_LuaError`/`Op_LuaPcall`/`Op_LuaSort` + `PcallUnwind` + `DefaultLuaCompare`.
- `SLua/LuaLib.cs` — math breadth (sin/cos/tan/asin/acos/atan(+atan2)/exp/log/pow/fmod/deg/rad/round/sign/clamp/modf), `string.split`/`reverse`, `table.insert`/`remove`/`concat`/`unpack`.
- `SLua/SLuaCompiler.cs` — FIX 1 (state_entry reclassification, rejection removed); `table.*` routing; `CoreCall` (print/error/assert/pcall) parse + `EmitCoreCall` + call-stmt/`EmitCallTo`/`IsCallExpr` + walkers; `table.sort` → `luasort`; new `LibFuncId` entries; multi-lib set += modf/unpack.

## 3. Proof (offline; ALL PASS)
- **SL canonical script** (`function state_entry()` + `LLEvents:on("touch_start", …)` + explicit `state_entry()`) → rez: `Hello, Avatar!`; touch: `Touched.` — **compiles and behaves as on SL.**
- **Flat top-level rez** still works; **bare `function state_entry()`** (no top-level) still auto-fires.
- **math:** `sin0 0, cos0 1, round(sin(pi/2)) 1, round(cos(pi)) -1, round(deg(pi)) 180, pow 1024, fmod 1, sign -1, clamp 3, round(2.5) 3, round(atan(1,1)*100) 79` (= π/4).
- **table:** sort `1,2,3`; comparator-sort `3,2,1`; insert append + 3-arg insert; remove; `unpack 10 20`.
- **pcall/error/assert:** `pcall(boom)`→`false kaboom`; `pcall(()->42)`→`true 42`; `assert(5)`→`5`; `pcall(()->assert(false,"failed"))`→`false failed`.
- **print** → owner channel; **string.split/reverse** correct.
- **Serialization:** a script with a sorted table in a global round-trips (99 B) and still sorts after resume.

## 4. Regression
LSL `Compile()` **OK** · closures **OK** · patterns **OK** · vectors **OK** · metatables **OK**. All additions additive; no existing op semantics changed.

## 5. Conformance delta (vs SLUA_CONFORMANCE.md)
**CLEARED:** #2 state_entry structure · #6 math trig/transcendentals · #6 `table.remove/concat/sort/unpack` + `insert` 2/3-arg · #10 `pcall`/`error`/`assert` · #6 `print` · #6 `string.split`/`reverse`.
**STILL DEFERRED (per-demand, post-conformance):** `bit32`, `utf8`, `os`, `buffer`, `coroutine`, Luau `vector` library (`ll.Vec*` covers it), `lljson`/`llbase64` namespaces (JSON/b64 via `ll.*`), `LLTimers` (use `ll.SetTimerEvent`+`timer()`), `integer()`/`uuid()` constructors, `gsub` table-repl, `select`/`next`/`raw*`, Luau table-type param annotations (`: {DetectedEvent}`).

## 6. Verdict
**An SL-canonical script now runs unchanged, and the stdlib breadth gap is closed to "a typical SL script runs."** The two runs-unchanged breakers from the audit are fixed; remaining items are per-demand libraries, not conformance blockers. Re-running the audit confirms the gate is clear for the Tranquillity port (modulo the documented deferrals).
