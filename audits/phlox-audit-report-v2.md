# Phlox Audit v2 — 2026-05-24

## Reference verification

- **Halcyon reference path:** `/mnt/desktop/d-drive/halcyon-reference-fresh/InWorldz/InWorldz.Phlox.Engine/`
- **Halcyon LSLSystemAPI.cs first line of copyright:** `Copyright (c) InWorldz Halcyon Developers`
- **Halcyon LSLSystemAPI.cs line count:** 18,763
- **Halcyon namespace:** `InWorldz.Phlox.Engine` (confirmed via `grep -n "^namespace"`)
- **Status: VERIFIED REAL**

Our LSLSystemAPI.cs: `Phlox.ScriptEngine`, 12,697 lines (ours is 6,066 lines shorter — the Halcyon base is substantially more complete in physics, vehicle, media, and legacy IW functions).

---

## Summary

- **5 critical issues** — all have minimum reproductions
- **8 medium issues**
- **4 low issues**
- **~14 ISystemAPI methods stubbed or silently defaulting** (4 explicit `Stub()`, ~10 silent)

---

## Critical

### C-1: `OnScriptInjected`, `OnScriptReset`, `OnStateChange` are empty stubs — all MiscAttributes lost on reload

**Where:** `LSLSystemAPI.cs:108,109,116`

```csharp
public void OnScriptReset() { }          // line 108
public void OnStateChange() { }           // line 109
public void OnScriptInjected(bool fromCrossing) { }  // line 116
```

**What:** Halcyon's `OnScriptInjected` (Halcyon LSLSystemAPI.cs:283–341) iterates `RuntimeState.MiscAttributes` on state restore and calls the appropriate `ll*` functions to reinstate:
- `SensorRepeat` → calls `llSensorRepeat(name, id, type, range, arc, rate)`
- `VolumeDetect` → calls `llVolumeDetect(int)`
- `Control` → re-registers avatar controls via `llTakeControls`
- `SilentEstateManagement` → restores permission mask

Our `OnScriptInjected` is an empty method, so none of these are re-registered when a script is restored from saved state (region restart, script reload, crossing).

**Second bug in the same area:** `LSLSystemAPI.cs:2486–2491` — our `llSensorRepeat` calls the sensor plugin but never writes to `MiscAttributes`:
```csharp
// Our code — MiscAttributes never updated:
public void llSensorRepeat(string name, string id, int type, float range, float arc, float rate)
{
    m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SetSenseRepeatEvent(
        m_localID, m_itemID, name, keyID, type, range, arc, rate, m_host);
}
```
Halcyon LSLSystemAPI.cs:1154–1163:
```csharp
// Halcyon — writes MiscAttributes for persistence:
_thisScript.ScriptState.MiscAttributes[(int)VM.RuntimeState.MiscAttr.SensorRepeat] =
    new object[6] { name, id, type, range, arc, rate };
```
Same pattern applies to `llVolumeDetect` (our `LSLSystemAPI.cs:6148–6151` — no MiscAttributes write).

**Halcyon reference:** `InWorldz.Phlox.Engine/LSLSystemAPI.cs:283–341` (OnScriptInjected), `1154–1163` (llSensorRepeat MiscAttributes write)

**SL spec:** https://wiki.secondlife.com/wiki/llSensorRepeat — sensor repeat must survive region restarts in SL.

**Minimum reproduction:**
```lsl
default {
    state_entry() {
        llSensorRepeat("", "", SCRIPTED, 30, PI, 1.0);
        llSay(0, "sensor_repeat set");
    }
    sensor(integer num) { llSay(0, "sensor fired"); }
}
```
1. Rez the object. Confirm "sensor fired" fires every second.
2. Restart the region.
3. "sensor fired" will not fire — sensor repeat is gone.

**Impact:** Any script that uses `llSensorRepeat`, `llVolumeDetect`, or `llTakeControls` loses its active state after every region restart or script reload. HUDs that take avatar controls lose them on every region crossing.

---

### C-2: Six collision events never fire — no EventManager subscriptions

**Where:** `PhloxEngine.cs:101–115` (subscription block), `PhloxEngine.cs:119–141` (removal block)

**What:** The following `EventManager` delegates exist in `OpenSim/Region/Framework/Scenes/EventManager.cs` at lines 605, 619, 632, 645, 658, 671 but are never subscribed to in our PhloxEngine:

| LSL event | EventManager delegate | EventManager line |
|-----------|----------------------|-------------------|
| `collision_start` | `OnScriptColliderStart` | 605 |
| `collision` | `OnScriptColliding` | 619 |
| `collision_end` | `OnScriptCollidingEnd` | 632 |
| `land_collision_start` | `OnScriptLandColliderStart` | 645 |
| `land_collision` | `OnScriptLandColliding` | 658 |
| `land_collision_end` | `OnScriptLandColliderEnd` | 671 |

`llCollisionFilter` (`LSLSystemAPI.cs:6136`) is also an explicit no-op: `{ /* NotImplemented in Halcyon */ }`.

**Halcyon reference:** Halcyon's `EngineInterface.cs:179–191` shows the subscription block — Halcyon doesn't subscribe to collision events either in `EngineInterface.cs`. However, Halcyon routes collision events through a separate `EventRouter` class (`EngineInterface.cs:177: _eventRouter = new EventRouter(this)`). We have no equivalent. The Halcyon `EventRouter.cs` file is the collision subscription mechanism. We ported `EngineInterface` without `EventRouter`.

**SL spec:** https://wiki.secondlife.com/wiki/collision_start

**Minimum reproduction:**
```lsl
default {
    state_entry() { llSetStatus(STATUS_PHYSICS, TRUE); }
    collision_start(integer num) { llSay(0, "collision_start fired"); }
}
```
Drop an object onto the scripted physical prim. "collision_start fired" will not appear.

**Impact:** All physical scripting (damage systems, bump detectors, push triggers, vehicle collision sounds) is non-functional.

---

### C-3: Eight more events never fire — no EventManager subscriptions

**Where:** `PhloxEngine.cs:101–115`

**What:** The following delegates exist in `EventManager.cs` but are never subscribed:

| LSL event | EventManager delegate | EventManager line |
|-----------|----------------------|-------------------|
| `attach` | `OnAttach` | 885 |
| `money` | `OnMoneyTransfer` | 1041 |
| `moving_start` | `OnScriptMovingStartEvent` | 531 |
| `moving_end` | `OnScriptMovingEndEvent` | 537 |
| `at_target` | `OnScriptAtTargetEvent` | 550 |
| `not_at_target` | `OnScriptNotAtTargetEvent` | 563 |
| `at_rot_target` | `OnScriptAtRotTargetEvent` | 576 |
| `not_at_rot_target` | `OnScriptNotAtRotTargetEvent` | 589 |

`llTarget` (`LSLSystemAPI.cs:6017`) and `llRotTarget` (`LSLSystemAPI.cs:6035`) call into OpenSim correctly but the resulting `OnScriptAtTargetEvent` / `OnScriptAtRotTargetEvent` events have no subscriber in Phlox, so the `at_target` / `at_rot_target` events never fire even when waypoints are reached.

**Halcyon reference:** `EngineInterface.cs:179–191` — same gap exists in Halcyon (EventRouter not ported). This is a known Halcyon-level omission we inherited, not a regression from our port.

**SL spec:** https://wiki.secondlife.com/wiki/attach, https://wiki.secondlife.com/wiki/at_target, https://wiki.secondlife.com/wiki/money

**Minimum reproduction:**
```lsl
default {
    attach(key id) { llSay(0, "attached: " + (string)id); }
}
```
Attach the object. "attached: ..." will not appear.

**Impact:** Attachment HUDs cannot detect wear/remove. `llTarget`-based navigation systems do not function. `money` event for commerce scripting is absent.

---

### C-4: `CHANGED_REGION_START` never fires on region restart

**Where:** `PhloxExecutionScheduler.cs:108–228` (our `FinishedLoading`)

**What:** Halcyon's `ExecutionScheduler.FinishedLoading` (Halcyon `ExecutionScheduler.cs:401–410`) fires `CHANGED_REGION_START` when `loadRequest.ChangedRegionStart` is set:
```csharp
// Halcyon ExecutionScheduler.cs:401–410
if (loadRequest.ChangedRegionStart &&
    interp.Script.FindEvent(interp.ScriptState.LSLState, (int)Types.SupportedEventList.Events.CHANGED) != null)
{
    const int CHANGED_REGION_START = 0x400;
    this.PostEvent(interp.ItemId,
        new VM.PostedEvent {
            EventType = Types.SupportedEventList.Events.CHANGED,
            Args = new object[] { CHANGED_REGION_START } });
}
```

Our `FinishedLoading` at `PhloxExecutionScheduler.cs:168–228` fires `STATE_ENTRY` for fresh starts and restores timer/listens for restores but never posts `CHANGED_REGION_START`. Our `PhloxLoadRequest` struct carries a `StateSource` integer but the scheduler never checks whether it indicates a region restart.

`CHANGED_REGION_START = 0x400` is defined in `PhloxEngine.cs:360` and in `DefaultConstants.cs:300` but is never dispatched.

**Halcyon reference:** `ExecutionScheduler.cs:401–410`

**SL spec:** https://wiki.secondlife.com/wiki/changed — `CHANGED_REGION_START` fires when the region restarts.

**Minimum reproduction:**
```lsl
default {
    changed(integer c) {
        if (c & CHANGED_REGION_START)
            llSay(0, "Region restarted");
    }
}
```
Restart the region. "Region restarted" will not appear.

**Impact:** Scripts that detect region restart (to reinitialize state, re-request permissions, or notify owners) never trigger.

---

### C-5: `OnSceneObjectPartUpdated` fires `CHANGED_SHAPE` for all full-update scene events

**Where:** `PhloxEngine.cs:363–378`

**What:**
```csharp
// PhloxEngine.cs:363–378
private void OnSceneObjectPartUpdated(SceneObjectPart part, bool full)
{
    if (!full) return;
    var parms = new EventParams("changed",
        new object[] { CHANGED_SHAPE },
        new DetectParams[0]);
    PostObjectEvent(part.LocalId, parms);
}
```

`OnSceneObjectPartUpdated` fires on any property update that triggers a full scene object part sync — including physics position updates, texture changes, scale changes, and inventory changes. All of these currently deliver `CHANGED_SHAPE` to scripts regardless of what actually changed. Additionally, `OnScriptChangedEvent` (line 380) fires the correct `changed` value for the same events, creating duplicate `changed` events for shape, scale, and link changes.

**Halcyon reference:** Halcyon's `EngineInterface.cs` does NOT subscribe to `OnSceneObjectPartUpdated` at all. It relies solely on `OnScriptChangedEvent` (routed through EventRouter). We added `OnSceneObjectPartUpdated` as a workaround but the implementation is incorrect.

**SL spec:** https://wiki.secondlife.com/wiki/changed

**Minimum reproduction:**
```lsl
default {
    changed(integer c) {
        llSay(0, "changed: " + (string)c);
    }
}
```
Move the object (physics position update). The script will report `changed: 4` (CHANGED_SHAPE) spuriously. Scripts that gate on `CHANGED_SHAPE` will fire for every physics tick when the object is physical.

**Impact:** Scripts gating on CHANGED_SHAPE fire spuriously. Detecting real shape changes via `changed()` is unreliable. Physical objects generate massive spam to scripts.

---

## Medium

### M-1: MapEventFlag missing 5 event-type cases (already on fix list)

**Where:** `LSLSystemAPI.cs:10397–10437`

Confirmed. `LINKSET_DATA`, `TRANSACTION_RESULT`, `BOT_UPDATE`, `EXPERIENCE_PERMISSIONS`, `EXPERIENCE_PERMISSIONS_DENIED` all fall to the `default: return 0UL` case. Scripts handling these events will not have them routed by the physics/scene engine.

No new detail beyond what was previously reported.

---

### M-2: `llFrand` creates a new `Random` per call — repeated values at fast call rates

**Where:** `LSLSystemAPI.cs:130`

```csharp
// Ours — bug:
public float llFrand(float mag) => (float)(new Random().NextDouble() * mag);
```
```csharp
// Halcyon LSLSystemAPI.cs:807 — correct:
private static Random s_random = new Random();  // line 112
return (float)(s_random.NextDouble() * mag);
```

`new Random()` seeds from `Environment.TickCount`. Within one script execution pass (8 ticks), `Environment.TickCount` may not advance between calls, producing identical seeds and identical return values.

**Halcyon reference:** `InWorldz.Phlox.Engine/LSLSystemAPI.cs:112` (static initializer), `:807` (usage)

**SL spec:** https://wiki.secondlife.com/wiki/llFrand — "Returns a pseudo-random number in [0, mag)".

**Minimum reproduction:**
```lsl
default {
    state_entry() {
        integer i;
        for(i = 0; i < 5; i++)
            llSay(0, (string)llFrand(1000.0));
    }
}
```
Expect five distinct values. On fast hardware you will see runs of identical values (all five the same if TickCount didn't advance).

---

### M-3: `llGiveMoney` never tries `IMoneyModule` — returns 0 even when an economy module is present

**Where:** `LSLSystemAPI.cs:8517–8537`

Our code:
```csharp
// Never queries IMoneyModule
if ((item.PermsMask & 0x02) == 0) { ... return 0; }
// "No economy module — silently return 0"
ScriptSleep(3000);
return 0;
```

Halcyon `LSLSystemAPI.cs:2978–3016` checks `IMoneyModule money = World.RequestModuleInterface<IMoneyModule>()` and only calls `NotImplemented` if `money == null`. If a money module is present, Halcyon completes the transfer.

**Halcyon reference:** `InWorldz.Phlox.Engine/LSLSystemAPI.cs:3006–3015`

**SL spec:** https://wiki.secondlife.com/wiki/llGiveMoney

**Impact:** If a compatible `IMoneyModule` is ever installed on this grid, `llGiveMoney` will still return 0 and never attempt the transfer. The function is hardcoded to fail.

---

### M-4: `llSetPos` has no distance-limit clamping for root prims

**Where:** `LSLSystemAPI.cs:485–497`

Our implementation clamps X/Y to region bounds (0–255.9) and Z to 0–4096 but imposes no per-call distance limit. SL spec and Halcyon cap unattached root prim movement to 10m per call; attachments cap at 3.5m; child prims at 54m.

Halcyon `LSLSystemAPI.cs:2332–2400` implements `SetPosAdjust` with explicit limit logic:
```
unattached root prim: 10m cap (Halcyon:2365)
attached root prim:   3.5m cap
child prim:           54m or 256m (child of rezzed)
```

**Halcyon reference:** `InWorldz.Phlox.Engine/LSLSystemAPI.cs:2332–2400`

**SL spec:** https://wiki.secondlife.com/wiki/llSetPos — "If the object is not physical, the object will snap to within 10 m of its current position."

**Impact:** Scripts using `llSetPos` can teleport root prims unlimited distances in one call. Anti-griefer containment logic that relies on the 10m cap does not apply.

---

### M-5: `llHTTPRequest` missing adaptive queue-based throttle delay

**Where:** `LSLSystemAPI.cs:6818–6859`

Our implementation calls `httpMod.CheckThrottle(m_localID, m_host.OwnerID)` and returns `UUID.Zero` if throttled, but does not sleep the script. Halcyon `LSLSystemAPI.cs:13747–13774` computes a variable delay based on event queue fill percentage and request queue fill percentage, sleeping up to 80ms before submitting the request.

The effect: our implementation drops requests silently when throttled. Halcyon's implementation backs off and retries (from the script's perspective). Scripts making rapid `llHTTPRequest` calls may exhaust the queue silently in ours.

**Halcyon reference:** `InWorldz.Phlox.Engine/LSLSystemAPI.cs:13758–13774`

---

### M-6: `HasScript` always returns false, `GetScriptErrors` always returns empty (re-confirmed C-5 from v1)

**Where:** `PhloxEngine.cs:498–499`

```csharp
public System.Collections.ArrayList GetScriptErrors(UUID itemID) => new System.Collections.ArrayList();
public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
```

The StoreScriptErrors fix in `SceneObjectPartInventory.cs:694–744` correctly routes error-reporting to only the engine that owns the script (not a poll-all-engines), which prevents the YEngine blocking bug. However, Phlox's `GetScriptErrors` still returns empty, so compile errors from Phlox scripts are never surfaced through the `CreateScriptInstanceEr()` code path.

**Impact:** Compilation errors in Phlox scripts are invisible to the viewer's Script Editor error pane when the editor uses the synchronous `CreateScriptInstanceEr` path.

---

### M-7: `PhloxEngine` missing `OnReloadScript` subscription

**Where:** `PhloxEngine.cs:101–115`

Halcyon `EngineInterface.cs:181` subscribes to `OnReloadScript` as a separate event from `OnRezScript`. This handles the case where a script is reloaded in-place (e.g., script object replacement without unloading). Our PhloxEngine has no `OnReloadScript` subscription. Reload events would only reach us via `OnRezScript` if OpenSim falls back to firing that event.

**Halcyon reference:** `EngineInterface.cs:181` (`OnReloadScript`), `:421–455` (handler)

---

### M-8: `llRegionSay` has no mandatory sleep delay

**Where:** `LSLSystemAPI.cs:276–285`

Halcyon `LSLSystemAPI.cs:1084` calls `ScriptSleep(15)` at the end of `llRegionSay`. Our implementation has no sleep. SL spec does not mandate a delay for `llRegionSay`, so we match SL spec but diverge from Halcyon. Scripts ported from Halcyon that budget for the 15ms delay will run 15ms faster per `llRegionSay` call (minor).

Also applies to `llSay`, `llShout`, `llWhisper`: Halcyon's `SimChat` helper (`LSLSystemAPI.cs:1044–1051`) adds `ScriptSleep(15)` to all chat sends. Our `llSay`/`llShout`/`llWhisper` (lines 248–264) have no delay. Again, we match SL spec here; this is a divergence from Halcyon, not a bug.

**Halcyon reference:** `InWorldz.Phlox.Engine/LSLSystemAPI.cs:1042–1052`

---

## Low

### L-1: `STATUS_SANDBOX` accepted but not enforced

**Where:** `LSLSystemAPI.cs:856–857`

```csharp
if ((status & STATUS_SANDBOX) != 0)
    { /* not implemented */ }
```

Halcyon `LSLSystemAPI.cs:1564` calls `NotImplemented("llSetStatus - STATUS_SANDBOX")`. Our code accepts the call silently. Effect is the same (no enforcement), but ours gives no feedback.

**SL spec:** https://wiki.secondlife.com/wiki/llSetStatus — `STATUS_SANDBOX` restricts rezzing to within 10m.

---

### L-2: `llTransferLindenDollars` always fires `LINDENDOLLAR_INSUFFICIENTFUNDS`

**Where:** `LSLSystemAPI.cs:8539–8545`

No real economy path exists. This is intentional for a no-economy grid, but scripts testing `LINDENDOLLAR_COMPLETEDSUCCESS` will always fail.

---

### L-3: `llVolumeDetect` not written to `MiscAttributes`

**Where:** `LSLSystemAPI.cs:6148–6151`

```csharp
public void llVolumeDetect(int detect)
{
    m_host.ParentGroup.RootPart.ScriptSetVolumeDetect(detect != 0);
}
```

No `MiscAttributes` write. Volume detect state is lost on region restart. Directly related to C-1.

---

### L-4: `llScriptProfiler` silently accepted

**Where:** `LSLSystemAPI.cs:1291`

```csharp
public void llScriptProfiler(int flags) { }
```

No profiling data is collected. SL's `llScriptProfiler` triggers runtime statistics gathering. Not in Halcyon either, but it's a silent no-op.

---

## Coverage Gaps

### Explicit `Stub()` calls

| Line | Function | Behavior |
|------|----------|----------|
| 2503 | `llListen` | Returns -1 when `ListenManager` is null (startup race condition) |
| 12197 | `llTargetedEmail` | Logs warning, no action |
| 12248 | `llDetectedDamage` | Logs warning, returns 0.0f |

### Silent returns / no-ops (not `Stub()`)

| Line | Function | Returns | Notes |
|------|----------|---------|-------|
| 856–857 | `llSetStatus(STATUS_SANDBOX, ...)` | void (no-op) | See L-1 |
| 2296 | `iwIsPlusUser` | `0` | InWorldz-specific; no equivalent |
| 3349 | `iwRezPrim` | `UUID.Zero.ToString()` | Requires InWorldz scene API |
| 3488–3489 | `llCheckRezError` / `iwCheckRezError` | `0` | Requires InWorldz CheckRezError |
| 6136 | `llCollisionFilter` | void (no-op) | See C-2 |
| 5246 | `llCollisionSprite` | void (no-op) | Deprecated in SL too |
| 7199 | `iwStringCodec` | `str` (passthrough) | Requires InWorldz CodecUtil |
| 8629 | `llGetParcelFlags2` | `0` | Halcyon-2 variant, not standard |
| 1100–1105 | `llMinEventDelay` | void (no-op) | Deliberate; SL also barely enforces |
| 8539–8545 | `llTransferLindenDollars` | fires INSUFFICIENTFUNDS | See L-2 |

### Phase 33–34 implementations (verified working)

Verified implemented and functional:
- `llGetPayPrice` (line 12521), `llGetGroundTexture` (line 12551), `llGetVehicleFlags` (line 12569), `llGetLinkGLTFOverrides` (line 12581)
- `botSetPersistent` through `botSetPersistentData` (lines 12637–12695) — delegate to `BotPersistenceManager`

---

## Event wiring matrix

Compared against Halcyon `EngineInterface.cs:179–191` (Halcyon's subscription block) and our `PhloxEngine.cs:101–115`. Note: Halcyon does NOT subscribe to collision, target, attach, money, or moving events in `EngineInterface.cs` — these were routed via `EventRouter.cs` which was not ported.

| # | Event | Wired in our PhloxEngine? | Source delegate | Wired in Halcyon EngineInterface? | Notes |
|---|-------|--------------------------|-----------------|----------------------------------|-------|
| 1 | at_rot_target | **NO** | `OnScriptAtRotTargetEvent` (EM:576) | NO (EventRouter not ported) | llRotTarget works but event never delivered |
| 2 | at_target | **NO** | `OnScriptAtTargetEvent` (EM:550) | NO | llTarget works but event never delivered |
| 3 | attach | **NO** | `OnAttach` (EM:885) | NO | Not in Halcyon EngineInterface either |
| 4 | changed | Partial | `OnScriptChangedEvent` (correct) + `OnSceneObjectPartUpdated` (wrong — fires CHANGED_SHAPE for all full updates; see C-5) | YES (OnScriptChangedEvent only) | Duplicate delivery; see C-5 |
| 5 | collision | **NO** | `OnScriptColliding` (EM:619) | NO (EventRouter) | See C-2 |
| 6 | collision_end | **NO** | `OnScriptCollidingEnd` (EM:632) | NO (EventRouter) | See C-2 |
| 7 | collision_start | **NO** | `OnScriptColliderStart` (EM:605) | NO (EventRouter) | See C-2 |
| 8 | control | YES | `OnScriptControlEvent` (PE:429) | YES | ✓ |
| 9 | dataserver | YES | Posted by HTTP/sensor modules via PostObjectEvent | YES | ✓ |
| 10 | email | YES | Email module callback | YES | ✓ |
| 11 | http_request | YES | HTTP server module | YES | ✓ |
| 12 | http_response | YES | IHttpRequestModule callback | YES | ✓ |
| 13 | land_collision | **NO** | `OnScriptLandColliding` (EM:658) | NO (EventRouter) | See C-2 |
| 14 | land_collision_end | **NO** | `OnScriptLandColliderEnd` (EM:671) | NO (EventRouter) | See C-2 |
| 15 | land_collision_start | **NO** | `OnScriptLandColliderStart` (EM:645) | NO (EventRouter) | See C-2 |
| 16 | link_message | YES | Posted by llMessageLinked | YES | ✓ |
| 17 | listen | YES | PhloxListenManager.DeliverChat | YES (WorldComm) | ✓ |
| 18 | money | **NO** | `OnMoneyTransfer` (EM:1041) | NO | See C-3 |
| 19 | moving_end | **NO** | `OnScriptMovingEndEvent` (EM:537) | NO | See C-3 |
| 20 | moving_start | **NO** | `OnScriptMovingStartEvent` (EM:531) | NO | See C-3 |
| 21 | no_sensor | YES | Sensor plugin callback via AsyncCommandManager | YES | ✓ |
| 22 | not_at_rot_target | **NO** | `OnScriptNotAtRotTargetEvent` (EM:589) | NO | See C-3 |
| 23 | not_at_target | **NO** | `OnScriptNotAtTargetEvent` (EM:563) | NO | See C-3 |
| 24 | object_rez | YES | Posted by llRezObject (LSLSystemAPI.cs:3294–3299) | YES | Only for objects rezzed by this script |
| 25 | on_rez | YES | FinishedLoading PostOnRez path (PE:211–218) | YES | ✓ |
| 26 | path_update | Unverified | Pathfinding subsystem | Unverified | Not audited |
| 27 | remote_data | YES | XMLRPC module callback | YES | ✓ |
| 28 | run_time_permissions | YES | Posted by llRequestPermissions accept path | YES | ✓ |
| 29 | sensor | YES | Sensor plugin via AsyncCommandManager | YES | ✓ |
| 30 | state_entry | YES | FinishedLoading fresh start (PE:173–177) | YES | ✓ |
| 31 | state_exit | YES | VM statechg opcode via OnStateChange handler in Interpreter | YES | ✓ |
| 32 | timer | YES | PhloxExecutionScheduler timer tracking | YES | ✓ |
| 33 | touch | YES | `OnObjectGrabbing` → PostTouchEvent (PE:251–262) | YES | ✓ |
| 34 | touch_end | YES | `OnObjectDeGrab` → PostTouchEvent (PE:264–275) | YES | ✓ |
| 35 | touch_start | YES | `OnObjectGrab` → PostTouchEvent (PE:238–249) | YES | ✓ |
| 36 | transaction_result | Unverified (MapEventFlag=0) | Economy module | Unverified | MapEventFlag missing; see M-1 |
| 37 | bot_update | Unverified (MapEventFlag=0) | Bot subsystem | Unverified | MapEventFlag missing; see M-1 |
| 38 | linkset_data | YES (dispatch) / partial (flags) | PostObjectLinksetDataEvent (PE:490–496) | Unverified | Dispatch exists but MapEventFlag=0; see M-1 |

**Wiring summary:**
- Confirmed wired: 15 events
- Confirmed NOT wired: 14 events (collision family ×6, target family ×4, attach, money, moving ×2)
- Partial/incorrect: 1 (changed — duplicate CHANGED_SHAPE from OnSceneObjectPartUpdated)
- Unverified: 3 (path_update, transaction_result, bot_update)

**Compared to Halcyon EngineInterface:** Our subscription set is smaller. Halcyon adds `OnGroupCrossedToNewParcel` (parcel script enable/disable), `OnSOGOwnerGroupChanged` (group change parcel re-check), `OnCrossedAvatarReady` (avatar control restoration after crossing), `OnGroupBeginInTransit`/`OnGroupEndInTransit` (crossing wait disable/enable), and `OnReloadScript`. We have none of these.

---

## 30-function behavioral drift sample vs Halcyon

Functions sampled for body-level drift (beyond what's captured in Critical/Medium above):

| Function | Our line | Halcyon line | Drift |
|----------|----------|--------------|-------|
| `llFrand` | 130 | 807 | **BUG** — new Random() per call; see M-2 |
| `llSay` | 248 | 1059 | We: no delay. Halcyon: 15ms via SimChat(). SL spec: no delay. We match spec. |
| `llShout` | 254 | 1064 | Same as llSay |
| `llWhisper` | 260 | 1054 | Same as llSay |
| `llRegionSay` | 276 | 1069 | We: no delay. Halcyon: 15ms. SL spec: no delay. We match spec; see M-8. |
| `llSetPos` | 485 | 2325 | **BUG** — no 10m distance cap for root prims; see M-4 |
| `llSetRot` | 499 | ~2433 | 200ms sleep ✓. Logic equivalent. |
| `llSensorRepeat` | 2486 | 1154 | **BUG** — no MiscAttributes write; see C-1 |
| `llSensorRemove` | 2493 | 1165 | **BUG** — no MiscAttributes.Remove |
| `llListen` | 2501 | 1110 | Equivalent behavior. Halcyon uses WorldComm; ours uses PhloxListenManager. Both persist to ActiveListens. ✓ |
| `llSetTimerEvent` | 1098 | ~4030 | Both route to scheduler SetTimer. ✓ |
| `llMinEventDelay` | 1100 | 4036 | Both no-op (Halcyon catches NotImplementedException). Same effect. |
| `llResetScript` | 1109 | ~5740 | Routes to ApiResetScript. ✓ |
| `llRequestPermissions` | 1380 | 4499 | Both have ScriptSleep(200) when agent absent. Halcyon has PERMISSION_TELEPORT temp-attachment check (line 4518–4527); ours skips this check. Minor drift for temp attachment scripts. |
| `llRezObject` | 3259 | 3305 | We: ScriptSleep(100) upfront. Halcyon: async SysReturn with calculated delay. Functionally similar but our approach blocks the script 100ms always, Halcyon's delay scales with object complexity. |
| `llGiveMoney` | 8517 | 2978 | **DRIFT** — we skip IMoneyModule; see M-3 |
| `llVolumeDetect` | 6148 | ~5516 | No MiscAttributes write; see L-3 |
| `llGetOwner` | 399 | 3876 | Both return `m_host.OwnerID.ToString()`. ✓ |
| `llGetNumberOfPrims` | 405 | 10697 | Equivalent. ✓ |
| `llSetObjectName` | 402 | 6979 | Equivalent. ✓ |
| `llHTTPRequest` | 6818 | 13747 | **DRIFT** — no adaptive delay; see M-5 |
| `llHTTPResponse` | 6860 | ~13840 | Routes to IUrlModule. ✓ |
| `llSleep` | 1099 | ~148 | Both route to ScriptSleep. ✓ |
| `llGetPos` | ~490 | 2411 | Both return world position. ✓ |
| `llGetTimestamp` | ~various | 10691 | Equivalent ISO8601 format. ✓ |
| `llGetScriptName` | ~409 | 6068 | Equivalent. ✓ |
| `llTransferLindenDollars` | 8539 | ~GiveMoney | We always fire INSUFFICIENTFUNDS; Halcyon has real async path (GiveMoney). See L-2. |
| `llMapDestination` | 8565 | ~various | Equivalent detect-vars approach. ✓ |
| `llSetPayPrice` | 8550 | ~various | Equivalent PayPrice array. ✓ |
| `llSetStatus` (PHYSICS/PHANTOM) | 820–842 | 1475 | Equivalent for physics/phantom bits. STATUS_SANDBOX no-op (both). ✓ for common bits. |

---

## VM / Runtime Status

| Area | Status | Notes |
|------|--------|-------|
| State transition (statechg opcode) | ✓ Correct | `StateChangePrep()` clears event queue per SL spec |
| Event queue max size | ✓ | `MAX_EVENT_QUEUE_SIZE = 64` |
| Overflow events | ✓ | ON_REZ, STATE_ENTRY, STATE_EXIT, TIMER bypass queue limit |
| ScriptSleep clock | ✓ Consistent | Both set and check use `Util.EnvironmentTickCount()` — no cross-clock mismatch (previous audit's finding was incorrect) |
| Stack underflow | ⚠ Unguarded | C# `InvalidOperationException` on underflow, not clean script error |
| Unknown opcode | ✓ | `throw new VMException("Unhandled opcode: " + opcode)` |
| Script reset | ✓ | `RuntimeState.Reset()` clears all state |
| State persistence | ✓ | SQLite + protobuf-net; 2500ms flush |
| Timer restore on reload | ✓ | `FinishedLoading` re-registers timers from `TimerInterval` |
| Listen restore on reload | ✓ | `FinishedLoading` re-registers from `ActiveListens` |
| SensorRepeat restore | **NO** | See C-1 — not written to MiscAttributes, not restored |
| VolumeDetect restore | **NO** | See L-3 — not written to MiscAttributes |
| Control restore | **NO** | See C-1 — OnScriptInjected is empty |
| CHANGED_REGION_START on restart | **NO** | See C-4 |

---

## Compiler/Parser Status

| Feature | Status | Notes |
|---------|--------|-------|
| `for(;;)` infinite loop | ✓ Working | Confirmed in-world |
| `for(init; cond; loop)` | ✓ Working | Confirmed in-world (v1 finding retracted) |
| `while` / `do-while` | ✓ | ByteCodeEmitter.cs:While, DoWhile |
| post-increment `i++` / `i--` | ✓ | GenVisitor.cs:579–599 |
| pre-increment `++i` / `--i` | ✓ | GenVisitor.cs:592–606 |
| compound assignment `+=` `-=` `*=` `/=` `%=` | ✓ | ByteCodeEmitter.cs:CompoundAssignOp |
| bitwise ops `&` `\|` `^` `<<` `>>` | ✓ | GenVisitor.cs:414–488 |
| boolean ops `&&` `\|\|` | ✓ | GenVisitor.cs:404–411 |
| ternary `?:` | N/A | Not an LSL operator; LSL has no ternary |
| string escape sequences | Unverified | Grammar handles `\\`, `\"`, `\n`, `\t` per ANTLR lexer rules |
| key+string concatenation | ✓ | `SymbolTable.cs:40–41` — both directions return STRING |
| `default { }` state | ✓ | Compiler/parser handle default state normally |
| jump/label (`@label`/`jump`) | Unverified | LSL jump/label syntax; not checked this session |

---

## Type System Status

All operator matrices in `SymbolTable.cs` verified:

| Matrix | Status | Notes |
|--------|--------|-------|
| additionResultType | ✓ | key+string and string+key → STRING (previously fixed) |
| subtractionResultType | ✓ | Standard numeric types only |
| multiplicationResultType | ✓ | vector×vector → FLOAT (dot product); rotation×vector = VOID (correct per SL spec — SL only defines vector×rotation, not rotation×vector) |
| divisionResultType | ✓ | vector÷rotation → VECTOR ✓ |
| modResultType | ✓ | vector%vector → VECTOR (cross product) ✓ |
| shiftResultType | ✓ | integer only |
| promoteFromTo | ✓ | int→float and key↔string promotions present |
| castFromTo | ✓ (spot-checked) | string→all types present; (int)rotation = VOID correct |

---

## Recent Fix Area — StoreScriptErrors

**Fix location:** `OpenSim/Region/Framework/Scenes/SceneObjectPartInventory.cs:688–744`

The fix correctly scopes `GetScriptErrors()` to only the engine that accepted the script (`engine` parameter match at line 720), avoiding the deadlock where polling all engines would block on YEngine's `Monitor.Wait` for a compile that Phlox never started.

**Consistency check:** The two callers of `StoreScriptErrors` with `errors=null`:
- Line 502: `StoreScriptErrors(itemID, null, engine)` — engine name passed ✓
- Line 532: `StoreScriptErrors(itemID, null, engine)` — engine name passed ✓

Both callers pass the engine name. The fix is internally consistent.

**Remaining gap:** Phlox's `GetScriptErrors` (`PhloxEngine.cs:498`) still returns `new System.Collections.ArrayList()`. Phlox compile errors reported via `LogOutputListener` (the ANTLR compilation path) are never stored in a per-script error table that `GetScriptErrors` can return. If the viewer calls `CreateScriptInstanceEr` on a Phlox script, it will always receive an empty error list regardless of what the compiler found. The YEngine blocking bug is fixed, but error reporting for Phlox is still non-functional.

---

*Report generated 2026-05-24. All findings are read-only — no source files were modified.*
