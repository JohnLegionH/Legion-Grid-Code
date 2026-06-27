# SLua Tier-2: Tables — Design + Proof (the first VM-type extension)

**Branch:** `slua-tier2-tables` (off `slua-tier1`). **Status:** built + proven offline; **NOT deployed** (build/prove pass only). Additive; LSL + Tier-1 SLua unaffected (regression-tested).

---

## 1. Design decisions (stated, with reasoning)

1. **Table storage = ordered map** (`Dictionary<object,object>` for O(1) lookup **+** a `List<object>` of keys in insertion order), NOT a true Lua array+hash hybrid. Reasoning: minimum-viable, and the insertion order makes iteration **stable and serialization deterministic** — essential so a table survives serialize→resume mid-iteration. *(Fork flagged — see §6.)*
2. **Tables are MUTABLE reference types** (unlike immutable `LSLList`). Lua tables are mutable and shared by reference; `t[k]=v` mutates the one boxed object on the stack/slot in place. This is the key behavioral difference from `LSLList`.
3. **Keys normalized**: integral number → boxed `int`, strings → `string` (so `t[1]` and `t[1.0]` are the same slot, matching Lua). Int + string keys only in Tier-2.
4. **`#t` (length) = contiguous integer-key sequence from 1** (the common Lua sequence border). Sparse-sequence `#` semantics deferred.
5. **Iteration = `pairs()` via a `next()` protocol** over the insertion-ordered keys (`ipairs` accepted as an alias — same order). `tabnext` returns the following (value,key) or nil-cursor at end. Chosen over an index-based form because `next` is what survives serialization cleanly.
6. **Serialization models `LSLList`/`SerializedLSLPrimitive` exactly** — a new `SerializedLSLTable` (ordered key/value `SerializedLSLPrimitive` lists). It reuses `FromPrimitive`/a new `ResolveValue` recursively, so **nested tables and tables-of-lists round-trip** (LSLList didn't need recursion; tables do). A new `ProtoMember(10)` on `SerializedLSLPrimitive` carries the table; `ToPrimitiveList`/`ToPrimitiveStack` now resolve via `ResolveValue` (behavior-identical for existing list/primitive cases).
7. **nil = a `LuaNil` singleton sentinel, NOT .NET null.** Forced by the VM: `SafeOperandsPush` and `_Load` *reject null* (an LSL bug-guard). A non-null sentinel loads/stores/serializes like any value (a `ProtoMember(11)` bool marks it). Tables never *store* nil (assigning nil removes the key), so `LuaNil` only lives transiently on the stack / in a slot (e.g. the iteration cursor).
8. **Table values are "Dynamic" in the front-end** — `tabget`/table-literal/`nil` results get no static type; the front-end emits **no cast** and relies on the VM's existing **consumption-time coercion** (`ConvToFloat`/`ConvToInt` in arithmetic ops, the syscall shim for `ll` args). This deliberately leans on existing machinery instead of building the full dynamic-typing system (a separate Tier-2 piece). *(Seam — see §6.)*
9. **Memory accounting** mirrors `LSLList` (`Util.MemoryCalc.CalcSizeOf` + per-container `0x20000` cap in `LSLTable.CheckMemorySize`). Caveat: `CalcSizeOf` returns 0 for the (unregistered) `LSLTable` type, so a table counts as 0 in the *aggregate* `MemInfo`; the per-table cap still bounds it. Registering `LSLTable` in `RuntimeMirror` for aggregate accounting is a follow-up.
10. **Metatable seam (deferred, clean):** all key access funnels through `LSLTable.Get/Set` (one choke point) and the `tabget`/`tabset` opcodes. `__index`/`__newindex` later hook inside `Get/Set` (on miss, consult a future `_metatable` field) with **zero** change to the opcodes or the front-end.

## 2. What was built
**New opcodes (7, appended after `booleval` — existing opcode values unchanged):** `pushnil`, `buildtable N`, `tabget`, `tabset`, `tablen`, `tabnext`, `isnil`.

**New files** (all added to the tracked `InWorldz.Phlox.csproj`):
- `Types/LSLTable.cs` — the runtime table (ordered map, normalize/Get/Set/Length/Next, memory).
- `Types/LuaNil.cs` — the nil sentinel.
- `Serialization/SerializedLSLTable.cs` — recursive protobuf wrapper.

**Modified (additive):**
- `Types/OpCodes.cs` — 7 opcodes.
- `VM/Interpreter.cs` — 7 dispatch cases.
- `VM/Interpreter.Actions.cs` — 7 `Op_*` implementations (mirroring `Op_BuildList` etc.).
- `Serialization/SerializedLSLPrimitive.cs` — `ProtoMember(10)` table + `ProtoMember(11)` nil + `ResolveValue` + table-aware `FromPrimitive`/`ToPrimitiveList`/`ToPrimitiveStack`/`IsValid`.
- `SLua/SLuaCompiler.cs` — front-end: table literals (`{}`,`{1,2}`,`{x=1}`,`{[k]=v}`, mixed), indexing (`t[k]`/`t.x`) read+assign, `#t`, `nil`, `== nil`/`~= nil`, `for k,v in pairs(t)`, `table.insert(t,v)`; two-pass local allocation (for iteration temps); a `Dynamic` pseudo-type with consumption-time coercion.

## 3. Proof (offline, recording shim observes `ll.*`)
Test script (creates table, string+int keys, nested table, length, `table.insert`, `pairs` iteration; in `/_sluaproof/`):
```lua
--!slua
local t = {}
function timer()
    t["a"] = 100;  t["b"] = 200;  t[1] = 1;  t[2] = 2
    t["nested"] = { x = 42, y = 7 }
    ll.Say(0, t["a"]); ll.Say(0, t[2]); ll.Say(0, #t)   -- 100, 2, 2
    table.insert(t, 99); ll.Say(0, #t)                  -- 3
end
function touch_start(n)
    ll.Say(0, t["a"]); ll.Say(0, t["nested"]["x"]); ll.Say(0, #t)  -- 100, 42, 3
    for k, v in pairs(t) do ll.Say(0, k) end            -- a,b,1,2,nested,3
end
ll.Say(0, "ready")
```
**Harness flow + result (PASS):**
- Build the table in `timer()` → outputs `100, 2, 2, 3`. ✓ create / get-by-string-key / get-by-int-key / length / `table.insert`.
- **Serialize between events** (table in a global) = **172 bytes** → deserialize → fresh interpreter → dispatch `touch_start` → reads `t["a"]`=100, `t["nested"]["x"]`=42 (nested survived), `#t`=3. ✓ table + nested round-trip.
- **Serialize MID-`pairs()`-iteration** = **449 bytes** (captures the cursor local + operand stack + table) → deserialize → resume → completes the loop printing all keys exactly once in insertion order: `a, b, 1, 2, nested, 3`. ✓ **the hard case** — mid-execution table + iteration state survives serialize→resume (region-crossing).
- `expected == actual` over all 14 outputs → **RESULT: PASS**.

## 4. LSL + Tier-1 SLua unaffected (regression-tested)
- `LSL Compile()`: **OK** (LSL path untouched).
- `LSL list serialize round-trip`: **OK** (the `ToPrimitiveList/Stack` → `ResolveValue` refactor is behavior-identical for lists/primitives).
- `Tier-1 SLua` (pinned script): **ready=1, touched=9 → OK** (the codegen refactor preserves Tier-1 output).
- New opcodes appended (no existing opcode value changed); new VM type is additive.

## 5. Friction / next-piece signal
- **Modeling on `LSLList` held well** for the *type* + *memory* shape, but tables needed two things lists didn't: (a) **mutability** (in-place Set, not copy-on-write), and (b) **recursive serialization** (`SerializedLSLList.FromList` sets `Value=obj` raw because LSL lists never nest; tables must use `FromPrimitive`/`ResolveValue`). Both were clean additions.
- **The null-forbidden VM guard** (`SafeOperandsPush`/`_Load`) was the one real surprise — it forced the `LuaNil` sentinel + its serialization. Worth knowing for any future "absent/optional" value.
- **Dynamic typing is the next domino.** Tables produce values of unknown static type; this pass leaned on the VM's consumption-time coercion (works for numbers→arithmetic/`ll`-args and strings→`ll`-args) and approximated truthiness (not-nil). A real `type()`, distinct `false`, and string ops (`..`, `tostring`) need the dynamic-typing piece. Estimate: dynamic typing is comparable in size to this tables pass (a VM-value + coercion-rules effort, ~1 focused session); then `table.*` stdlib + closures are mostly front-end.

## 6. Forks flagged (stated, proceeded with minimum-viable)
- **Array+hash hybrid vs ordered-map:** chose ordered-map. Trade: simpler + deterministic serialization, at the cost of true Lua sparse-sequence `#` semantics and array-part perf. Bolt-on later if needed.
- **Full dynamic typing vs consumption-time coercion:** chose to lean on existing runtime coercion for Tier-2 tables; full dynamic typing (truthiness incl. `false`, `type()`, string coercion ops) is the next piece. The `Dynamic` pseudo-type + the single `Get/Set` choke point are the seams.

## 7. Verdict
**YES — tables work in SLua-on-Phlox end to end, including serialization.** A SLua script creates tables (incl. nested + literals), reads/writes by string and integer key, takes length, `table.insert`s, and iterates with `pairs` — and the table **survives serialize→deserialize→resume both between events and mid-iteration**, via the existing `RuntimeState`/protobuf machinery extended (not replaced). No existing LSL behavior or opcode semantics changed. Metatables and full dynamic typing are the deferred next pieces, with clean seams left for both.
