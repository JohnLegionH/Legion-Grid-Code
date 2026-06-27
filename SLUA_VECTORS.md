# SLua: Vectors & Rotations — SL-Correct Support (Design + Proof)

**Branch:** `slua-tier2-tables`. **Status:** built + proven offline (each op vs hand-computed SL-correct values); deploy + in-world pending. Additive: no new opcodes, no new files, no csproj change — rides the existing `buildvec`/`buildrot` + boxed `Vector3`/`Quaternion` and the SLua dynamic-op path. LSL untouched.

---

## 1. SL-semantics verification (the build-to spec)
**Ground truth = this grid's existing LSL vector opcodes** (Halcyon/InWorldz is SL-faithful), cross-checked with `SLUA_SURFACE.md` + the `secondlife/slua` repo (`lua_pushvector`).

| Operation | SL-correct behavior | Source |
|---|---|---|
| `vector(x,y,z)` / `rotation(x,y,z,s)` (`quaternion` synonym) | constructors → native value | surface §4 (**verified**); repo `lua_pushvector` |
| `.x/.y/.z` (vector), `.x/.y/.z/.s` (rotation) | component read, **read-only / immutable** (Luau native vector) | surface §4 + Luau semantics (**verified**) |
| `v + v`, `v - v` | component-wise → vector | LSL `vadd`/`vsub` (**verified**) |
| `v * v` | **dot product → number (scalar)** | LSL `vmul` = `Vector3.Dot` (**verified**) |
| `v % v` | cross product → vector | LSL `vcross` (**verified**, see flag) |
| `v * n`, `n * v`, `v / n` | scale → vector | LSL `vfmul`/`vimul`/`vfdiv` (**verified**) |
| `v * rot`, `v / rot` | rotate / inverse-rotate → vector | LSL `vrmul`/`vrdiv` (**verified**) |
| `rot * rot`, `rot / rot` | quaternion mul / divide | LSL `rmul`/`rdiv` (**verified**) |
| `-v`, `-rot` | negate | LSL `vneg`/`rneg` (**verified**) |
| `v == v`, `rot == rot` | by value | LSL `veq`/`req` (**verified**) |
| `tostring(v)` | `<x, y, z>` at **5 fractional digits** (`<x, y, z, s>` for rot) | LSL string-cast `Vector3ToStringWith5FractionalDigits` (**verified**) |
| `type(v)` | `"vector"` / `"rotation"` | Luau native type name (**verified** for vector; rotation name **inferred**) |
| mutability | **immutable** — no `v.x = 5` | Luau native vector (**verified**); diverges from LSL (mutable) but matches SLua |

**Inferred / flagged:** (a) `v % v` cross via `%` is LSL-faithful but SL/Luau may instead use a `vector.cross` library func — provided `%` (harmless, LSL-correct); (b) `type(rotation)` name (`"rotation"` chosen); (c) no Luau `vector` **library** namespace (`vector.magnitude` etc.) — use the existing `ll.VecMag`/`ll.VecNorm`/`ll.VecDist` functions; (d) `..` does **not** auto-stringify a vector (Luau-correct — use `tostring(v)`).

## 2. Design decisions
1. **Constructors = front-end builtins** `vector`/`rotation`/`quaternion` (same pattern as `setmetatable`), emitting the existing `buildvec`/`buildrot`. Result is a boxed `Vector3`/`Quaternion` → passes the shim's `ConvToVector`/`ConvToQuat` cast cleanly at the `ll.*` boundary. **No VM change.**
2. **Component read = extend `Op_TabGet`.** `.x/.y/.z` already lowers to `tabget`; the op now returns the component when the target is a boxed `Vector3`/`Quaternion` (read-only, immutable — matches Luau). Cleaner than a new opcode and reuses the existing emission.
3. **Arithmetic = extend `Op_LuaBinop`** (the dyn-op path metatables use): a `VectorBinop` branch dispatches the SL-correct op when an operand is a vector/rotation, mirroring the LSL opcodes exactly (dot vs scale vs rotate). `-v` extends `Op_LuaUnm`; `==` extends `LuaEquals`. **LSL's own opcodes are untouched** — only the SLua dynamic ops gained a vector branch.
4. **tostring/type** extend `LuaToString`/`LuaTypeName` (SL 5-digit format; `"vector"`/`"rotation"`).
5. **Mutability:** immutable (no component write) — matches Luau/SLua, the deliberate divergence from LSL.
6. **Serialization:** boxed `Vector3`/`Quaternion` already round-trip (the `SerializedVector3`/`SerializedQuaternion` primitive members + the table path) — confirmed, no new code.

## 3. What was built
**No new files, no new opcodes, no csproj change.** Two files:
- `SLua/SLuaCompiler.cs` — `VecCtor` AST + `vector`/`rotation`/`quaternion` parsing + `EmitVecCtor` (→ `buildvec`/`buildrot`); the 3 free-variable walkers descend into `VecCtor`.
- `VM/Interpreter.Actions.cs` — `Op_TabGet` component read (`VecComponent`/`RotComponent`); `Op_LuaBinop` + `VectorBinop`; `Op_LuaUnm` negate; `LuaEquals`; `LuaToString`; `LuaTypeName`.

## 4. Proof (offline; every arithmetic op vs hand-computed SL value — ALL PASS)
`a = vector(1,2,3)`, `b = vector(4,5,6)`:
- read: `a.x/a.y/a.z` → `1 2 3` · `a+b` → `<5,7,9>` · `a-b` → `<-3,-3,-3>`
- **`a*b` (dot) → `32`** (scalar) · **`a%b` (cross) → `<-3, 6, -3>`** · `a*2` & `2*a` → `<2,4,6>` · `a/2` → `<0.5,1,1.5>` · `-a` → `<-1,-2,-3>`
- `a == vector(1,2,3)` → `true`, `a == b` → `false`
- `tostring(a)` → `<1.00000, 2.00000, 3.00000>` (SL 5-digit) · `type(a)` → `vector`
- rotation: `rotation(0,0,0,1)` → `<0.00000, 0.00000, 0.00000, 1.00000>`, `.s` → `1`, `a * identity` → `<1,2,3>`
- **serialize→resume (111 bytes) with vectors in globals → `a+b` & `a.x` still correct.**

## 5. Regression (unaffected)
LSL `Compile()` incl. an LSL `<1,2,3>*2.0` vector cast **OK** · SLua Closures **OK** · Patterns **OK** · Metatables **OK** · Tables **OK**. All vector hooks are gated on `Vector3`/`Quaternion` operands; numbers/strings/tables/booleans take the unchanged paths, and LSL's own vector opcodes are untouched.

## 6. Correctness vs SL
**SL-correct end to end for the common surface:** construct, read, `+ - * / % == -`, scalar/rotation interactions, tostring (SL format), serialization. The one semantic that bites — `vector * vector` = **dot (scalar)** vs `vector * scalar` = **scale** — is correct, matching the LSL opcodes byte-for-byte.
**Explicit divergences/flags:** immutable (no `v.x=5`) = Luau-correct, LSL-divergent by design; `%`-cross is LSL-faithful but SL may prefer `vector.cross` (flagged); no `vector.*` library namespace (use `ll.VecMag`/`ll.VecNorm`/`ll.VecDist`); `..` needs explicit `tostring(v)`; `[1]/[2]/[3]` indexing not supported (SL uses `.x/.y/.z`).

## 7. Verdict
**YES — SLua vectors/rotations are SL-correct end to end:** construction, immutable component read, the full SL operator set (with dot-vs-scale right), SL-format tostring, and serialize→resume — all matching this grid's LSL semantics exactly, proven against hand-computed values. Additive (no opcodes/files/csproj), LSL + all prior SLua unaffected. Committed on `slua-tier2-tables`; deploy + in-world (`ll.SetColor(vector(...))` / `ll.SetPos(vector(...))`) next.
