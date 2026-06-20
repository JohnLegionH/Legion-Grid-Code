# Phlox LSL Conformance Report

**Scope.** Measure how closely the Phlox LSL engine matches real Second Life LSL behaviour. This is a conformance/behaviour audit — not a code-presence audit.

**Method.** Where possible, the result for each test is the output of actually running the LSL source through the compiled `InWorldz.Phlox.dll` (current bin, built from this tree). Where compile-only results were insufficient (runtime semantics like short-circuit evaluation), findings are based on direct reading of the implementing bytecode opcode (cited line) and corroborated by ready-to-drop in-world scripts.

**Artifacts produced.**
- `_audit/runner/` — net8 console runner that drives 136 compile-time test cases through `InWorldz.Phlox.Glue.CompilerFrontend`
- `_audit/corpus_results.tsv` — raw per-case verdicts
- `_audit/runtime_scripts/*.lsl` — six in-world scripts marked `REQUIRES IN-WORLD RUN`
- `_audit/phlox_grammar_review.md` — static review of `grammar/LSL.g4`
- `_audit/phlox_function_inventory.md` — `ll*` inventory of `ISystemAPI.cs` / `LSLSystemAPI.cs`

---

## 1. Headline numbers (measured)

| Dimension | Measured | Source |
|---|---:|---|
| Valid SL operators accepted (compile) | 31 / 31 | corpus_results.tsv |
| Invalid C-isms correctly rejected (compile) | 4 / 6 | corpus_results.tsv |
| Type casts conformant (compile) | 12 / 13 | corpus_results.tsv |
| Vector/rotation arithmetic conformant (compile) | 10 / 12 | corpus_results.tsv |
| List operations conformant (compile) | 5 / 5 | corpus_results.tsv |
| SL events accepted with correct signature (compile) | 38 / 43 sampled | corpus_results.tsv (5 newer SL events not supported) |
| Statement/flow forms conformant (compile) | 12 / 12 | corpus_results.tsv |
| String/number literal forms conformant (compile) | 8 / 8 | corpus_results.tsv |
| `quaternion` type alias accepted | 0 / 2 | corpus_results.tsv |
| **Compile corpus overall** | **124 / 136 (91.2%)** | corpus_results.tsv |
| `ll*` declared in `ISystemAPI.cs` | 491 | inventory |
| `ll*` IMPLEMENTED (substantive body) | 458 | inventory |
| `ll*` STUBBED (deprecated / no-op / default-return) | 33 | inventory |
| `ll*` MISSING (declared, no body) | 0 | inventory |
| SL canonical `ll*` found missing from `ISystemAPI.cs` | ≥ 17 (sampled, not exhaustive) | spot-check vs wiki |
| Constants in `DefaultConstants.cs` | 744 | inventory |
| Runtime spot-checks executed in interpreter | 0 / 6 | full VM bring-up out of scope; static evidence + scripts provided |

The "ran 0 of 6 runtime checks" is the most important caveat: I have **static** evidence for short-circuit, div/mod, and pre/post increment behavior (cited below), and **drop-in scripts** for the others, but I did not boot the Phlox VM with a syscall shim to execute them headless. Anyone running the scripts in-world should record what they actually observe.

---

## 2. UNDER-CONFORMANT findings (Phlox rejects valid SL)

### U1 — `quaternion` type alias unsupported
- **Repro:**
  ```
  default { state_entry() { quaternion q = <0.0,0.0,0.0,1.0>; } }
  ```
- **Phlox error:** `line 1:37 no viable alternative at input 'quaternion q'`
- **Why it matters:** SL treats `quaternion` as a synonym for `rotation`; many published scripts use it.
- **Root cause:** `grammar/LSL.g4` line 223-231 `TYPE` lexer rule lists only `integer|float|key|vector|rotation|string|list`.
- **Fix:** add `'quaternion'` to the `TYPE` alternatives, or add a separate token that maps to the same semantic type.

### U2 — `(quaternion)` cast unsupported
- **Repro:** `rotation r = (quaternion)<0.0,0.0,0.0,1.0>;`
- **Phlox error:** `line 1:55 mismatched input ',' expecting ';'`
- Same root cause as U1.

### U3 — Five SL events not in Phlox's supported-event whitelist
Each repro is `default { state_entry() {} <eventName>(...) {} }` and each fails with `Unknown LSL event '<name>'` from semantic-pass enforcement:

| Event | Phlox error |
|---|---|
| `path_update` | `Unknown LSL event 'path_update'` |
| `game_control` | `Unknown LSL event 'game_control'` |
| `on_damage` | `Unknown LSL event 'on_damage'` |
| `on_death` | `Unknown LSL event 'on_death'` |
| `final_damage` | `Unknown LSL event 'final_damage'` |

- **Root cause:** Whitelist in `InWorldz.Phlox/Types/SupportedEventList.cs` lines 59–334 does not include these. Pathfinding (`path_update`) is the most consequential omission for a server claiming SL parity since the `ll*` pathfinding functions themselves are implemented (`llCreateCharacter`, `llNavigateTo`, `llEvade`, etc.) — scripts can call them but can't receive the result event.
- **Fix:** add entries to `SupportedEventList._supportedEvents` with correct signatures and route their dispatch in the VM event-posting code.

### U4 — `llAsc` (SL canonical) absent from `ISystemAPI`
- Phlox declares the inverse `llChar` and the helper `llOrd`, both implemented.
- SL also exposes `llAsc` (deprecated alias of `llOrd`). Scripts that use the alias will fail to compile.

### U5 — Sampled SL `ll*` functions missing from Phlox API surface
Spot-checked against `Special:PrefixIndex` listings on the SL wiki. **This list is not exhaustive** — fetch was paginated and several letter ranges were not enumerated. Use it as a starting punch list, not as a complete delta.

| Function | Notes |
|---|---|
| llAddCameraView | newer SL camera control |
| llAddToEstateBanList | estate management |
| llRemoveFromEstateBanList | estate management |
| llRequestAgentKeyByName | name/key lookup |
| llRequestAgentKeyByUsername | name/key lookup |
| llRequestAgentKeysByDisplayName | name/key lookup |
| llRotateAgent | agent rotation |
| llReturnObject | object return (singular) |
| llReturnObjects | object return (plural) |
| llReturnOwnersObjects | object return |
| llRezBullet | combat 2.0 |
| llRegexParse2List | regex parsing |
| llPermuteLinkedPrims | linkset permutation |
| llMatchGroup | recent group check |
| llMapTouch | recent map UI |
| llPrompt | prompt dialog |
| llReadKeyValueExists | key-value existence check |

---

## 3. OVER-CONFORMANT findings (Phlox accepts what SL rejects)

### O1 — `<<=` compound shift-left assignment accepted
- **Repro:**
  ```
  default { state_entry() { integer r; integer a; r <<= a; } }
  ```
- **Phlox result:** compiles cleanly. **SL:** rejects.
- **Root cause:** `grammar/LSL.g4` line 75 (`assignmentStmt`) and line 131 (`assignmentExpression`) both list `'<<=' | '>>='` in the assignment-operator set. The SL operator table has neither.
- **Fix:** remove `'<<='` and `'>>='` from both rules.

### O2 — `>>=` compound shift-right assignment accepted
Same locations and same fix as O1.

### O3 — `(list)scalar` direct cast accepted
- **Repro:** `list l = (list)42;`
- **Phlox result:** compiles cleanly. **SL:** rejects — listification is `[scalar]`, not a typecast.
- **Root cause:** `lcast` opcode + permissive `(TYPE)` cast rule on integer/float/string sources. The type-check pass doesn't disallow target type `list` for a scalar cast.
- **Severity:** low — semantics are intuitive and produce a sensible list, but it's still a parity drift. Catch it in `TypesVisitor.cs` when target is `list` and source is not a list.

### O4 — `rotation + rotation` accepted
- **Repro:** `rotation r=<0.,0.,0.,1.>; rotation q=<0.,0.,1.,0.>; rotation x = r + q;`
- **Phlox result:** compiles cleanly. **SL:** `rotation + rotation` is not defined.
- **Root cause:** dedicated `radd` opcode at `OpCode.radd` (Interpreter.cs:506) — componentwise rotation addition was emitted by design. The compiler's type pass treats it as a valid `rotation+rotation -> rotation` operation.

### O5 — `rotation - rotation` accepted
Same as O4 with `rsub` opcode (Interpreter.cs:510).

---

## 4. BEHAVIOR-MISMATCH findings (compile-clean, runtime semantics differ)

None of the runtime spot-checks were executed in the interpreter. All findings here are **static reads of `Interpreter.Actions.cs`**, treated as evidence not as observation. Run the in-world scripts in `_audit/runtime_scripts/` to verify against actual SL behavior side-by-side.

### B1 — Short-circuit evaluation — STATIC: matches SL (non-short-circuit)
- **SL expected:** `&&` and `||` evaluate both operands every time. `FALSE && (1/0)` must throw Math Error.
- **Static evidence — CONFORMANT:** `Op_Iland` (Interpreter.Actions.cs:742) and `Op_Ilor` (line 727) pop **both** operands from the stack before testing — there is no conditional skip emitter for `&&`/`||`. Both operands' bytecode is unconditionally executed first.
- **Runtime confirmation:** `_audit/runtime_scripts/01_short_circuit.lsl`.

### B2 — Integer division truncates toward zero — STATIC: matches SL
- **SL expected:** `-15 / 2 == -7`.
- **Static evidence — CONFORMANT:** `Op_Idiv` (Interpreter.Actions.cs:413) is `SafeOperandsPush(a / b)` — C# `/` on `int` truncates toward zero.

### B3 — `%` sign follows dividend — STATIC: matches SL
- **SL expected:** `-7 % 3 == -1`, `7 % -3 == 1`.
- **Static evidence — CONFORMANT:** `Op_Imod` (Interpreter.Actions.cs:421) is `SafeOperandsPush(a % b)` — C# `%` on `int` takes sign of dividend.

### B4 — Div/mod by zero — STATIC: matches SL math error
- **SL expected:** Math Error.
- **Static evidence — CONFORMANT:** No explicit zero guard in `Op_Idiv`/`Op_Imod`. C# raises `DivideByZeroException`, which the Tick loop will see as an unhandled VM-level exception (and the engine surfaces this as a Math Error in the host).

### B5 — Pre vs post increment expression value — STATIC: matches SL
- **SL expected:** `++count` yields the post-increment value; `count++` yields the pre-increment value.
- **Static evidence — CONFORMANT:** distinct opcodes `ipreinc_l` and `ipostinc_l` (Interpreter.cs:290, 294) and the matching `_g` variants — the dispatcher routes them to separate handlers.
- **Runtime confirmation:** `_audit/runtime_scripts/02_pre_vs_post_increment.lsl`.

### B6 — `llRemoveInventory(llGetScriptName())` self-removal — NOT TESTED
This is the open behavioural item from current debugging. Static read insufficient — depends on the scene host's handling of inventory removal vs script execution termination. Drop `_audit/runtime_scripts/04_self_remove.lsl` and record:
1. Whether `Description = "AFTER"` is ever observed (it should NOT be).
2. Whether the script disappears from prim contents.
3. Whether it stays gone across a region restart.
**Recommend filing the result here as B6.1/B6.2/B6.3 once measured.**

### B7 — `llDialog` 12-button rendering — NOT TESTED
`_audit/runtime_scripts/06_dialog_12_buttons.lsl` — drop and confirm all 12 buttons appear and the listen fires.

### B8 — `llParseStringKeepNulls` empty fields & `llGetSubString` negative indices — NOT TESTED
`_audit/runtime_scripts/05_list_funcs.lsl` — expected description `LEN=3 LIT=a EMPTY=3 SUB=llo`.

---

## 5. Stub / missing function list

### 5.1 Stubbed (33)
The following `ll*` are declared and have bodies, but the bodies are stubs (deprecated SL functions kept for compile compatibility, Halcyon-era no-ops, Animesh placeholders, or hard-coded default returns):

`llCheckRezError`, `llCloseFloater`, `llCollisionFilter`, `llCollisionSprite`, `llDetectedDamage`, `llGetAccel`, `llGetCameraAspect`, `llGetCameraFOV`, `llGetFreeMemory`, `llGetLinkSitFlags`, `llGetMemoryLimit`, `llGetObjectAnimationNames`, `llGetOmega`, `llGetSPMaxMemory`, `llGetTorque`, `llGodLikeRezObject`, `llMakeExplosion`, `llMakeFire`, `llMakeFountain`, `llMakeSmoke`, `llMapBeacon`, `llPointAt`, `llRefreshPrimURL`, `llReplaceAgentEnvironment`, `llScriptProfiler`, `llSetAgentEnvironment`, `llSetLinkSitFlags`, `llSetPrimURL`, `llSound`, `llStartObjectAnimation`, `llStopObjectAnimation`, `llStopPointAt`, `llTargetedEmail`.

Notable groups:
- **Deprecated by SL itself** (intentional no-op): `llMakeExplosion/Fire/Fountain/Smoke`, `llPointAt`, `llStopPointAt`, `llRefreshPrimURL`, `llSetPrimURL`, `llSound`, `llGodLikeRezObject`.
- **Animesh, never implemented:** `llStartObjectAnimation`, `llStopObjectAnimation`, `llGetObjectAnimationNames`.
- **Halcyon-era no-ops:** `llCollisionFilter`, `llCollisionSprite`, `llCloseFloater`, `llSetLinkSitFlags`, `llGetLinkSitFlags`.
- **Per-script accounting fakes:** `llGetFreeMemory` (65536), `llGetMemoryLimit` (131072), `llGetSPMaxMemory` (16384) — these are constants; SL returns real numbers.
- **Physics queries returning zero:** `llGetAccel`, `llGetOmega`, `llGetTorque`, `llDetectedDamage`.
- **Per-agent EEP and combat 2.0** waiting on viewer protocol: `llSetAgentEnvironment`, `llReplaceAgentEnvironment`, `llDetectedDamage`.

### 5.2 Missing from `ISystemAPI.cs` (sampled — not exhaustive)
See U5 above. At least 17 SL canonical `ll*` functions are not declared. The diff was not exhaustive because the SL wiki's prefix index is paginated and only a subset of letters was enumerated. To produce a complete list, fetch every `Special:PrefixIndex?prefix=llX` page (one per letter A–Z), strip `/lang` suffixes and `Test`/`(...)` variants, and diff against `_audit/phlox_ll_functions.txt`.

### 5.3 Extras (intentional)
117 non-SL `ll*`/`iw*`/`os*`/`bot*` functions are declared by design:
- `osTeleportAgent`, `osGetAvatarList` (OSSL ports)
- 62 `iw*` (Halcyon/InWorldz extensions: `iwIntRand`, `iwClampFloat`, `iwGiveLinkInventory`, `iwSearchInventory`, etc.)
- 53 `bot*` (NPC system: `botCreateBot`, `botFollowAvatar`, `botSensor`, etc.)

These should not be treated as drift — they are intentional Phlox/InWorldz extensions.

---

## 6. Honest closing assessment

**Where Phlox is on solid ground.**
- Operator surface, expression grammar, and statement set match SL almost exactly. The 31 valid operators all parse; the four most-tested invalid C-isms (`?:`, `|=`, `&=`, `^=`) are correctly rejected.
- All 7 SL primitive types are recognized — except the `quaternion` alias.
- Pre/post increment, integer div/mod sign, math-error-on-zero, and non-short-circuit `&&`/`||` semantics are all correct **by static read of the VM opcode implementations** (citations in §4). They have not been confirmed by execution in this audit.
- 38 of the 38 mainstream SL events compile with correct signatures.
- 458 of 491 declared `ll*` have substantive bodies. The 33 stubs are dominated by SL-deprecated functions (intentional) and Animesh / Combat 2.0 / per-agent EEP awaiting host support.

**Where Phlox drifts.**
- Two over-conformant operator bugs: `<<=` and `>>=` are accepted but not part of SL. The fix is a one-line grammar edit.
- Five SL events are not whitelisted — most notably `path_update`, which is a real gap because the rest of the pathfinding API IS implemented.
- `quaternion` keyword is not an accepted alias for `rotation`.
- `rotation + rotation` and `rotation - rotation` are accepted with componentwise semantics; SL does not define either.
- `(list)scalar` direct cast is accepted; SL requires `[scalar]`.
- An unknown number of SL `ll*` functions (sampled at 17) are not declared in `ISystemAPI.cs` — exhaustive enumeration would require fetching every prefix page on the SL wiki and was not completed in this pass.

**What this audit did NOT measure.**
- Runtime behaviour was not exercised in-VM. The runtime spot-check predictions in §4 are static reads, not observations. Drop `_audit/runtime_scripts/*.lsl` in-world and record actual results before treating them as definitive.
- The `llRemoveInventory(llGetScriptName())` self-removal sequence (B6) is **the one open debugging item** identified in the brief and remains open here — script provided, run-and-observe required.
- Function-level behavioural conformance (e.g. does `llParseStringKeepNulls` exactly match SL on every edge case?) was sampled (B8) but not exhaustively tested.

**Single-sentence verdict.** Phlox is parser- and grammar-conformant to SL at ~91% of the compile-time corpus, with the specific drifts above; runtime semantics for the math-and-control primitives match SL by static read of the VM but have not been executed in this audit, and one open behavioural item (`llRemoveInventory` self-removal) remains untested pending in-world drop.
