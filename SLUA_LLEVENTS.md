# SLua Tier-2: LLEvents:on / DetectedEvent — Design + Proof (last SL-parity feature)

**Branch:** `slua-tier2-tables`. **Status:** built + proven offline; **NOT deployed**. Additive; LSL + all prior SLua unaffected (regression-tested). Closures unblocked this (handlers are function values).

---

## 1. SL-shape verification (verified vs inferred)
Sources: SL wiki (SLua_Alpha, Luau_Examples), SLua release notes (Nov 6 2025), `secondlife/slua` repo.
- **Verified:** registration `LLEvents:on("event", function(...) end)` (method syntax `:`); a detection-event handler takes one arg = an **array of DetectedEvent** (`detected[1]:getKey()`, 1-based, method syntax); non-detection handlers take LSL scalar params (`listen(channel,name,id,msg)`, `timer()`, ...); multiple handlers per event; DetectedEvent methods **confirmed `:getKey()`, `:getName()`**.
- **Verified divergence:** Nov 2025 release notes say *"global function event handlers ... no longer do anything"* — **current SL deprecated Form-1.** The prompt directs Legion to **keep Form-1**, so Legion intentionally diverges (both forms coexist).
- **Inferred (LLEvents/DetectedEvent are simulator-side / not in public source):** the full DetectedEvent method set beyond getKey/getName — implemented by mapping to the existing `llDetected*` family; handler-removal API (deferred). Flagged.

## 2. Design decisions
1. **Method-call sugar `obj:method(args)`** (prerequisite — both `LLEvents:on` and `det:getKey()` use `:`; deferred from closures, added now). Desugars per receiver via a `methcall` opcode.
2. **`LLEvents:on` storage = a hidden global table** (`__llevents`, reserved as one extra global slot; event-name → list of handler closures). Reasoning: a table of closures in a Globals slot **serializes for free** through the existing path — registered handlers survive serialize→resume with **zero new serialization**. Multiple handlers per event = a list; `on` appends.
3. **Dispatch via generated dispatcher `.evt` blocks.** A pre-scan collects events registered via `LLEvents:on("literal", …)`; for each (without a Form-1 handler) the front-end synthesizes a `.evt` that, on the existing `PostedEvent`/`DoEvent` fire, runs `firellevents` — which builds the DetectedEvent array (detection events) or passes the scalar event args, and invokes each registered handler via the closure machinery (`InvokeClosureSync`). **No change to existing event dispatch.**
4. **DetectedEvent = a lightweight native object holding a 0-based index** (`LuaDetected`); `:getKey()`→`llDetectedKey(i)` etc. via `methcall` → the existing `llDetected*` syscalls (read while the detection context is live; handlers run synchronously during dispatch). No recompute; transient (no serialization).
5. **Form-1 / Form-2 coexistence (Legion keeps both):** per event, a Form-1 global function wins (its `.evt`); otherwise the `LLEvents:on` dispatcher fires. Different events mix freely.

**New opcodes (3):** `regevent` (append handler to registry), `methcall methodConst,argc` (DetectedEvent native dispatch; LSLTable method-as-field via `InvokeClosureSync`), `firellevents eventConst,argc` (invoke registered handlers, build DetectedEvent array for detection events).

**DetectedEvent methods (Common set, backed by `llDetected*`):** getKey, getName, getPos, getOwner, getGroup, getType, getVel, getRot, getLinkNumber (names beyond getKey/getName are inferred-from-LSL). Others → clear error.

**Flagged/deferred:** dynamic (non-literal) event-name registration; handler removal/replace; defining both Form-1 and Form-2 for the *same* event (Form-1 wins); DetectedEvent stored across a yield (transient); method-sugar on non-object values.

## 3. What was built
**New file** (tracked csproj): `Types/LuaDetected.cs`. **Modified:** `OpCodes.cs` (3 opcodes), `Interpreter(.Actions).cs` (dispatch + `Op_RegEvent`/`Op_MethCall`/`Op_FireLLEvents` + the DetectedEvent→llDetected* map, reusing `InvokeClosureSync`), `SLuaCompiler.cs` (method-call parsing/codegen, `LLEvents:on` registration, `__llevents` registry reservation+init, dispatcher generation, `CollectLLEvents` pre-scan, capture/name walkers extended for `MethodCall`). No new serialization (registry rides Globals).

## 4. Proof (offline; PASS — 3/3 outputs)
Script: `LLEvents:on("touch_start", function(detected) ... detected[1]:getName()/getKey() ... end)` + a Form-1 `function timer()`; in `/_sluaproof/`.
- **On rez** (`state_entry`): runs the registration (no output).
- **On touch:** `touched by Avatar0 (key-0)` — the **LLEvents:on handler fired**, reading **DetectedEvent** `getName()`/`getKey()` (→ `llDetectedName`/`llDetectedKey`). ✓
- **On timer:** `form-1 timer fired` — **Form-1 still works** alongside Form-2. ✓
- **Serialize (124 bytes) the script with the handler registered → restore → touch again:** `touched by Avatar0 (key-0)` — **the registered handler closure survived serialize→resume** (it lives in the `__llevents` global table). ✓
- Generated assembly verified: registry init, `mkclosure … regevent` in state_entry, a `firellevents` dispatcher for touch_start, `methcall "getName"/"getKey"` in the handler.

## 5. Regression (all prior work unaffected)
`LSL Compile()` **OK** · `Tier-1` **OK** · `Closures` (counter-maker) **OK** · `Patterns` (match captures) **OK**. New opcodes appended; dispatch hooks ride the existing event system; `MethodCall` added to the capture/name walkers so closure capture inside handler args is correct.

## 6. Next-piece signal
With `LLEvents:on`/DetectedEvent done, **the last real SL-parity event feature is in.** Remaining is small/polish:
- **Metatables** — seam ready in `LSLTable.Get/Set`; `__index`/`__newindex`/`__tostring` (~1 session).
- Small items: wider DetectedEvent methods (touch face/ST/UV), table-repl gsub, varargs, handler removal, dynamic event-name registration, optional cross-closure cell-sharing serialization.
- **Running estimate: ~1–2 sessions to "broadly SL-compatible".** No hard VM work remains. Natural **deploy-batch point** with closures+patterns (already committed-undeployed) — LLEvents could deploy alongside.

## 7. Verdict
**YES — the `LLEvents:on`/DetectedEvent model works end to end, including serialization.** Handlers register as function values, fire on the existing event dispatch with DetectedEvent data available via method calls backed by `llDetected*`, Form-1 global handlers continue to work, and a registered handler survives serialize→deserialize→resume. No existing behavior changed. Committed on `slua-tier2-tables`; not deployed. Legion intentionally keeps Form-1 (diverging from SL's deprecation), per the prompt.
