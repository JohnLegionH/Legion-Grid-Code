# SLua Tier-2: Lua Pattern Matching — Design + Proof

**Branch:** `slua-tier2-tables` (continued). **Status:** built + proven offline; **NOT deployed** (build/prove/commit only, per request). Additive; LSL + Tier-1 + tables + dyntyping + essentials all unaffected (regression-tested).

---

## 1. Design decisions (stated, with reasoning)

1. **Faithful matcher, NOT regex.** `SLua/LuaPattern.cs` is a hand-written port of Lua 5.x `lstrlib.c`: a recursive backtracking matcher with character-class predicates, `* + - ?` greedy/lazy expansion (`MaxExpand`/`MinExpand`), a capture stack (`StartCapture`/`EndCapture`), anchors, back-references (`MatchCapture`), `%b` balanced, and `%f` frontier. Pure value-level (string→captures); **no `System.Text.RegularExpressions`**, **no VM-type change** in the matcher itself.
2. **Variable multi-capture returns** (the one real plumbing need). `match`/`find`/`gsub` return a runtime-variable number of values. Rather than statically counting captures (literal-only, silent-failure-prone), I made it **runtime-correct**: a new `luacallm funcid,argc` opcode calls `LuaLib.CallMulti`→`object[]` and pushes the values **plus a runtime count**; a new `adjustm T` opcode pops the count and pads/truncates the group to exactly `T`. This unifies with user-function multi-return (which now also routes through `EmitMultiCall`+`adjustm`, pushing a static count). Works for runtime patterns too — no silent wrong results.
3. **gmatch iterator mirrors `pairs`/`tabnext`.** A serializable value type `Types/LuaGmatch.cs` (`{src, pat, pos}`) + a `gmatchnext K` opcode (analogous to `tabnext`). `for v1..vK in string.gmatch(s,p) do` evaluates the iterator once (a `luacall` → `LuaGmatch`), then loops `load _it; gmatchnext K; brf done; store vars; body`. Because `LuaGmatch` is a plain serializable value (two strings + int), **the loop state round-trips for free** (it's just an operand/slot value, serialized like `LSLTable` via a `SerializedLuaGmatch` + `ProtoMember(13)`).
4. **The four functions, faithfully:** `find(s,pat,init,plain)` → start,end,+captures (1-based; `plain` = literal `IndexOf`, also auto-fast-paths patterns with no specials); `match(s,pat,init)` → captures or whole match; `gmatch(s,pat)` → iterator (above); `gsub(s,pat,repl,n)` → result,count. Position captures `()` → 1-based number; back-refs `%1..%9`; `%%`/`%0`/`%1..%9` substitution in `gsub` string repl.

**Flag — what was deferred (per the discipline):**
- **gsub function/table replacement:** you chose string+function repl, but **function repl requires passing a function as a value** = first-class functions, which is the *deferred closures* work (a constrained "named-function only" version would need re-entrant VM invocation — high-risk plumbing better built with closures). So **string repl is delivered fully**; function/table repl raise a **clear error** ("needs first-class functions; coming with closures") rather than silently mis-running. I'll do it properly in the closures pass.
- The *matcher* needed no VM change (as predicted); but faithful **variable-capture multi-return** + the **gmatch iterator** needed 3 additive opcodes (`luacallm`/`adjustm`/`gmatchnext`) + 1 serializable iterator type (`LuaGmatch`) — mirroring the already-sanctioned multi-return and `pairs`/`tabnext` machinery, **not** a regex engine or any change to existing opcodes.

## 2. What was built
**New files** (added to the tracked `InWorldz.Phlox.csproj`): `SLua/LuaPattern.cs` (the matcher), `Types/LuaGmatch.cs` (iterator state). **New opcodes:** `luacallm`, `adjustm`, `gmatchnext` (appended; existing values unchanged). **Modified:** `LuaLib.cs` (find/match/gsub via `CallMulti`, gmatch via `Call`), `SerializedLSLPrimitive.cs` (`ProtoMember(13)` + `SerializedLuaGmatch`), `OpCodes.cs`, `Interpreter.cs`/`Interpreter.Actions.cs` (dispatch + `Op_LuaCallM`/`Op_AdjustM`/`Op_GmatchNext`), `SLuaCompiler.cs` (multi-lib routing, unified `EmitMultiCall`, gmatch `for` codegen, `string.find/match/gsub/gmatch` resolution).

## 3. Proof (offline; PASS — 12/12 outputs)
Script exercises classes/sets/anchors/quantifiers/multiple-captures/back-ref across all four functions; in `/_sluaproof/`:
- **`string.find("hello world","wor")`** → `find: 7-9` ✓ ; **plain** `string.find("a.b.c",".",1,true)` → `findplain: 2` ✓
- **`string.match("name = bob","(%a+)%s*=%s*(%a+)")`** → `match: name/bob` ✓ (classes + `*` + 2 captures)
- **`string.match("  42px","%d+")`** → `num: 42` ✓
- **back-reference** `string.match("hello hello there","(%a+) %1")` → `dup: hello` ✓
- **`string.gsub("a1b2c3","(%a)(%d)","%2%1")`** → `gsub: 1a2b3c (3)` ✓ (capture-swap repl + count)
- **`for tok in string.gmatch("red,green,blue","[^,]+")`** → `gmatch: [red][green][blue]` ✓ (negated set)
- **mid-`gmatch`-loop serialize→resume:** `for word in string.gmatch("one two three four five","%a+")` printed `one,two,three`, **serialized (224 bytes)** capturing the `LuaGmatch` iterator state, restored, **resumed → `four,five`** with no skips/dupes. ✓ (the iterator-state round-trip — the key proof.)

## 4. Regression (all prior work unaffected)
`LSL Compile()` **OK** · `Tier-1` (touched×9) **OK** · `Tables` **OK** · `Dynamic typing` **OK** · `Essentials` (multi-return `mm(8,3)`→`3,8`; numeric for→`15`; multi-assign swap→`21`) **OK**. The shared multi-return refactor (`EmitMultiCall`+`adjustm`) is behavior-equivalent to the prior static path. New opcodes appended; no existing opcode semantics changed.

## 5. Friction / next-piece signal
- **The matcher was the bulk** and went in clean as a faithful `lstrlib.c` port (value-level). The plumbing (variable multi-return + gmatch iterator) was the fiddly part — the **runtime `luacallm`+`adjustm`** approach avoided the literal-only capture-counting trap and unified with user-function multi-return.
- **gsub function-repl is the one IOU**, blocked on first-class functions (closures) — flagged with a clear error, not faked.
- **Remaining Tier-2 surface (closing in on broadly-SL-compatible):**
  - **Closures / first-class functions** — the one remaining *medium-VM* item (local functions as values, upvalue capture); also unblocks `gsub`-function-repl and `LLEvents:on` handlers-as-values (~1–2 sessions).
  - **`LLEvents:on` + `DetectedEvent` object model** — front-end + small object wrapper (~1 session).
  - **Metatables** — seam ready in `LSLTable.Get/Set` (~1 session).
  - Long-tail stdlib (`os.*`, more `string.*`/`table.*`), varargs, `%b`/`%f` are already in — small remainders.
  - **Running estimate: ~3–5 more sessions** to broadly-SL-compatible SLua, with **closures the only substantial piece left** and no hard VM-type extensions remaining.

## 6. Verdict
**YES — Lua pattern matching works faithfully (a real `lstrlib.c`-style matcher, not regex), end to end including serialization.** `find`/`match`/`gmatch`/`gsub` all correct across classes, sets, anchors, quantifiers, multiple captures, and back-references; and a `gmatch`-driven `for` loop survives serialize→deserialize→resume mid-iteration. Only gsub function/table replacement is deferred (needs closures) with an honest error. No existing behavior changed. Committed on `slua-tier2-tables`; not deployed.
