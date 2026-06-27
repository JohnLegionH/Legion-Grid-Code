# SLua Tier-2: Everyday Essentials — Design + Proof (stdlib core + numeric for + multi-assign/return)

**Branch:** `slua-tier2-tables` (continued). **Status:** built + proven offline; **NOT deployed**. Additive; LSL + Tier-1 + tables + dynamic typing all unaffected (regression-tested). Front-end-dominant (both hard VM pieces were already done).

---

## 1. Design decisions (stated, with reasoning)

1. **Stdlib exposure = one `luacall` opcode + a pure `LuaLib` dispatcher**, NOT a per-function opcode or `ll`-table entries. `luacall` takes two operands (lib-func id, arg count); the VM pops the args, calls `LuaLib.Call(id, args)`, pushes the result. Reasoning: `string.*`/`math.*` are scene-independent value functions, so they need no `ISystemAPI`/shim and their results serialize like any operand. The front-end resolves `string.format`→`Func.StrFormat` etc. via a static name map. One opcode keeps the VM surface tiny; adding stdlib functions later is just enum + dispatch + a map entry.
   - Reconciliation with `ll.*`: `ll.*` stays on its own scene-bound syscall path (674-fn table). `string`/`math` are a *separate*, pure dispatch — no overlap. `math.pi`/`math.huge` are member *values*: `pi`→an `fconst` literal, `huge`→`luacall(MathHuge,0)`.
2. **`string.format` directives supported:** `%d %i %u`, `%f`/`%.Nf`, `%e %E`, `%g %G`, `%s` (with `.N` truncate), `%x %X`, `%c`, `%%`, plus flags `-`/`0` and width (`%5d`, `%-8s`, `%05.2f`). A self-contained printf-style parser (Lua's format is C-printf-like; C#'s `String.Format` syntax differs, so it's hand-rolled). **Deferred:** the rarer `%q`, `*` width, locale specifics.
3. **Numeric `for` codegen** over existing opcodes, no sign branching: continue while **`(i - stop) * step <= 0`** (true for both step>0 `i<=stop` and step<0 `i>=stop`). Loop var `i` + hidden `_stop`/`_step` are locals; `step` defaults to `1.0`; `i += step` each iteration; `flte`+`brf` for the test. (step==0 → infinite, as Lua errors; not guarded — flagged.)
4. **Multi-return rules.** The VM made this free: `Op_Ret` leaves the operand stack untouched, so **return values live on the shared operand stack** — no new opcode. A user function is declared with a **fixed return count R** (max arity among its `return`s) and a param count P (computed in a pre-pass), and **always leaves exactly R values** (returns pad/truncate to R; fall-off pushes R nils). Call sites adjust to context:
   - **statement** `f(x)` → discard all R.
   - **single-value expression** → keep 1 (pad nil if R=0, pop R−1 if R>1).
   - **last expr of a value list** (multi-assign RHS / return list) → expands to R; earlier list exprs adjust to 1.
   - Args are adjusted to P (pad nil / truncate) so `_Call` pops the right count.
5. **Multi-assignment** (`local a,b = …`; `a,b = b,a`): evaluate the **whole RHS list first** (so swap works), adjust the produced count to the LHS count (pop extras / pad nils), then store right-to-left (stack top = last value). Tier-2 multi-assign targets are **simple names** (single `t[k]=v` index-assign still works; index targets *inside* a multi-assign are deferred — flagged).
6. **User functions added** (top-level `function name(...)` whose name is not an event) → emitted as `.def name: args=P, locals=L` via the same two-pass local allocation as event handlers; calls via the existing `call` opcode. Forward references resolve through the assembler's existing reference-fixup pass. This is the one "new construct," but it rides entirely on the existing call/ret machinery (no VM change).

**Forks flagged (chose minimum-viable):**
- **Lua pattern matching** (`string.match`/`gmatch`/`gsub`) — **deferred** (its own engine); calls to unsupported `string.*`/`math.*` raise a clear front-end error rather than mis-running.
- **Varargs `...`** — deferred (functions have fixed P/R).
- **Index targets in multi-assignment** and **method calls `t:f()`** — deferred.
- Long tail of each library (`os.*`, `string.pack`, `utf8.*`, `table.*` beyond the tables pass) — deferred.

## 2. What was built
**1 new opcode:** `luacall` (operands: func id, arg count). **1 new file** (added to the tracked `InWorldz.Phlox.csproj`): `SLua/LuaLib.cs` — the `string.*`/`math.*` implementations + `string.format`.

**Modified (additive):** `Types/OpCodes.cs` (luacall); `VM/Interpreter.cs` (dispatch); `VM/Interpreter.Actions.cs` (`Op_LuaCall`); `SLua/SLuaCompiler.cs` — parser: numeric/generic `for` dispatch, `local a,b`/`a,b=`/return-lists, `string.`/`math.` access, user calls; codegen: user functions (`.def`, return-count pre-pass, calling convention + context adjustment), numeric `for`, multi-assign, stdlib calls, `math.pi`/`math.huge`.

**Stdlib delivered:** `string.format/sub/len/upper/lower/rep/byte/char`; `math.floor/ceil/abs/min/max/sqrt/random/randomseed/huge/pi`.

## 3. Proof (offline; PASS — 20/20 outputs)
Script uses `string.format`, `math.*`, a numeric `for`, a multi-assign swap, and a multi-return function; in `/_sluaproof/`. Results:
- **`string.format("hi %s, n=%d, f=%.2f", "bob", 42, 3.14159)`** → `hi bob, n=42, f=3.14` ✓
- **`math.floor(3.7)`=3, `math.max(1,9,4)`=9, `math.abs(-5)`=5** ✓
- **numeric `for i=1,5 do sum=sum+i end`** → `15` ✓
- **multi-assign swap** `local a,b=10,20; a,b=b,a` → `20,10` ✓
- **multi-return** `function minmax(a,b) … return … end; local lo,hi=minmax(8,3)` → `3,8` ✓
- **nested stdlib** `string.upper(string.sub("hello",1,3))` → `HEL` ✓
- **mid-`for`-loop serialize→resume:** `touch_start` runs `for i=1,10 do acc=acc+i; ll.Say(acc) end`; paused after it printed `1`, **serialized (202 bytes)** capturing the loop state (`i`,`acc`,`_stop`,`_step`) + operand stack, restored into a fresh interpreter, and **resumed to print `3,6,10,…,55`** — the full cumulative sequence with no skips/dupes. ✓ (the loop-state round-trip — the key proof.)

## 4. Regression (all prior work unaffected)
`LSL Compile()` **OK** · `Tier-1 SLua` (touched×9) **OK** · `Tables` **OK** · `Dynamic typing` (`type(true)`=boolean; `if 0` truthy; `if nil` falsy) **OK**. New opcode appended (existing values unchanged); user functions ride existing `call`/`ret`; no existing opcode semantics touched.

## 5. Friction / next-piece signal
- **Front-end-dominant, as predicted** — only 1 new opcode (`luacall`), and it's a generic dispatch. The multi-return "free lunch" (return values on the shared operand stack) was the nicest find; the work was the **calling-convention bookkeeping** (fixed P/R + context adjustment), not VM mechanics.
- **`string.format` was the biggest single piece** (a printf parser), exactly as the prompt predicted ("the workhorse").
- **SLua is now genuinely writable for real scripts:** variables/types, tables, control flow incl. both `for` forms, functions with multi-return, string building (`..` + `string.format`), and the common `math`/`string` helpers — all serialize. A typical "respond to touch, format a message, do some math, loop over a table" script is now expressible.
- **Remaining Tier-2 surface (all non-"hard"):**
  - **Lua pattern matching** (`string.match/gmatch/gsub`) — the largest remaining item; a self-contained matcher (~1–2 sessions). High value for text scripts.
  - **Closures/upvalues** — touches the VM (capture of enclosing locals); the one remaining *medium-VM* item (~1–2 sessions).
  - **`LLEvents:on` + `DetectedEvent` object model** — front-end + a small object wrapper (~1 session); needed for full SL event-style parity.
  - **Metatables** — seam ready in `LSLTable.Get/Set` (~1 session for `__index`/`__newindex`).
  - Long-tail stdlib, varargs, numeric-for edge guards, multi-assign index targets — small.
  - **Running estimate:** a broadly SL-compatible SLua is ~4–7 more sessions, with pattern matching + closures the only substantial pieces and **no more hard VM-type extensions**.

## 6. Verdict
**YES — the everyday essentials are in, end to end including serialization.** Core `string.*`/`math.*`, numeric `for`, and multi-assignment + multi-return (via user functions) all work, and the numeric-`for` loop state survives serialize→deserialize→resume mid-loop. No existing LSL/Tier-1/tables/dyntyping behavior changed. SLua has crossed into "usable for real scripts." Pattern matching and the long tail are deferred with seams in place.
