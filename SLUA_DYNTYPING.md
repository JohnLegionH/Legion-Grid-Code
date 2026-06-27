# SLua Tier-2: Dynamic Typing — Design + Proof (2nd & last hard VM piece)

**Branch:** `slua-tier2-tables` (continued — keeps Tier-2 cohesive). **Status:** built + proven offline; **NOT deployed**. Additive; LSL + Tier-1 + tables all unaffected (regression-tested). With this, **both hard VM-type extensions (tables + dynamic typing) are done** — the rest of Tier-2 is mostly front-end.

---

## 1. Design decisions (stated, with reasoning)

1. **The boxed .NET runtime type IS the type discriminator** — no separate tag/wrapper. `int`/`float`→number, `string`→string, `LSLTable`→table, `LuaNil`→nil, `FunctionInfo`→function. **The only type that was missing is `boolean`** (Tier-1/tables used int `0/1`, which collides with number `0`). So this pass is small in concept: add a real boolean + the operations that need true type knowledge. Reasoning: reuses the existing operand representation; minimal, additive, and serialization already keys on the boxed type.
2. **Lua boolean = boxed .NET `bool`.** A distinct .NET type (so `type()`/`==` distinguish it from number), naturally a tag, and `SafeOperandsPush`/`_Load` accept it (non-null). LSL never produces a boxed bool, so this is purely additive — existing LSL ops never see one.
3. **nil = the existing `LuaNil` sentinel** from the tables pass (reused, not re-invented). Forced earlier because the VM forbids `null` on the stack.
4. **Truthiness via a dedicated `luatruthy` opcode** (value → int 1/0), NOT the existing `booleval` (which uses LSL truthiness where `0` is false). `luatruthy` implements the Lua rule: **only `nil` and `false` are falsy**; `0`, `""`, tables, functions are truthy. All conditionals/`and`/`or` route through it; `brf`/`brt` are unchanged (LSL-safe).
5. **Comparisons return a real `boolean`**, not int. Essential: if `a<b` returned int `0`, `luatruthy` would see it as truthy (Lua: `0` is truthy) — wrong for a *boolean* false. So relational ops emit `flt`/`fgt`/… then **`tobool`**, and equality uses **`luaeq`** (which pushes a bool). This keeps truthiness correct for comparison results.
6. **`==` is type-aware (`luaeq`):** different Lua types are never equal (`1 == "1"` → **false**); numbers compare by value (`1 == 1.0` true, both "number"); strings by value; tables by reference identity; nil==nil. (Replaces the old float-coercing `feq`.) `~=` = `luaeq` + `lnot`. The `x == nil` fast-path stays via `isnil`.
7. **Coercions implemented vs rejected (Lua semantics):**
   - **Arithmetic** (`+ - * / %`): operands coerced to number by the VM ops (`ConvToFloat`), so number-like strings work (`"10" + 5` → `15`). Number result. (bool/nil/table in arithmetic → VM error, ~Lua.)
   - **Concat `..`** (`concat` opcode): coerces **number and string only** to string (`1 .. 2` → `"12"`); bool/nil/table → error ("attempt to concatenate a <type> value"), matching Lua (use `tostring` for those).
   - **Comparison**: numeric relational via `ConvToFloat`; `==` type-aware (no cross-type coercion). String relational (`"a" < "b"`) is **deferred** (numeric `<` only) — flagged.
8. **Operations as opcodes (not syscalls)** — `type`/`tostring`/`tonumber`/`..` are pure value ops, so opcodes match the existing pattern better than adding `ll`-table entries. `tostring`/concat use **Luau number formatting** (integral floats print without `.0`: `5.0`→`"5"`, `2.5`→`"2.5"`).
9. **Serialization: one addition** — boolean. `ProtoMember(12) bool? ValueBool` on `SerializedLSLPrimitive` (nullable so presence is distinct from `false`). nil already serializes (`ProtoMember(11)` + `LuaNil`); number/string already did. Extended, not replaced — same machinery as tables.
10. **Front-end `Dynamic` pseudo-type (from tables) carries booleans too** — boolean/relational/`type`/`tostring`/`tonumber`/table-read results are `Dynamic` (no static cast); the VM's consumption-time coercion + the new ops do the rest. **Metatable seam unchanged** (still via `LSLTable.Get/Set`); `__eq`/`__tostring` wait.

**Forks flagged (chose minimum-viable, didn't silently expand):**
- **String relational comparison** (`<`/`<=` on strings) deferred — numeric only this pass.
- **`and`/`or` return the operand value** (Lua semantics) via short-circuit, not a coerced boolean — implemented correctly (needed `dup`).
- **Integer vs float number subtypes:** SLua numbers stay **float** (Luau numbers are doubles); `type()` is "number" for both. No separate Lua-5.3 integer subtype (Luau doesn't have one) — consistent with Luau, not a gap.

## 2. What was built
**11 new opcodes** (appended after the table opcodes; existing values unchanged): `pushtrue`, `pushfalse`, `luatruthy`, `lnot`, `tobool`, `luaeq`, `concat`, `luatype`, `luatostr`, `luatonum`, `dup`.

**Modified (all additive, existing tracked files — no new files, no csproj change):**
- `Types/OpCodes.cs` — 11 opcodes.
- `VM/Interpreter.cs` — 11 dispatch cases (`pushtrue`/`pushfalse` inline; rest call `Op_*`).
- `VM/Interpreter.Actions.cs` — `Op_*` impls + helpers (`LuaIsTruthy`, `LuaEquals`, `ConcatStr`, `LuaTypeName`, `LuaToString`, `LuaNumToStr`).
- `Serialization/SerializedLSLPrimitive.cs` — `ProtoMember(12)` boolean + `IsValid` for bool/nil.
- `SLua/SLuaCompiler.cs` — `true`/`false`→bool; `..`; `not`/`and`/`or` (short-circuit) with Lua precedence (`or<and<cmp<..<+<*<unary`); `type`/`tostring`/`tonumber` builtins; type-aware `==`/`~=`; relational→boolean; `EmitCondBool` now emits `luatruthy` (correct truthiness everywhere).

## 3. Proof (offline, recording shim; PASS)
Test script exercises every required item (in `/_sluaproof/`). Outputs (expected == actual, 21 values):
- **`type()`**: `nil, boolean, number, string, table` ✓ (each kind distinct).
- **Truthiness (the headline correctness fix)**: `if 0` → prints `0-truthy`; `if ""` → `empty-truthy`; `if nil` → `nil-falsy`; `if false` → `false-falsy`. ✓ **`0` and `""` are truthy; `nil`/`false` falsy** — proper Lua, not the old not-nil approximation.
- **`tostring` + `..`**: `"n=" .. tostring(42)` → `n=42`; `1 .. 2` → `12`. ✓
- **Coercion**: `tostring("10" + 5)` → `15` (number-like string coerces in arithmetic). ✓
- **`not`/`and`/`or`**: `not false`→`true`; `true and "yes"`→`yes`; `false or "fallback"`→`fallback` (return the operand value). ✓
- **Type-aware equality**: `1 == 1`→`true`; `1 == "1"`→**`false`** (different types). ✓
- **Serialization**: a **boolean (`b=true`) and a nil (`x=nil`) in scope** survived **serialize→deserialize→resume between events** (75 bytes) — the restored handler read `type(b)`=`boolean`, `tostring(b)`=`true`, `type(x)`=`nil`; and a **mid-handler** round-trip (139 bytes) resumed correctly. ✓

## 4. Regression (LSL + Tier-1 + tables unaffected)
`LSL Compile()` **OK** · `LSL list serialize` **OK** · `Tier-1 SLua` (touched×9) **OK** · `Tables` (string+int key, `#t`) **OK**. New opcodes appended (no existing value changed); boolean/`luatruthy` are additive — no existing opcode semantics touched.

## 5. Friction / next-piece signal
- **Modeling on existing machinery held extremely well** — "the boxed type is the tag" meant only *one* new value type (bool) + one serialization field. The hard part was *correctness of semantics* (truthiness, type-aware `==`, comparison-returns-bool), not representation.
- **One subtlety worth recording:** comparison results MUST be a real boolean, else `luatruthy` mis-reads a `0`-valued false as truthy. `tobool`/`luaeq` close that.
- **Both hard VM pieces are now done (tables + dynamic typing).** Realistic remaining Tier-2 surface, and it **is mostly front-end**:
  - `string.*` / `math.*` standard libraries — front-end maps to existing `ll*`/new helper opcodes or syscalls; mechanical (~1 session for a useful subset).
  - Numeric `for i=a,b,c do` — pure front-end (~small).
  - Multiple assignment / multiple return — front-end + minor VM stack handling (~small–medium).
  - `string` relational compare, `tostring` metamethod — small.
  - **Closures/upvalues** and **`LLEvents:on`/DetectedEvent objects** — the larger remaining items; closures touch the VM (capture), the LLEvents object model is front-end + a small object wrapper. These are the next "medium" pieces, not "hard VM-type" pieces.
  - **Metatables** — deferred; seam ready in `LSLTable.Get/Set`.
  - Estimate: a useful, broadly-compatible SLua (stdlib subset + numeric-for + multi-assign) is ~2–4 more sessions, all front-end-dominant; closures + LLEvents another ~2–3. **No more hard VM-type extensions expected.**

## 6. Verdict
**YES — SLua has real dynamic typing, end to end including serialization.** Distinct `nil`/`boolean`/`number`/`string`/`table`; correct Lua truthiness (`0`/`""` truthy, `nil`/`false` falsy); type-aware equality; coercions per Lua; `type()`/`tostring()`/`tonumber()`/`..`; and booleans + nil round-trip through the existing `RuntimeState`/protobuf path. No existing LSL behavior or opcode semantics changed. **Both hard VM-type extensions (tables + dynamic typing) are complete; the remaining Tier-2 work is mostly front-end.** Metatables/closures/coroutines/broader stdlib deferred with seams in place.
