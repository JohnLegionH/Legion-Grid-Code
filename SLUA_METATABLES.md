# SLua Tier-2: Metatables — Design + Proof (the final feature)

**Branch:** `slua-tier2-tables`. **Status:** built + proven offline; **NOT deployed** (batches with patterns + closures + LLEvents:on for the final deploy). Additive at the seam left for it (`LSLTable.Get/Set` → the `tabget`/`tabset` opcodes); LSL + all prior SLua unaffected (regression-tested). With this, SLua is feature-complete for the planned scope.

---

## 1. Design decisions + reasoning (the forks)

1. **Metatable storage = one field on `LSLTable` (`Metatable`, an `LSLTable` or null).** The cheap "has metatable?" fast-path is a single `!= null` check, so **plain tables (no metatable) are untouched and not slower** — every metamethod hook bails on `Metatable == null` before any dispatch.

2. **`__index`/`__newindex` hook in the opcodes, not in `LSLTable`.** `LSLTable.Get/Set` stay **raw** accessors (no VM knowledge). The metamethod logic lives in `Op_TabGet`/`Op_TabSet` (and `Op_MethCall`), where `InvokeClosureSync` is available for the **function form** — `MetaTableGet`/`MetaTableSet` helpers:
   - `__index`: raw hit wins; on a miss, follow the metatable's `__index` — **table form chains** (the OOP/inheritance case: `obj → Derived → Base`), **function form is invoked** `__index(t, key)`. Bounded loop (100) defends against a cyclic `__index`; the base case is raw access at the bottom of the chain.
   - `__newindex`: on assignment to an **absent** key only — table form re-targets, function form is invoked `__newindex(t, k, v)`; an existing key (or no `__newindex`) is a raw set.

3. **Operator metamethods via ONE selector opcode `luabinop <sel>` (the flagged fork).** SLua arithmetic/relational previously emitted the shared LSL `fadd/flt/...` ops. To dispatch `__add..__le` I did **NOT** change those shared opcodes (LSL must be untouched); instead the SLua front-end now emits `luabinop <sel>` (sel 0-8 = `+ - * / % < <= > >=`). The op: **fast-path** — if neither operand is a table, do the numeric op (one `is LSLTable` check, negligible); otherwise dispatch the metamethod (`gt`/`ge` reuse `__lt`/`__le` with swapped operands, per Lua). Relational selectors yield a boolean directly. `__unm` = a separate `luaunm`. `__eq` extends the existing `luaeq` (fires only between two distinct tables); `__concat` extends the existing `concat`.

4. **`__tostring` / `__call` / `__len`:** `__tostring` extends `luatostr` (so `tostring(t)` / string coercion use it). `__call` extends `Op_CallV` — a non-closure callee that is a table with `__call` dispatches `__call(self, args…)` (rides the existing `callv` machinery). `__len` extends `Op_TabLen` (`#t`).

5. **`setmetatable(t, mt)` / `getmetatable(t)`** = two opcodes (`setmeta`/`getmeta`) recognized by the front-end like `tostring`/`type`. `setmetatable` returns the table (Lua semantics).

6. **`Op_MethCall` method lookup now honors `__index`** (was a raw `Get`). This is the seam that makes `obj:method()` resolve **class methods through the metatable** — the whole point of OOP. (Bug-class fix: without it, `inst:mag2()` could never find a method defined on the class.)

7. **Serialization — cycle-safe by-value (the important fork).** A metatable is a table, so it rides the existing table path. But the **standard idiom `T.__index = T` makes the metatable self-referential** — a by-value copy would recurse forever (a latent hazard for *any* cyclic table, which metatables turn into the common case). Fix in `SerializedLSLTable`:
   - An `active` set tracks tables being serialized in the current tree.
   - A value that **is the owning table** (`T.__index = T`) → a **self marker** (`TableSelfRef`), re-linked to the rebuilt table on restore → **the common idiom round-trips correctly**.
   - `setmetatable(t, t)` → a `MetatableSelf` flag.
   - Any **deeper multi-table cycle** (A→B→A) → **broken to nil** (graceful, never infinite-loops) — **flagged limitation**.
   - The serialized format is additive (members 3/4 + a primitive flag 16); the only deployed table format (members 1/2) is unchanged, so old serialized tables still load.

**Deferred / flagged (per the prompt — explicitly OUT):**
- **Weak/GC metamethods** `__mode`, `__gc` — **out** (Phlox has no weak-ref GC hook). `__metatable` (protection), `__pairs`/`__ipairs` (deprecated), `__idiv`/bitwise — deferred (no-op/absent).
- **Cross-instance metatable SHARING** across a serialize boundary is not preserved (each instance restores its own metatable copy — methods still work). **Indirect multi-table cycles** are broken to nil on serialize. Both flagged.

## 2. What was built
**No new files** (purely additive to existing tracked files):
- `Types/LSLTable.cs` — `Metatable` field.
- `Types/OpCodes.cs` — 4 opcodes: `luabinop`, `luaunm`, `setmeta`, `getmeta` (appended; existing values unchanged; auto-mapped by the assembler).
- `VM/Interpreter.cs` — dispatch cases.
- `VM/Interpreter.Actions.cs` — `MetaRaw`/`MetaTableGet`/`MetaTableSet` helpers + the 4 new ops; extended `Op_TabGet`/`Op_TabSet`/`Op_TabLen`/`Op_LuaEq`/`Op_Concat`/`Op_LuaToStr`/`Op_CallV`/`Op_MethCall`.
- `SLua/SLuaCompiler.cs` — `luabinop`/`luaunm` emission; `MetaCall` AST + parse + `EmitMetaCall` for `setmetatable`/`getmetatable`; the three free-variable walkers descend into `MetaCall`.
- `Serialization/SerializedLSLTable.cs` — cycle-safe `FromTable`/`ToTable` (`Metatable`, `MetatableSelf`); `Serialization/SerializedLSLPrimitive.cs` — `TableSelfRef` marker.

## 3. Proof (offline; PASS)
Harness in `/_sluaproof/`. **Main proof** — an OOP `Vec` "class" using the **standard self-referential idiom** `Vec.__index = Vec`, with a method, an `__add` overload, and `__tostring`:
- `mag2=25` — **method lookup through `__index`** (`inst:mag2()`).
- `sum=(4,6)` — **`__add` operator overload** + **`__tostring`** (`tostring(inst + newVec(1,2))`).
- **Serialize (345 bytes) the self-referential class table + a live instance in globals → restore → run again:** `restored mag2=25`, `restored sum=(13,24)` — **the metatable association (and the `__index` self-cycle) survived serialize→resume**, with method lookup and operator overload still firing.

**Per-metamethod checks (all PASS):** `__newindex` (function form intercepts absent-key writes), `__call` (calling a table), `__eq` (two distinct tables compare equal), `__len`/`__unm`/`__concat`, and an **`__index` inheritance chain** (`obj → Derived → Base`).

## 4. Regression (all prior work unaffected)
`LSL Compile()` **OK** · `Tier-1` **OK** · `Closures` **OK** · `Patterns` **OK** · **Plain tables (no metatable) OK** (the fast-path: indexing/length on a metatable-less table is unchanged). New opcodes appended; metamethod hooks bail immediately when `Metatable == null`.

## 5. Next-piece signal
**Metatables done → SLua is broadly SL-compatible and feature-complete for the planned scope. No hard VM work remains.** Remaining is small/polish, optional:
- Wider DetectedEvent methods (touch face/ST/UV); table-replacement `gsub`; varargs (`...`); LLEvents handler removal / dynamic (non-literal) event-name registration.
- Serialization edges (flagged): cross-instance metatable sharing; indirect multi-table cycles.

**This is the natural point for the final batched deploy:** patterns (`509ae3c319`) + closures (`b273c81a81`/`c76e92c773`) + LLEvents:on (`e4421942a8`) + metatables (this commit) → one deploy + a comprehensive in-world test.

## 6. Verdict
**YES — metatables work end to end:** core metamethods (`__index` table+function, `__newindex`, `__add..__le`/`__unm`/`__concat`/`__eq`, `__tostring`, `__call`, `__len`), OOP method dispatch through `__index`, operator overloading, and **a table with a metatable — including the self-referential `T.__index = T` idiom — round-trips through serialize→resume** with method lookup and operators still working. Plain tables are unaffected and not slower. Weak/GC metamethods are explicitly out. Committed on `slua-tier2-tables`; not deployed (batches with patterns + closures + LLEvents:on).
