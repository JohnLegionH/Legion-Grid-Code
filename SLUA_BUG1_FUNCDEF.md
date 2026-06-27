# SLUA-BUG-1 (+ related): `function T.f()` / `function T:m()` definition, `T.f()` calls, and top-level OOP ordering

**Branch:** `slua-tier2-tables`. **Status:** built + proven offline (ALL PASS); deploy pending. **Front-end only** — no VM/opcode change, no new files, no csproj change. Reuses existing `callv` + `IndexAssign`/`FuncExpr`/`EmitHandler`.

---

## 1. What was wrong + the fix
Three related table-field-function gaps (all stock Luau, all hit by any OOP/class SL script):

1. **BUG-1 — `function T.field(...)` / `function T:method(...)` definition** failed (`expected '(' near '.'`). `ParseFunction` only accepted a bare `Name`. **Fix:** parse the full Luau funcname `Name ('.' Name)* (':' Name)?`. A bare name stays a `FuncDecl` (unchanged). A dotted/colon name **desugars to `T.a.b = function(...) … end`** — an `IndexAssign` whose target is the table chain and whose value is a `FuncExpr` (reusing existing table-set + closure codegen). **Colon form injects `self` as the first parameter**, matching the existing `obj:method()` call (which passes the receiver as arg 0).

2. **Related — calling `T.f(args)`** (a function stored in a table field) also failed (`ParsePostfix` had no call-after-index). **Fix:** `ParsePostfix` now accepts `(args)` after any postfix expression → a new `CallExpr{Callee, Args}`, emitted as `EmitExpr(callee)` (the closure value, via `tabget`/`__index`) + args + **existing `callv`**. Covers `Vec.new(...)`, `a.b.c(...)`, chained calls, and tables with `__call`.

3. **Related — top-level OOP ordering.** A top-level `local a = Vec.new(...)` was hoisted into the globals-init block and ran **before** `function Vec.new` (an execStmt), so `Vec.new` was nil. **Fix:** all top-level code now runs in **source order** in the synthesized `state_entry` (rez) handler — a top-level `local x = e` lowers to a global store; slots are pre-registered so any order resolves. (A script with no top-level code keeps the Tier-1 form: locals init in globals-init, a bare `function state_entry()` still auto-fires.) This is a real frame, so nested block-locals in top-level control flow work too.

## 2. Proof (offline; ALL PASS)
The prompt's exact showcase (instances built **at top level**):
- dot defs `function Vec.new`/`function Vec.__add`; colon defs `function Vec:length`/`function Vec:scale` (implicit `self`) → `sum.x=4 sum.y=6`, `len=5`, `scaled.x=6`.
- **multi-level** `function a.b.greet()` → `hi`; **dot-chain colon** `function a.b:who()` (self) → `X`.
- **inheritance** (`function Dog:speak()` overriding `Animal:speak()` via `__index` chain) → `Cat makes a sound`, `Rex barks`.
- **colon-def `self` agrees with colon-call** (length/who both read `self`).
- **serialization:** a class instance (table + metatable holding dot/colon-defined methods) round-trips serialize→resume (259 B) and `inst:length()` still returns `5`.

**Regression:** LSL `Compile()` · SL canonical script (conformance) · bare `function f()` · closures · metatables — all OK.

## 3. No VM change
Confirmed front-end only: `function T.f` → `IndexAssign`+`FuncExpr`; `T.f(args)` → `CallExpr` → existing `callv`; ordering → existing `EmitHandler`. No opcodes added, no `Interpreter`/serialization edits, no new files.

## 4. Conformance note
Clears **SLUA-BUG-1** plus the two gaps it surfaced (dot-call; top-level OOP ordering). `local function` and anonymous `function() … end` already worked. Remaining definition-syntax niceties (per-demand): `local function f()` recursive self-reference edge, multiple-assignment from a call in all positions, Luau table-type param annotations (`: {DetectedEvent}`) — none block OOP scripts.

## 5. Verdict
**YES — `function T.field()`/`function T:method()` definitions, `T.f(args)` calls, and top-level OOP (define class → build instances at top level) all work and match Luau, including `self` agreement and serialization.** Front-end only; LSL + all prior SLua unaffected. Committed on `slua-tier2-tables`; deploy next.
