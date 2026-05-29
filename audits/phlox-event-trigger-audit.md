# Phlox Event Trigger Audit

**Date:** 2026-05-26  
**Scope:** 9 events across 7 distinct audit questions — covers attach (both sides), moving_start/end, at_target, not_at_target, at_rot_target, not_at_rot_target, money.

---

## Reference Verification

- **Halcyon reference path:** `/mnt/desktop/d-drive/halcyon-reference-fresh/InWorldz/InWorldz.Phlox.Engine/`
- **File header copyright:** `Copyright (c) InWorldz Halcyon Developers` / `Copyright (c) Contributors, http://opensimulator.org/`
- **Status:** VERIFIED REAL

---

## Summary Table

| Event | Trigger exists in our tree? | Fires in normal use? | Halcyon supported? | Fix scope |
|-------|---------------------------|----------------------|-------------------|-----------|
| attach (attach side) | YES — but wrong path | NO — right-click attach bypasses TriggerOnAttach | YES — StateSource dispatch in engine | PHLOX-ONLY |
| attach (detach side) | YES — TriggerOnAttach line 957 | YES — fires on detach | YES | NONE (needs end-to-end test) |
| moving_start | NO — trigger never called | NO | NO | OPENSIM CORE |
| moving_end | NO — trigger never called | NO | NO | OPENSIM CORE |
| at_target | YES — CheckAtTargets/TriggerAtTargetEvent | YES (once Session 3c wires Phlox) | YES | PHLOX-ONLY (Session 3c) |
| not_at_target | YES — TriggerNotAtTargetEvent | YES (once Session 3c wires Phlox) | YES | PHLOX-ONLY (Session 3c) |
| at_rot_target | YES — TriggerAtRotTargetEvent | YES (once Session 3c wires Phlox) | YES | PHLOX-ONLY (Session 3c) |
| not_at_rot_target | YES — TriggerNotAtRotTargetEvent | YES (once Session 3c wires Phlox) | YES | PHLOX-ONLY (Session 3c) |
| money | NO — OnObjectPaid never fired | NO | YES (with real money module) | BLOCKED (dependency) |

---

## Per-Event Detail

---

### attach (attach side)

**Q1 — SL spec**  
`attach(key id)` fires when the object is attached to an avatar. `id` is the wearer's agent UUID. Also fires on rez-as-attachment and region crossing while worn.  
https://wiki.secondlife.com/wiki/Attach

**Q2 — Trigger source in our OpenSim**  
`TriggerOnAttach` is defined in `EventManager.cs:1091` and called at three sites in `AttachmentsModule.cs`:
- **Line 806** (`AttachObjectInternal`, `else` branch): fires only when `resumeScripts == false`
- **Line 957** (`DetachSingleAttachmentToInv`): fires detach (avatarID=UUID.Zero)
- **Line 1279** (`PrepareScriptInstanceForSave`): fires detach (avatarID=UUID.Zero)

The critical issue: the code at line 806 reads:
```csharp
if (resumeScripts)
{
    group.CreateScriptInstances(0, true, m_scene.DefaultScriptEngine, 4);
    group.ResumeScripts();
}
else
    m_scene.EventManager.TriggerOnAttach(group.LocalId, group.FromItemID, sp.UUID);
```
When `resumeScripts=true` (the normal right-click attach path), scripts are freshly started with `stateSource=4` (AttachedRez). `TriggerOnAttach` is NOT called. The comment says "scripts do internal enqueue of attach event" — the script engine is expected to post attach as part of startup, NOT via the `OnAttach` delegate.

**Q3 — Fires in normal use?**  
**NO.** Right-click → Attach from inventory calls `RezSingleAttachmentFromInventoryInternal` → `AttachObjectInternal(..., resumeScripts=true, ...)`. This creates script instances with `stateSource=4=AttachedRez` and does NOT call `TriggerOnAttach`. Our `OnAttach` handler is never invoked. The diagnostic log silence during the attach test confirmed this.

**Q4 — Halcyon**  
Halcyon's `SceneGraph.cs:956` fires `TriggerOnAttachObject(remoteClient.AgentId, group.LocalId)` from within the attach path when `(flags & AttachFlags.DontFireOnAttach) == 0`. This is a direct trigger in the scene graph — different architecture than ours.

However, YEngine (in our tree) handles this the correct way for our codebase: `XMRInstCtor.cs` has a `switch(m_StateSource)` block where `case StateSource.AttachedRez:` posts `attach(AttachedAvatar.ToString())` directly. This is the authoritative pattern for our OpenSim version.

**Q5 — What it takes to fix**  
**PHLOX-ONLY.** Add a `StateSource.AttachedRez` case to the dispatch block in `PhloxExecutionScheduler.FinishedLoading`, analogous to the existing C-4 `StateSource.RegionStart` dispatch.

Sketch:
```
File: PhloxExecutionScheduler.cs, in FinishedLoading after OnScriptInjected returns
Location: the switch-like block that currently handles StateSource.RegionStart (C-4)

if (req.StateSource == (int)StateSource.AttachedRez)
{
    SceneObjectPart part = m_scene.GetSceneObjectPart(req.LocalID);
    if (part != null &&
        interp.Script.FindEvent(interp.ScriptState.LSLState,
            (int)SupportedEventList.Events.ATTACH) != null)
    {
        PostEvent(req.ItemID, new PostedEvent
        {
            EventType = SupportedEventList.Events.ATTACH,
            Args = new object[] { part.ParentGroup.AttachedAvatar.ToString() }
        });
    }
}
```

`req.LocalID` is the prim's localID; `GetSceneObjectPart` retrieves it; `ParentGroup.AttachedAvatar` is the wearer UUID (set during AttachToAgent). Must check that `FindEvent` returns non-null before posting (same guard as C-4).

---

### attach (detach side)

**Q1 — SL spec**  
`attach(key id)` fires with `id == NULL_KEY` when the object is detached. Same event, different argument.  
https://wiki.secondlife.com/wiki/Attach

**Q2 — Trigger source in our OpenSim**  
`TriggerOnAttach(so.LocalId, so.UUID, UUID.Zero)` is called at:
- `AttachmentsModule.cs:957` — in `DetachSingleAttachmentToInv`, fires after inventory write before `ResumeScripts()`. Scripts continue running.
- `AttachmentsModule.cs:1279` — in `PrepareScriptInstanceForSave` when `fireDetachEvent=true`, with a 30ms sleep after to allow the event handler to run before script state is saved.

Both paths pass `avatarID=UUID.Zero`, which causes our handler to post `"attach"` with `{ UUID.Zero.ToString() }` = `NULL_KEY`. This is correct SL behavior.

**Q3 — Fires in normal use?**  
**YES.** Right-click → Detach calls `DetachSingleAttachmentToInv` which fires `TriggerOnAttach(..., UUID.Zero)`. Our `OnAttach` diagnostic log confirmed the handler was called during the in-world test.

Timing note at line 959-961: after firing the event, the code calls `so.RemoveScriptsPermissions(...)` and `so.ResumeScripts()` — scripts keep running, so the event can actually execute.

**Q4 — Halcyon**  
Halcyon's `EventRouter` had a separate `OnDetachObject(uint localId)` handler that posted `attach(UUID.Zero.ToString())`. Our combined `OnAttach` delegate handles both cases (avatarID=UUID.Zero → detach) — functionally equivalent.

**Q5 — What it takes to fix**  
**NONE** for the trigger side. The trigger fires; the handler posts the event. Needs end-to-end verification that the script receives `NULL_KEY` correctly (no open bugs here from the test; it was just not proven, not disproven).

---

### moving_start

**Q1 — SL spec**  
`moving_start()` fires when a physics-enabled object transitions from stationary to moving (velocity transitions from ≈zero to non-zero).  
https://wiki.secondlife.com/wiki/Moving_start

**Q2 — Trigger source in our OpenSim**  
`TriggerScriptMovingStartEvent(uint localID)` is **declared** in `EventManager.cs:2342` and fires `OnScriptMovingStartEvent`. It is **never called** anywhere else in our OpenSim tree. Only Phlox and YEngine subscribe to it; nobody fires it.

**Q3 — Fires in normal use?**  
**NO.** No code path invokes the trigger. Dropping a physical prim, pushing it, anything — the trigger is never called.

**Q4 — Halcyon**  
Halcyon also never calls this trigger. `EventRouter.cs` had stub handlers for `moving_start` and `moving_end` (lines 497-509) that were never subscribed in `HookUpEvents()`. The `ScriptEvents` flags exist in Halcyon's enum. The feature was stub-only in Halcyon.

**Q5 — What it takes to fix**  
**OPENSIM CORE** — requires adding velocity-transition detection to the scene's physics update loop.

Approach:
- `Scene.cs`, inside `Update()` or a physics step callback, after physics has been simulated for the frame
- Maintain a `Dictionary<uint, bool> m_wasMoving` tracking whether each physics object was moving last frame
- After `SimulateWorld(FrameTime)`, iterate active physics actors; for each physical `SceneObjectPart`:
  - current moving = `physActor.Velocity.LengthSquared() > threshold`
  - if `wasMoving=false → moving=true`: call `EventManager.TriggerScriptMovingStartEvent(localID)`
  - if `wasMoving=true → moving=false`: call `EventManager.TriggerScriptMovingEndEvent(localID)`
  - update `m_wasMoving[localID]`

Alternative: add a callback in `BSPrim` (BulletSim) or `OdePrim` (ubODE) when velocity changes state — more architecturally correct but more invasive.

This is non-trivial: requires deciding which physics plugin to target, handling prim removal/add to tracking dict, choosing an appropriate velocity threshold. **Not a one-line fix.**

---

### moving_end

**Q1 — SL spec**  
`moving_end()` fires when a physics-enabled object transitions from moving to stationary.  
https://wiki.secondlife.com/wiki/Moving_end

**Q2–Q5**  
Identical analysis to `moving_start`. `TriggerScriptMovingEndEvent` is declared, never called, never called in Halcyon, never fires in normal use. Fix is the same **OPENSIM CORE** approach described for moving_start (same per-frame tracking loop, opposite condition).

---

### at_target

**Q1 — SL spec**  
`at_target(integer handle, vector targetpos, vector ourpos)` fires each frame that a prim with an active `llTarget()` is within the specified range of its target position.  
https://wiki.secondlife.com/wiki/At_target

**Q2 — Trigger source in our OpenSim**  
`TriggerAtTargetEvent(UUID scriptID, uint handle, Vector3 targetpos, Vector3 currentpos)` fires `OnScriptAtTargetEvent`. It is called from:

- `SceneObjectGroup.cs:4937` — inside `CheckAtTargets()`, for each target where the group is within range.

`CheckAtTargets()` on `SceneObjectGroup` is called by `Scene.CheckAtTargets()` at `Scene.cs:2008`, which is called every frame at `Scene.cs:1769` (inside `Scene.Update()`).

Prerequisites for `CheckAtTargets()` to run on a group:
1. `llTarget()` was called → `RegisterTargetWaypoint()` → `AddGroupTarget(this)` — group registered in `m_groupsWithTargets`
2. `m_scriptListens_atTarget == true` — set by `SceneObjectGroup.UpdateScriptEvents()` when `scriptEvents.at_target` bit is set by `MapEventFlag(AT_TARGET)` in Phlox LSLSystemAPI.cs (confirmed at line 10461)

Both prerequisites are met correctly in the current code. The trigger IS working.

**Q3 — Fires in normal use?**  
**YES** — once Session 3c subscribes Phlox to `OnScriptAtTargetEvent`. The full pipeline (script calls `llTarget()` → `RegisterTargetWaypoint` → `AddGroupTarget` → frame loop fires `CheckAtTargets()` → `TriggerAtTargetEvent` → `OnScriptAtTargetEvent`) is intact.

**Q4 — Halcyon**  
Same `CheckAtTargets()` architecture, ported from upstream OpenSim. Halcyon's EventRouter subscribed to `OnScriptAtTargetEvent` and posted `at_target` via `PostObjectEvent` with `{ handle, targetpos, atpos }`. Our approach in Session 3c uses `PostScriptEvent` (because our EventManager fires per-script UUID, not per-object localID).

**Q5 — What it takes to fix**  
**PHLOX-ONLY** — Session 3c. Subscribe to `OnScriptAtTargetEvent(UUID scriptID, uint handle, Vector3 targetpos, Vector3 atpos)` and call `PostScriptEvent(scriptID, new EventParams("at_target", new object[] { (int)handle, targetpos, atpos }, new DetectParams[0]))`.

Note: Vector3 args pass directly — Phlox VM casts to `(Vector3)` = `OpenMetaverse.Vector3` (confirmed in Session 3a investigation).

---

### not_at_target

**Q1 — SL spec**  
`not_at_target()` fires each frame that a prim with an active `llTarget()` is NOT within range of any of its targets.  
https://wiki.secondlife.com/wiki/Not_at_target

**Q2 — Trigger source in our OpenSim**  
`TriggerNotAtTargetEvent(UUID scriptID)` fires `OnScriptNotAtTargetEvent`. Called at `SceneObjectGroup.cs:4945` inside the same `CheckAtTargets()` loop, for each scriptID whose target was not reached this frame.

Same prerequisites and same frame-loop timing as `at_target`.

**Q3 — Fires in normal use?**  
**YES** — once Session 3c subscribes Phlox. Same pipeline as at_target.

**Q4 — Halcyon**  
Same architecture.

**Q5 — What it takes to fix**  
**PHLOX-ONLY** — Session 3c. Subscribe to `OnScriptNotAtTargetEvent(UUID scriptID)` and call `PostScriptEvent(scriptID, new EventParams("not_at_target", new object[0], new DetectParams[0]))`.

---

### at_rot_target

**Q1 — SL spec**  
`at_rot_target(integer handle, rotation targetrot, rotation ourrot)` fires each frame that a prim with an active `llRotTarget()` is within the specified angular error of its target rotation.  
https://wiki.secondlife.com/wiki/At_rot_target

**Q2 — Trigger source in our OpenSim**  
`TriggerAtRotTargetEvent(UUID scriptID, uint handle, Quaternion targetrot, Quaternion currentrot)` fires `OnScriptAtRotTargetEvent`. Called at `SceneObjectGroup.cs:4984` inside the rot-target loop in `CheckAtTargets()`.

Prerequisites:
1. `llRotTarget()` called → `RegisterRotTargetWaypoint(m_itemID, rot, error)` → `AddGroupTarget(this)` (same `m_groupsWithTargets` set as pos targets)
2. `m_scriptListens_atRotTarget == true` — set when `scriptEvents.at_rot_target` bit set by `MapEventFlag(AT_ROT_TARGET)` in Phlox (confirmed present in LSLSystemAPI.cs)

**Q3 — Fires in normal use?**  
**YES** — once Session 3c subscribes Phlox.

**Q4 — Halcyon**  
Same pattern. Halcyon EventRouter subscribed `OnScriptAtRotTargetEvent` and posted `at_rot_target`.

**Q5 — What it takes to fix**  
**PHLOX-ONLY** — Session 3c. Subscribe to `OnScriptAtRotTargetEvent(UUID scriptID, uint handle, Quaternion targetrot, Quaternion atrot)` and call `PostScriptEvent(scriptID, new EventParams("at_rot_target", new object[] { (int)handle, targetrot, atrot }, new DetectParams[0]))`.

Note: Quaternion passes directly — Phlox VM casts `VarType.Rotation` to `(Quaternion)` = `OpenMetaverse.Quaternion` (confirmed from Interpreter.Actions.cs).

---

### not_at_rot_target

**Q1 — SL spec**  
`not_at_rot_target()` fires each frame that a prim with an active `llRotTarget()` is NOT within angular error of any of its rotation targets.  
https://wiki.secondlife.com/wiki/Not_at_rot_target

**Q2 — Trigger source in our OpenSim**  
`TriggerNotAtRotTargetEvent(UUID scriptID)` fires `OnScriptNotAtRotTargetEvent`. Called at `SceneObjectGroup.cs:4992` inside the same `CheckAtTargets()` rot-target loop.

**Q3 — Fires in normal use?**  
**YES** — once Session 3c subscribes Phlox.

**Q4 — Halcyon**  
Same architecture.

**Q5 — What it takes to fix**  
**PHLOX-ONLY** — Session 3c. Subscribe to `OnScriptNotAtRotTargetEvent(UUID scriptID)` and call `PostScriptEvent(scriptID, new EventParams("not_at_rot_target", new object[0], new DetectParams[0]))`.

---

### money

**Q1 — SL spec**  
`money(key id, integer amount)` fires when an avatar pays the prim using the viewer's PAY dialog. `id` is the paying avatar's UUID, `amount` is the amount paid.  
https://wiki.secondlife.com/wiki/Money

**Q2 — Trigger source in our OpenSim**  
The PAY path is: avatar clicks Pay → `MoneyTransferRequest` UDP packet → `LLClientView.HandleMoneyTransferRequest` → `Scene.ProcessMoneyTransferRequest` → `EventManager.TriggerMoneyTransfer` → `OnMoneyTransfer`.

`OnMoneyTransfer` is **not** the event that script engines subscribe to. Both YEngine and Halcyon subscribe to `IMoneyModule.OnObjectPaid` — a separate event on the `IMoneyModule` interface.

In our codebase:
- `IMoneyModule.OnObjectPaid` is declared in `IMoneyModule.cs:49` (`event ObjectPaid OnObjectPaid`)
- `SampleMoneyModule.cs:111` declares `public event ObjectPaid OnObjectPaid` — but **never fires it**
- `OnObjectPaid` is **never invoked anywhere** in our entire codebase (`grep -rn "OnObjectPaid(" OpenSim/` returns zero results)

**Q3 — Fires in normal use?**  
**NO.** No avatar PAY action results in `OnObjectPaid` being fired. The `SampleMoneyModule` processes the `MoneyTransferRequest` but never calls `OnObjectPaid`. YEngine's `HandleObjectPaid` (which calls `money()`) is never invoked.

**Q4 — Halcyon**  
Halcyon's `AvatarCurrency.cs:749` fires `this.OnObjectPaid(e.receiver, sourceAvatarID, transAmount)` when an avatar-to-object money transfer is processed by their economy module. Their EventRouter subscribes to `IMoneyModule.OnObjectPaid` at line 92. The full pipeline works in Halcyon because they have a real money module.

Our `SampleMoneyModule` is explicitly documented as "There is no money code here!" (line 49 comment) — it exists only for land-related money operations.

**Q5 — What it takes to fix**  
**BLOCKED — DEPENDENCY.**

Two independent parts must both be done:

**(a) Phlox subscription (easy, Phlox-only):** Subscribe to `IMoneyModule.OnObjectPaid` during `RegionLoaded`, analogous to how YEngine does it in `XMREvents.cs:77-81`:
```
IMoneyModule money = m_Scene.RequestModuleInterface<IMoneyModule>();
if (money != null)
    money.OnObjectPaid += HandleObjectPaid;
```
Handler follows YEngine's pattern: look up `SceneObjectPart` by `objectID`, check `scriptEvents.money` flag, fall back to root part, call `PostObjectEvent("money", { agentID.ToString(), amount }, det)`.

**(b) Money module must fire OnObjectPaid (OPENSIM CORE, blocking):** `SampleMoneyModule.ProcessMoneyTransferRequest` → `TriggerMoneyTransfer` currently doesn't check transactiontype or fire `OnObjectPaid`. `SampleMoneyModule` would need to be modified to fire `OnObjectPaid` when transactiontype is an object-pay transaction, or a real economy module must be deployed that implements this.

Without (b), wiring Phlox (a) accomplishes nothing — `OnObjectPaid` never fires regardless of subscription.

**Human decision required:** Is money/economy functionality in scope? If yes, the minimum path is extending `SampleMoneyModule` to fire `OnObjectPaid` for the appropriate transaction type. A real economy system is out of scope for this audit.

---

## Recommended Implementation Sequence

### Session 3c — at_target / not_at_target / at_rot_target / not_at_rot_target (PHLOX-ONLY)
**4 events. Complexity: LOW.** Full trigger pipeline exists. Only Phlox subscription missing.  
File: `PhloxEngine.cs` only.  
Use `PostScriptEvent(scriptID, ...)` not `PostObjectEvent` — EventManager fires per-script.  
All four delegate signatures confirmed: `(UUID scriptID, ...)`.

### Session 3d-attach — attach (attach side) (PHLOX-ONLY)
**1 event. Complexity: LOW-MEDIUM.**  
Fix goes in `PhloxExecutionScheduler.FinishedLoading`. Add StateSource.AttachedRez case alongside existing StateSource.RegionStart (C-4).  
Needs `m_scene.GetSceneObjectPart(req.LocalID)` to get `ParentGroup.AttachedAvatar`.  
Verify that `m_scene` is accessible from the scheduler (or passed as context).

### Session 3d-detach — attach (detach side) (VERIFY ONLY)
**No code change needed.** Remove diagnostic log from `OnAttach` handler and do end-to-end test.

### Future session — moving_start / moving_end (OPENSIM CORE)
**Complexity: HIGH.** Requires physics-layer velocity-transition tracking. Not a Phlox fix.  
**Recommend: defer.** Not in Halcyon either; not expected by users familiar with InWorldz. Revisit after C/M series is complete.

### Future session — money (BLOCKED)
**Complexity: HIGH.** Requires SampleMoneyModule or economy module work beyond Phlox.  
**Human decision needed** before this session is planned.

---

## Open Questions for Human Decision

1. **Attach event test (detach side):** Is the detach path (`attach(NULL_KEY)`) verified in-world? The diagnostic log confirmed `OnAttach` fired, but it's unknown whether the script's `attach()` handler actually executed. Strip the diagnostic log and do a clean test.

2. **moving_start/end scope:** Implementing these requires modifying `Scene.cs` or a physics plugin (BulletSim/ubODE). This is a significant OpenSim core change. Is this in scope for the current audit sprint, or deferred?

3. **money scope:** `money()` requires a money module that fires `IMoneyModule.OnObjectPaid`. Is implementing pay-to-object functionality (in `SampleMoneyModule` or a new module) in scope? If no: skip money entirely. If yes: extend `SampleMoneyModule` to fire `OnObjectPaid` for transactiontype 5000 (GiveMoney to object — verify the exact type code first).

4. **OnAttach handler (Session 3b):** The `OnAttach` subscription in `PhloxEngine.cs` still exists. Once the attach-side fix moves to `FinishedLoading` (StateSource dispatch), the `OnAttach` handler will cover a narrow edge case (the `resumeScripts=false` path in `AttachObjectInternal`). Should it be kept as a belt-and-suspenders fallback, or removed to avoid double-firing? Most normal attach paths go through `resumeScripts=true`, so double-firing is unlikely but worth confirming.
