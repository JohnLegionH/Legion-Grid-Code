# Phlox LSL Script Engine Audit — 2026-05-24

Auditor: Claude Sonnet 4.6  
Scope: Legion Grid Phlox engine vs SL LSL spec and Halcyon reference  
Rules: Read-only. No fixes applied. All findings cite file:line.  
Note: The local Halcyon reference at `/mnt/desktop/d-drive/halcyon-reference/InWorldz/InWorldz.Phlox.Engine/LSLSystemAPI.cs` has been overwritten with Legion Grid code (same file headers, same line numbers). Direct local Halcyon comparison was not possible. GitHub fetch was not performed. Behavioral drift findings are based on SL spec and internal code inspection only.

---

## Summary

| Severity | Count |
|----------|-------|
| Critical | 5     |
| Medium   | 9     |
| Low      | 4     |
| Coverage Gaps (stubs) | 4+ explicit, many implicit |

---

## Critical Issues

### C-1: For-loop init/condition silently discarded — all `for(init; cond; loop)` loops broken

**File:** `OpenSim/Addons/Phlox/InWorldz.Phlox/Compiler/GenVisitor.cs:270–280`  
**Also:** `InWorldz.Phlox/Compiler/LSLBaseVisitor.cs:138`, `grammar/generated/LSLParser.cs:838–857`

**Description:**  
`VisitForStmt` calls `Visit(context.init)` and `Visit(context.cond)`, but `context.init` and `context.cond` are both typed as `ExprStatementContext` (grammar rule: `expression SEMI`). `Visit(ExprStatementContext)` resolves to `LSLBaseVisitor.VisitExprStatement` (line 138), which calls `VisitChildren(context)`. ANTLR4's default `AggregateResult(aggregate, nextResult)` returns `nextResult` — the last child visited. The last child in `exprStatement` is always the SEMI terminal. `VisitTerminal` returns `DefaultResult`, which for `AbstractParseTreeVisitor<string>` is `null`. The result for any non-empty for-loop init or condition is therefore `null`.

In `ByteCodeEmitter.ForLoop` (lines 437–462), emission of init and cond is gated on `!string.IsNullOrEmpty(...)`. Both null results pass this test as "empty", so init code and condition branch are never emitted.

Effect: `for(i = 0; i < 10; i++)` compiles to an infinite loop with no initialization. The loop variable is never set, the exit condition never tested. Only `for(;;)` works correctly.

**Test coverage gap:**  
`CompilerTests/CompilerTests/ControlPaths.cs` — only for-loop test is `for(;;)` at line 139. No test exercises init or condition expressions.

**SL spec:** https://wiki.secondlife.com/wiki/LSL_Flow_Control#for  
**Impact:** Any LSL script using a standard `for` loop will malfunction silently. This is the most common loop pattern in LSL.

---

### C-2: collision_start / collision / collision_end / land_collision_start / land_collision / land_collision_end never fire

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxEngine.cs:101–135`

**Description:**  
`PhloxEngine.RegionLoaded` (lines 101–115) and `RemoveRegion` (lines 122–135) show the complete set of EventManager subscriptions. The following collision delegates are **not subscribed**:

- `OnScriptColliding` → `collision`
- `OnScriptCollidingStart` → `collision_start`
- `OnScriptCollidingEnd` → `collision_end`
- `OnScriptLandCollidingStart` → `land_collision_start`
- `OnScriptLandColliding` → `land_collision`
- `OnScriptLandCollidingEnd` → `land_collision_end`

`llCollisionFilter` in `LSLSystemAPI.cs:6136` is an explicit no-op:
```
// NotImplemented in Halcyon
```

No code path exists to deliver these events to any script.

**SL spec:** https://wiki.secondlife.com/wiki/collision_start  
**Impact:** All physics collision scripting (doors, damage systems, sensors by collision, vehicles) is non-functional.

---

### C-3: attach / object_rez (from outside) / money / moving_start / moving_end / at_target / not_at_target / at_rot_target / not_at_rot_target never fire

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxEngine.cs:101–135`

**Description:**  
The following EventManager delegates are absent from PhloxEngine subscriptions:

- `OnObjectAttach` → `attach`
- `OnMoneyTransfer` → `money`
- `OnScriptMovingStartEvent` → `moving_start`
- `OnScriptMovingEndEvent` → `moving_end`
- `OnScriptAtTargetEvent` → `at_target`
- `OnScriptNotAtTargetEvent` → `not_at_target`
- `OnScriptAtRotTargetEvent` → `at_rot_target`
- `OnScriptNotAtRotTargetEvent` → `not_at_rot_target`

`llTarget` (LSLSystemAPI.cs:6017) and `llRotTarget` (LSLSystemAPI.cs:6035) call `RegisterTargetWaypoint` / `RegisterRotTargetWaypoint` successfully, but the resulting events have no subscriber to deliver them to Phlox scripts.

`object_rez` fires correctly for objects rezzed **by** the script (LSLSystemAPI.cs:3283, via `PostObjectEvent`), but the attach event path is completely absent.

**SL spec:** https://wiki.secondlife.com/wiki/attach, https://wiki.secondlife.com/wiki/at_target  
**Impact:** Attachment HUDs cannot detect attach/detach. Waypoint navigation (llTarget/llRotTarget) is non-functional. Moving object detection (moving_start/end) is absent. Money event for LSL commerce hooks is absent.

---

### C-4: OnSceneObjectPartUpdated fires `changed(CHANGED_SHAPE)` for all full-update scene events

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxEngine.cs:363`

**Description:**  
`PhloxEngine` subscribes to both `OnSceneObjectPartUpdated` and `OnScriptChangedEvent`. The `OnSceneObjectPartUpdated` handler at line 363 fires `changed(CHANGED_SHAPE)` for **every** full-update (`full=true`) scene object part update. This fires on a much broader set of conditions than `CHANGED_SHAPE` should — any physics update, position update, or state change that triggers a full part sync will incorrectly deliver `CHANGED_SHAPE` to scripts.

This creates two problems:
1. `changed(CHANGED_SHAPE)` fires when the shape has not actually changed
2. For true shape changes, both `OnSceneObjectPartUpdated` and `OnScriptChangedEvent` fire, delivering a duplicate `changed` event

**SL spec:** https://wiki.secondlife.com/wiki/changed  
**Impact:** Scripts using `changed(CHANGED_SHAPE)` fire spuriously and on every physical movement. Can cause significant script load and logic errors.

---

### C-5: HasScript and GetScriptErrors are permanent silent stubs

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxEngine.cs:498–499`

**Description:**  
```csharp
public System.Collections.ArrayList GetScriptErrors(UUID itemID)
    { return new System.Collections.ArrayList(); }
public bool HasScript(UUID itemID, out bool running)
    { running = false; return false; }
```

`HasScript` always returns `false` with `running = false`. `GetScriptErrors` always returns an empty list. These are not just stubs — they actively lie. Any caller relying on `HasScript` to determine whether a Phlox script exists will get a false negative. `GetScriptErrors` was specifically fixed in a recent commit (the YEngine GetScriptErrors blocking issue), but Phlox never implemented it.

The recent commit message "Fix script save 30-second timeout: YEngine.GetScriptErrors blocks indefinitely for Phlox scripts" (SHA 3a1d2aed9f) confirms awareness of this interface, but the Phlox side was not implemented.

**Impact:** Script error reporting through any caller of `IScriptEngine.GetScriptErrors` returns no errors for Phlox scripts. Region tools and viewers that query script running state via `HasScript` will always see Phlox scripts as absent/not-running.

---

## Medium Issues

### M-1: MapEventFlag missing 5 event types — always returns 0 for new event flags

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:10397–10437`

**Description:**  
`MapEventFlag` maps `SupportedEventList.Events` enum values to `ScriptEvents` flag bits. The `default` case returns `0UL`. The following events defined in `SupportedEventList.cs` have no case in `MapEventFlag`:

- `TRANSACTION_RESULT`
- `LINKSET_DATA`
- `BOT_UPDATE`
- `EXPERIENCE_PERMISSIONS`
- `EXPERIENCE_PERMISSIONS_DENIED`

When `SetScriptEventFlags` calls `MapEventFlag` for these events, it silently returns 0, so the physics/scene engine is never told the script handles them. Even if the event were delivered, the engine would not route it correctly.

**Impact:** Scripts using `linkset_data`, `transaction_result`, `experience_permissions`, or `bot_update` events will not receive them even if the delivery path exists.

---

### M-2: llFrand creates a new Random instance on every call — statistical bias

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:130`

**Description:**  
```csharp
public LSL_Float llFrand(double mag) {
    return new Random().NextDouble() * mag;
}
```

`new Random()` seeds from the system clock. On rapid successive calls (within the same tick), the clock resolution causes identical seeds, producing identical return values. A loop calling `llFrand` multiple times in one script pass will return the same value repeatedly.

**SL spec:** `llFrand` is specified to return a pseudo-random float in `[0, mag)` with statistical independence between calls.  
**Fix approach (for reference only):** Use a shared static `Random` instance with thread-safe access or `System.Security.Cryptography.RandomNumberGenerator`.

---

### M-3: llMinEventDelay is a no-op

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:1100`

**Description:**  
`llMinEventDelay` stores no value and enforces no delay between events. Scripts using this to rate-limit sensor or timer events will receive events at full speed.

**SL spec:** https://wiki.secondlife.com/wiki/llMinEventDelay — sets minimum interval between queued events of the same type.

---

### M-4: llCollisionFilter is a no-op

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:6136`

**Description:**  
```csharp
// NotImplemented in Halcyon
```

No collision filtering logic exists. Combined with C-2 (collisions never fire at all), this means even if collisions were wired up, the filter would not apply.

**SL spec:** https://wiki.secondlife.com/wiki/llCollisionFilter

---

### M-5: llTransferLindenDollars immediately returns LINDENDOLLAR_INSUFFICIENTFUNDS

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:8539`

**Description:**  
`llTransferLindenDollars` fires a `LINDENDOLLAR_INSUFFICIENTFUNDS` dataserver event immediately without any economy integration attempt. Scripts treating this as a real transfer function will always fail, but scripts testing the return code path will work.

This is a deliberate stub (no grid economy), but it could mislead script authors testing on this grid before deploying to SL.

---

### M-6: llGiveMoney returns 0 always — no economy integration

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:8517`

**Description:**  
Correctly checks PERMISSION_DEBIT (0x02) before attempting anything, but always returns 0 (failure) with no economy module call. No error is reported to the script — it silently fails.

**SL spec:** https://wiki.secondlife.com/wiki/llGiveMoney

---

### M-7: llCollisionSprite is a no-op

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs` (no-op return)

**Description:**  
`llCollisionSprite` accepts parameters and returns without action. No visual effect is applied on collision. This is a legacy SL function (sprites are deprecated in SL too), but the call should at minimum not silently succeed.

---

### M-8: OnScriptReset not re-registering listens/timers

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/PhloxEngine.cs` (OnScriptReset handler)  
**Related:** `PhloxExecutionScheduler.cs:108–228`

**Description:**  
`RuntimeState.Reset()` clears `ActiveListens`, `TimerInterval`, and `TimerLastScheduledOn`. State restores (via `FinishedLoading`) re-register timers and listens from persisted `MiscAttributes`. However, `OnScriptReset` triggers a script reset without going through the full `FinishedLoading` path — the re-registration logic for sensors and listens may not be invoked. This requires follow-up verification against the reset code path in PhloxExecutionScheduler.

---

### M-9: ScriptSleep uses EnvironmentTickCount (30-bit masked) for NextWakeup — mismatch with Clock.GetLongTickCount

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:66–71`

**Description:**  
```csharp
public void ScriptSleep(int ms) {
    state.NextWakeup = (ulong)Util.EnvironmentTickCount() + (ulong)ms;
}
```

`EnvironmentTickCount()` returns a masked 30-bit value (wraps at ~12.4 days). `Clock.GetLongTickCount()` (used in `TotalRuntime` and the scheduler's sleep wake check) is a 64-bit monotonic clock. If the scheduler compares `NextWakeup` (30-bit origin) against `GetLongTickCount()` (64-bit value), the comparison will always be `NextWakeup < current`, causing the script to wake immediately rather than sleeping, after the first clock wrap.

This requires verification of the scheduler's sleep comparison expression, but the mismatch in tick sources is a latent correctness issue.

---

## Low Issues

### L-1: STATUS_SANDBOX is a no-op

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs`

**Description:**  
`llSetStatus(STATUS_SANDBOX, TRUE)` restricts a script to rezzing objects only within 10m. No sandbox enforcement is implemented. Scripts relying on this for safety containment will not be sandboxed.

**SL spec:** https://wiki.secondlife.com/wiki/llSetStatus

---

### L-2: llTargetedEmail is an explicit stub

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:12197`

**Description:**  
`Stub()` is called explicitly. This is a non-standard extension function (not in base SL LSL) so the impact is limited to scripts using this specific iw* or os* extension.

---

### L-3: llDetectedDamage is an explicit stub

**File:** `OpenSim/Addons/Phlox/Phlox.ScriptEngine/LSLSystemAPI.cs:12248`

**Description:**  
`Stub()` is called explicitly. `llDetectedDamage` is used in combat scripts. Limited to combat-enabled regions.

**SL spec:** https://wiki.secondlife.com/wiki/llDetectedDamage

---

### L-4: No explicit stack underflow guard in Interpreter

**File:** `OpenSim/Addons/Phlox/InWorldz.Phlox/VM/Interpreter.cs`

**Description:**  
The operand stack is a C# `Stack<object>`. `Stack<T>.Pop()` throws `InvalidOperationException` on underflow, which is not the same as a clean LSL script error. A malformed bytecode sequence (from a compiler bug, not user code) would surface as an unhandled exception rather than a controlled script error. The `default: throw new VMException("Unhandled opcode: " + opcode)` for unknown opcodes is handled correctly, but stack underflow from compiler-generated code is not.

---

## Coverage Gaps (ISystemAPI Stubs)

**Interface:** `OpenSim/Addons/Phlox/InWorldz.Phlox/Glue/ISystemAPI.cs` (736 lines, ~230+ methods)

Explicit `Stub()` calls found in `LSLSystemAPI.cs`:

| Line | Function | Notes |
|------|----------|-------|
| 2503 | `llListen` (partial) | Fires when no ListenManager available |
| 12197 | `llTargetedEmail` | Extension function |
| 12248 | `llDetectedDamage` | Combat function |
| ~8517 | `llGiveMoney` | Returns 0, no economy |

**Functions returning silent defaults** (not `Stub()` but effectively no-ops):
- `llMinEventDelay` — stores nothing, enforces nothing
- `llCollisionFilter` — explicitly commented as not implemented
- `llCollisionSprite` — no-op return
- `STATUS_SANDBOX` in `llSetStatus` — silently accepted, not enforced
- `llTransferLindenDollars` — always fires insufficient funds event
- `MapEventFlag` for 5 event types — returns 0 silently

**ISystemAPI sections not audited for completeness** (scope limit):  
Phase 34 (bot persistence, lines 731–735), EEP/environment functions, PBR materials functions, experience KVP functions, RSA functions. These were defined in the interface but LSLSystemAPI.cs implementation was not line-by-line checked for each.

---

## Event Wiring Matrix

All events from `SupportedEventList.cs`. "Wired" = PhloxEngine.cs subscribes to the relevant EventManager delegate and delivers to scripts.

| # | Event Name | TableIndex | Wired in PhloxEngine? | Source Delegate | Notes |
|---|-----------|-----------|----------------------|----------------|-------|
| 1 | at_rot_target | 1 | **NO** | `OnScriptAtRotTargetEvent` | llRotTarget registers but event never delivered |
| 2 | at_target | 2 | **NO** | `OnScriptAtTargetEvent` | llTarget registers but event never delivered |
| 3 | attach | 3 | **NO** | `OnObjectAttach` | Not subscribed |
| 4 | changed | 4 | Partial | `OnScriptChangedEvent` + `OnSceneObjectPartUpdated` | Duplicate CHANGED_SHAPE delivery; see C-4 |
| 5 | collision | 5 | **NO** | `OnScriptColliding` | Not subscribed; see C-2 |
| 6 | collision_end | 6 | **NO** | `OnScriptCollidingEnd` | Not subscribed; see C-2 |
| 7 | collision_start | 7 | **NO** | `OnScriptCollidingStart` | Not subscribed; see C-2 |
| 8 | control | 8 | YES | `OnScriptControlEvent` | Subscribed at line 109 |
| 9 | dataserver | 9 | YES | Posted directly by HTTP/sensor subsystems | Via PostObjectEvent |
| 10 | email | 10 | YES | Email module callback | Via PostObjectEvent |
| 11 | http_request | 11 | YES | HTTP server module | Via PostObjectEvent |
| 12 | http_response | 12 | YES | IHttpRequestModule callback | Via PostObjectEvent |
| 13 | land_collision | 13 | **NO** | `OnScriptLandColliding` | Not subscribed; see C-2 |
| 14 | land_collision_end | 14 | **NO** | `OnScriptLandCollidingEnd` | Not subscribed; see C-2 |
| 15 | land_collision_start | 15 | **NO** | `OnScriptLandCollidingStart` | Not subscribed; see C-2 |
| 16 | link_message | 16 | YES | Posted by llMessageLinked via PostObjectEvent | |
| 17 | listen | 17 | YES | Chat/listen subsystem | Via PostObjectEvent |
| 18 | money | 18 | **NO** | `OnMoneyTransfer` | Not subscribed |
| 19 | moving_end | 19 | **NO** | `OnScriptMovingEndEvent` | Not subscribed |
| 20 | moving_start | 20 | **NO** | `OnScriptMovingStartEvent` | Not subscribed |
| 21 | no_sensor | 21 | YES | Sensor plugin callback | Via AsyncCommandManager |
| 22 | not_at_rot_target | 22 | **NO** | `OnScriptNotAtRotTargetEvent` | Not subscribed |
| 23 | not_at_target | 23 | **NO** | `OnScriptNotAtTargetEvent` | Not subscribed |
| 24 | object_rez | 24 | YES (partial) | Posted by llRezObject (LSLSystemAPI.cs:3283) | Only fires for objects this script rezzes; no inbound path |
| 25 | on_rez | 25 | YES | `OnRezScript` path → FinishedLoading PostOnRez | Delivered at script load |
| 26 | path_update | 26 | Unverified | Pathfinding subsystem | Not audited in depth |
| 27 | remote_data | 27 | YES | XMLRPC module callback | Via PostObjectEvent |
| 28 | run_time_permissions | 28 | YES | Posted by llRequestPermissions accept path | Via PostObjectEvent |
| 29 | sensor | 29 | YES | Sensor plugin callback | Via AsyncCommandManager |
| 30 | state_entry | 30 | YES | Fired in FinishedLoading on fresh start | ✓ |
| 31 | state_exit | 31 | YES | Fired on state change via statechg opcode | ✓ |
| 32 | timer | 32 | YES | PhloxExecutionScheduler timer tracking | ✓ |
| 33 | touch | 33 | YES | `OnObjectGrabbing` (line 109) | touch (continuous) |
| 34 | touch_end | 34 | YES | `OnObjectDeGrab` (line 109) | touch_end |
| 35 | touch_start | 35 | YES | `OnObjectGrab` (line 109) | touch_start |
| 36 | transaction_result | 36 | Unverified | Economy module | MapEventFlag returns 0 for this event |
| 37 | bot_update | 37 | Unverified | Bot subsystem | MapEventFlag returns 0 for this event |
| 38 | linkset_data | 38 | Unverified | Posted by llLinksetDataWrite etc. | MapEventFlag returns 0 for this event |
| — | experience_permissions | — | Unverified | Experience subsystem | MapEventFlag returns 0 for this event |
| — | experience_permissions_denied | — | Unverified | Experience subsystem | MapEventFlag returns 0 for this event |

**Summary of wiring:**
- Confirmed wired (YES): 14 events
- Confirmed NOT wired (NO): 13 events (collision family: 6, target family: 4, attach: 1, money: 1, moving: 2)
- Partial/incorrect: 1 (changed — duplicate delivery)
- Unverified: 5 (transaction_result, bot_update, linkset_data, experience_permissions×2)

---

## Compiler Type System Status

All operator result type matrices in `SymbolTable.cs` have been verified:

| Matrix | Status | Notes |
|--------|--------|-------|
| additionResultType | ✓ Correct | key+string and string+key → STRING (previously fixed) |
| subtractionResultType | ✓ Correct | Standard numeric types |
| multiplicationResultType | ✓ Correct | vector×vector → FLOAT (dot product) |
| divisionResultType | ✓ Correct | vector÷rotation → VECTOR |
| modResultType | ✓ Correct | vector%vector → VECTOR (cross product) |
| shiftResultType | ✓ Correct | integer only |
| promoteFromTo | ✓ Correct | int→float promotion present |
| castFromTo | Not fully audited | Spot checks passed |

---

## VM / Runtime Status

| Area | Status | Notes |
|------|--------|-------|
| State transition (statechg opcode) | ✓ Correct | `StateChangePrep()` clears event queue per SL spec |
| Event queue max size | ✓ | `MAX_EVENT_QUEUE_SIZE = 64` |
| Overflow events | ✓ | ON_REZ, STATE_ENTRY, STATE_EXIT, TIMER bypass queue limit |
| Sleep semantics | ⚠ Suspect | `EnvironmentTickCount` (30-bit) vs `GetLongTickCount` (64-bit) mismatch; see M-9 |
| Stack underflow | ⚠ Unguarded | C# `InvalidOperationException` on underflow, not clean script error |
| Unknown opcode | ✓ | `throw new VMException("Unhandled opcode: " + opcode)` |
| Script reset | ✓ | `RuntimeState.Reset()` clears all state including listens, permissions, timers |
| State persistence | ✓ | SQLite + protobuf-net; 2500ms flush, 200 dirty execution cap |
| Timer restore on reload | ✓ | `FinishedLoading` re-registers timers from MiscAttributes |
| Listen restore on reload | ✓ | `FinishedLoading` re-registers listens from ActiveListens |

---

## Recent Fix Consistency (StoreScriptErrors / GetScriptErrors)

**Commit:** 3a1d2aed9f "Fix script save 30-second timeout: YEngine.GetScriptErrors blocks indefinitely for Phlox scripts"

The fix addressed YEngine calling `GetScriptErrors` on a Phlox script and blocking. The Phlox-side implementation (`PhloxEngine.GetScriptErrors`, line 498) remains `return new System.Collections.ArrayList()` — always empty. The fix was defensive on the caller side only. Phlox does not implement `StoreScriptErrors` nor populate the errors collection. This is documented as C-5.

---

*End of audit report. No changes were made to any source file.*
