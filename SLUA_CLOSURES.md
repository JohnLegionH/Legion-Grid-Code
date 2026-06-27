# SLua Tier-2: Closures / First-Class Functions — Design + Proof (the keystone)

**Branch:** `slua-tier2-tables`. **Status:** built + proven offline; **NOT deployed**. Additive; LSL + Tier-1 + tables + dyntyping + essentials + patterns all unaffected (regression-tested). Foundation committed `b273c81a81`; front-end completion this commit.

---

## 1. Design decisions (as reviewed + nodded)

1. **Function value** = `LuaClosure { FunctionInfo Fn; UpvalCell[] Upvals }`. `type(f)` → "function".
2. **Capture model: full transitive, shared cells.** A function's local is allocated as a heap `UpvalCell` from creation **iff** captured by a nested function (a free-variable pre-scan at function entry computes the captured set). Closures hold **cell references**, so two closures capturing the same variable share it (write-through) — exact Lua semantics in memory. Transitive capture (inner-inner → outer-outer) threads upvalue descriptors through each level.
3. **Two call paths** (nodded): `call name()` for top-level named functions (static return arity), `callv argc, wanted` for value-calls (a local/param/upvalue/global holding a function). `callv` carries a **`wanted`** operand; `Op_Ret` does an **additive, opt-in** result-adjustment for callv frames only (named-call/event frames use `Wanted = -1` → existing behavior byte-for-byte unchanged). This makes value-calls support correct multi-return despite runtime-unknown arity.
4. **gsub function-repl via re-entrant `InvokeClosureSync`** — the matcher calls `fn(captures)` per match. Safe: gsub is one atomic opcode, so the VM never yields/serializes mid-callback (the C# stack is never captured).
5. **Serialization by value** (nodded): closure + upval cells + a suspended closure-call frame round-trip. **Flagged limitation:** cross-closure cell *sharing* is not preserved across a serialize boundary (two live closures sharing one cell get separate cells on resume). Single-closure capture — the returned-closure case and the proof — is correct.

**New opcodes (8 + the call-convention tweak):** `mkcell`, `cellget`, `cellput`, `getupval`, `setupval`, `pushupval`, `mkclosure funcIdx,nups`, `callv argc,wanted`. (Foundation also added them; `callv` gained the `wanted` operand.)

**Flagged limitations (deferred, clean errors):** capturing a **loop variable** in a nested function; **multi-assignment to a captured variable**; nested non-local `function f()` is treated as `local function f`; cross-closure cell sharing across serialize. Metatables, `LLEvents:on`, coroutines, varargs, table-repl gsub remain deferred.

## 2. What was built
**VM/serialization (foundation commit + this):** `UpvalCell`/`LuaClosure` (Types/LuaClosure.cs), `StackFrame.Closure`/`Wanted`/`OperandBase`, 8 opcodes + impls, `InvokeClosureSync`, gsub-fn wiring (Op_LuaCallM), `LuaPattern.GSubFunc`, `Op_Ret` opt-in adjust, `SerializedClosure`/`SerializedCell` + frame `Closure`/`Wanted`/`OperandBase`.

**Front-end (this commit, `SLuaCompiler.cs`):** nested **`FuncScope`** (parent link, locals with `IsCell`, upvalue list, captured set); **free-variable analysis** (`CollectCaptured`) to mark captured locals as cells; transitive **upvalue resolution** (`ResolveUpval`); cell-aware load/store (`EmitLoadName`/`EmitAssignName`); **function expressions** (`function(...) ... end`, `local f = function…`, `local function f…`) emitted as flattened `.def` blocks with `mkclosure` capturing the resolved cells; **unified call dispatch** (`EmitCallTo`) choosing `call` (named) vs `callv` (value) and producing exactly the wanted result count; and **completing `gsub` function-repl** (the prior pass's "needs first-class functions" error is gone).

## 3. Proof (offline; PASS — 12/12 outputs)
Script in `/_sluaproof/`:
- **Counter-maker closure** `function makeCounter() local n=0; return function() n=n+1; return n end end`: `c()` → `1,2,3`; an independent `d()` → `1`; `c()` → `4` (capture persists/updates; instances independent; shared cell correct). ✓
- **Function as argument**: `apply(double, 21)` where `double = function(x) return x*2 end` → `42`. ✓
- **`gsub` function-repl** (the keystone): `string.gsub("a1b2c3","%d", function(g) return tostring(tonumber(g)+1) end)` → `a2b3c4`. ✓
- **Closure + captured upvalue survives serialize→resume**: `touch_start` runs a closure counter in a loop; paused after printing `1`, **serialized (261 bytes)** capturing the closure `c` and its upval cell, restored, **resumed → `2,3,4,5`** — the count continued correctly. ✓

## 4. Regression (all prior work unaffected)
`LSL Compile()` **OK** · `Tier-1` **OK** · `Tables` **OK** · `Dynamic typing` **OK** · `Essentials` (multi-return, numeric for) **OK** · `Patterns` (match captures, gmatch loop) **OK**. The `Op_Ret` change is inert for named-call/event frames; new opcodes appended; the scope refactor preserves prior codegen behavior.

## 5. Friction / next-piece signal
- The **nested-scope codegen rewrite** was the bulk (free-var analysis + upvalue threading + cell handling), but landed cleanly on the committed VM foundation. The **`wanted`/`Op_Ret`** convention elegantly solved runtime-unknown value-call arity without changing named-call behavior. gsub-fn re-entrancy worked first try (atomic-opcode safety holds).
- One latent bug caught + fixed: lambda names used `$` which the assembler ID lexer rejects — renamed to `slua_lam_N`.
- **Both medium-VM-ish pieces are now behind us. Remaining Tier-2 is small/front-end:**
  - **`LLEvents:on` + `DetectedEvent`** object model — now unblocked (handlers are function values); front-end + a small object wrapper (~1 session). The last "real SL parity" item.
  - **Metatables** — seam ready in `LSLTable.Get/Set`; `__index`/`__newindex`/`__tostring` (~1 session).
  - Small: table-repl gsub (cheap now), varargs, `t:f()` method sugar, more stdlib, the flagged capture edge-cases, optional cross-closure cell-sharing serialization.
  - **Running estimate: ~2–3 sessions to broadly-SL-compatible SLua, all front-end-dominant, no hard VM extensions left.** This is a natural **deploy-batch point** — tables+dyntyping+essentials are already live; patterns + closures are committed-but-undeployed and could batch-deploy together next.

## 6. Verdict
**YES — closures / first-class functions work end to end**, including upvalue capture (persisting/updating, independent instances, shared cells), function-as-value/arg/return, the now-completed **`gsub` function-repl**, and a **closure + captured upvalue surviving serialize→resume**. No existing behavior changed. Committed on `slua-tier2-tables`; not deployed (next deploy can batch patterns + closures).
