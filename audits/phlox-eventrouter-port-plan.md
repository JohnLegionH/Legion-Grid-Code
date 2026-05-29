# Phlox EventRouter Port Plan

**Date:** 2026-05-25  
**Scope:** Wire 13 missing LSL events into `PhloxEngine.cs` (collision family, land_collision family, attach, at_target, not_at_target, at_rot_target, not_at_rot_target, moving_start, moving_end, money)  
**Reference:** `/mnt/desktop/d-drive/halcyon-reference-fresh/InWorldz/InWorldz.Phlox.Engine/EventRouter.cs` (534 lines, verified real, READ-ONLY)

---

## Reference Verification

All 13 missing events confirmed present in:
- `SupportedEventList.Events` enum — ✓
- `MapEventFlag()` case labels in `LSLSystemAPI.cs` — ✓
- `scriptEvents` flags in `SceneObjectGroup` — ✓ (collision, land_collision, money via aggregatedScriptEvents; at_target family via m_scriptListens_* booleans)

---

## Architectural Decision

Add event subscriptions **directly into `PhloxEngine.cs`** in the existing `RegionLoaded` / `RemoveRegion` blocks. Do NOT recreate EventRouter as a separate class.

Subscription add block: `PhloxEngine.cs` lines ~101–114 (`RegionLoaded`)  
Subscription remove block: `PhloxEngine.cs` lines ~121–133 (`RemoveRegion`)

---

## Critical Differences from Halcyon

### 1. Land collision delegate names (NAME MISMATCH)
| Side | Start event | End event |
|------|-------------|-----------|
| Halcyon | `OnScriptLandCollidingStart` | `OnScriptLandCollidingEnd` |
| **Ours** | **`OnScriptLandColliderStart`** | **`OnScriptLandColliderEnd`** |
| Both | `OnScriptLandColliding` | — |

Use our names. Do not use the Halcyon names.

### 2. Target events fire per-script, not per-object
Halcyon's EventManager fires `at_target` / `not_at_target` / `at_rot_target` / `not_at_rot_target` with `uint localID` → handler calls `PostObjectEvent`.

Our `EventManager.cs` fires these with **`UUID scriptID`** — one event per script, not per object.

**Consequence:** handlers must call `PostScriptEvent(scriptID, ...)` not `PostObjectEvent(localID, ...)`.

### 3. Attach/detach combined into one delegate
Halcyon had separate `OnAttachObject(UUID avatarId, uint localId)` and `OnDetachObject(uint localId)`.

Our EventManager has a single delegate:  
`Attach(uint localID, UUID itemID, UUID avatarID)` on `OnAttach`  
When `avatarID == UUID.Zero` → detach. When `avatarID != UUID.Zero` → attach.

### 4. moving_start / moving_end not wired in Halcyon
Halcyon's `EventRouter.cs` has handler stubs (lines 497–509) but never subscribes them in `HookUpEvents()`. Our `EventManager` has working delegates: `OnScriptMovingStartEvent(uint localID)` and `OnScriptMovingEndEvent(uint localID)`. We CAN wire these — do so.

---

## Per-Event Implementation Plan

### Group A — Collision family (3 events)

**Delegates (EventManager.cs):**
```
OnScriptColliderStart   delegate ScriptColliding(uint localID, ColliderArgs col)
OnScriptColliding       delegate ScriptColliding(uint localID, ColliderArgs col)
OnScriptCollidingEnd    delegate ScriptColliding(uint localID, ColliderArgs col)
```

**Handler pattern** (identical for all three, only event type differs):
```csharp
private void OnScriptColliderStart(uint localID, ColliderArgs col)
{
    var det = new List<DetectParams>();
    foreach (var detobj in col.Colliders)
        det.Add(DetectParams.FromDetectedObject(detobj));
    if (det.Count == 0) return;
    m_scene.ForEachScenePresence(sp => { }); // not needed
    PostObjectEvent(localID, new EventParams(
        "collision_start", new object[] { det.Count },
        det.ToArray()));
}
```
Use `"collision_start"`, `"collision"`, `"collision_end"` respectively.

**Subscriptions to add:**
```csharp
m_scene.EventManager.OnScriptColliderStart    += OnScriptColliderStart;
m_scene.EventManager.OnScriptColliding        += OnScriptColliding;
m_scene.EventManager.OnScriptCollidingEnd     += OnScriptCollidingEnd;
```
**Unsubscriptions (mirror).**

---

### Group B — Land collision family (3 events)

**Delegates (EventManager.cs):**
```
OnScriptLandColliderStart   delegate ScriptCollidingMove(uint localID, Vector3 pos)
OnScriptLandColliding       delegate ScriptCollidingMove(uint localID, Vector3 pos)
OnScriptLandColliderEnd     delegate ScriptCollidingMove(uint localID, Vector3 pos)
```
*(Note: our names differ from Halcyon — see Critical Differences §1 above)*

**Handler pattern** (identical for all three, only event type differs):
```csharp
private void OnScriptLandColliderStart(uint localID, Vector3 pos)
{
    PostObjectEvent(localID, new EventParams(
        "land_collision_start", new object[] { pos },
        new DetectParams[0]));
}
```
Use `"land_collision_start"`, `"land_collision"`, `"land_collision_end"` respectively.

**Subscriptions to add:**
```csharp
m_scene.EventManager.OnScriptLandColliderStart += OnScriptLandColliderStart;
m_scene.EventManager.OnScriptLandColliding     += OnScriptLandColliding;
m_scene.EventManager.OnScriptLandColliderEnd   += OnScriptLandColliderEnd;
```
**Unsubscriptions (mirror).**

---

### Group C — Attach / Detach (1 event, 2 cases)

**Delegate (EventManager.cs):**
```
OnAttach    delegate Attach(uint localID, UUID itemID, UUID avatarID)
```

**Handler:**
```csharp
private void OnAttach(uint localID, UUID itemID, UUID avatarID)
{
    PostObjectEvent(localID, new EventParams(
        "attach", new object[] { avatarID.ToString() },
        new DetectParams[0]));
}
```
When `avatarID == UUID.Zero` the LSL script receives `NULL_KEY` — correct detach behaviour.

**Subscription:**
```csharp
m_scene.EventManager.OnAttach += OnAttach;
```
**Unsubscription (mirror).**

---

### Group D — at_target / not_at_target (2 events, PER-SCRIPT)

**Delegates (EventManager.cs):**
```
OnScriptAtTargetEvent     delegate ScriptAtTargetEvent(UUID scriptID, uint handle, Vector3 targetpos, Vector3 atpos)
OnScriptNotAtTargetEvent  delegate ScriptNotAtTargetEvent(UUID scriptID)
```

**Handlers:**
```csharp
private void OnScriptAtTargetEvent(UUID scriptID, uint handle, Vector3 targetpos, Vector3 atpos)
{
    PostScriptEvent(scriptID, new EventParams(
        "at_target", new object[] { (int)handle, targetpos, atpos },
        new DetectParams[0]));
}

private void OnScriptNotAtTargetEvent(UUID scriptID)
{
    PostScriptEvent(scriptID, new EventParams(
        "not_at_target", new object[0],
        new DetectParams[0]));
}
```
**IMPORTANT:** `PostScriptEvent(UUID itemID, ...)` not `PostObjectEvent`. The scene already filters per-script before firing.

**Subscriptions:**
```csharp
m_scene.EventManager.OnScriptAtTargetEvent    += OnScriptAtTargetEvent;
m_scene.EventManager.OnScriptNotAtTargetEvent += OnScriptNotAtTargetEvent;
```
**Unsubscriptions (mirror).**

---

### Group E — at_rot_target / not_at_rot_target (2 events, PER-SCRIPT)

**Delegates (EventManager.cs):**
```
OnScriptAtRotTargetEvent     delegate ScriptAtRotTargetEvent(UUID scriptID, uint handle, Quaternion targetrot, Quaternion atrot)
OnScriptNotAtRotTargetEvent  delegate ScriptNotAtRotTargetEvent(UUID scriptID)
```

**Handlers:**
```csharp
private void OnScriptAtRotTargetEvent(UUID scriptID, uint handle, Quaternion targetrot, Quaternion atrot)
{
    PostScriptEvent(scriptID, new EventParams(
        "at_rot_target", new object[] { (int)handle, targetrot, atrot },
        new DetectParams[0]));
}

private void OnScriptNotAtRotTargetEvent(UUID scriptID)
{
    PostScriptEvent(scriptID, new EventParams(
        "not_at_rot_target", new object[0],
        new DetectParams[0]));
}
```

**Subscriptions:**
```csharp
m_scene.EventManager.OnScriptAtRotTargetEvent    += OnScriptAtRotTargetEvent;
m_scene.EventManager.OnScriptNotAtRotTargetEvent += OnScriptNotAtRotTargetEvent;
```
**Unsubscriptions (mirror).**

---

### Group F — moving_start / moving_end (2 events)

**Delegates (EventManager.cs):**
```
OnScriptMovingStartEvent   delegate ScriptMovingStartEvent(uint localID)
OnScriptMovingEndEvent     delegate ScriptMovingEndEvent(uint localID)
```
*(Halcyon never wired these; we will.)*

**Handlers:**
```csharp
private void OnScriptMovingStartEvent(uint localID)
{
    PostObjectEvent(localID, new EventParams(
        "moving_start", new object[0],
        new DetectParams[0]));
}

private void OnScriptMovingEndEvent(uint localID)
{
    PostObjectEvent(localID, new EventParams(
        "moving_end", new object[0],
        new DetectParams[0]));
}
```

**Subscriptions:**
```csharp
m_scene.EventManager.OnScriptMovingStartEvent += OnScriptMovingStartEvent;
m_scene.EventManager.OnScriptMovingEndEvent   += OnScriptMovingEndEvent;
```
**Unsubscriptions (mirror).**

---

### Group G — money (1 event)

**Reference pattern (EventRouter.cs lines 163–181, 303–315):**  
Halcyon checked the `ScriptEvents.money` flag on the prim, then fell back to root if the child had no handler, then called its own `money()` internal method which built a DetectParams from the paying avatar and posted `{ agentID.ToString(), amount }`.

**Our approach:** Wire into `EventManager.OnObjectPay`. Check if IMoneyModule exists. The EventManager fires `OnObjectPay(IClientAPI client, UUID agentID, UUID receiverID, int amount, int objectID)` — **verify this exact delegate signature before implementing.** If the signature differs, adjust.

The script receives: `money(key id, integer amount)` — post `new object[] { agentID.ToString(), amount }` with a DetectParams carrying the agentID.

**Prerequisite:** Before implementing, read EventManager.cs to confirm the exact `OnObjectPay` delegate signature. The handler pattern:
```csharp
private void OnObjectPay(IClientAPI client, UUID agentID, UUID receiverID, int amount, int objectID)
{
    SceneObjectPart part = m_scene.GetSceneObjectPart((uint)objectID);
    if (part == null) return;

    DetectParams dp = new DetectParams();
    dp.Key = agentID;
    // populate dp fields from agentID as needed

    PostObjectEvent(part.LocalId, new EventParams(
        "money", new object[] { agentID.ToString(), amount },
        new DetectParams[] { dp }));
}
```
**IMoneyModule** exists at `OpenSim/Framework/IMoneyModule.cs`. The module must be active for `OnObjectPay` to fire at all (it's wired by the money module). No RequestModuleInterface guard needed in the handler itself — if the event fires, the module is present.

**Subscription:**
```csharp
m_scene.EventManager.OnObjectPay += OnObjectPay;
```
**Unsubscription (mirror).**

---

## Implementation Session Sizing

| Session | Groups | Events | Files touched |
|---------|--------|--------|---------------|
| 3a | A (collision) + B (land_collision) | 6 | PhloxEngine.cs only |
| 3b | C (attach) + F (moving_start/end) | 3 | PhloxEngine.cs only |
| 3c | D (at_target) + E (at_rot_target) | 4 | PhloxEngine.cs only |
| 3d | G (money) | 1 | PhloxEngine.cs only; pre-read EventManager.OnObjectPay sig first |

Each session: one `PhloxEngine.cs` edit, show diff, stop, wait for build OK.

---

## Pre-Implementation Checks

Before Session 3a begins:
1. Confirm `ColliderArgs` and `DetectParams.FromDetectedObject` are in scope in `PhloxEngine.cs` (check existing usings).
2. Confirm `EventParams` constructor signature used in existing code in `PhloxEngine.cs`.
3. Confirm `PostObjectEvent(uint, EventParams)` and `PostScriptEvent(UUID, EventParams)` are accessible (both confirmed at lines 420 and 390).

Before Session 3d begins:
1. Read `EventManager.cs` `OnObjectPay` delegate definition to confirm exact parameter types.

---

## Out of Scope for This Plan

- Compiler changes (ANTLR4 paths — do not touch)
- Enum extensions outside Phlox (stop and ask if any needed)
- YEngine or other script engines
- C-5 (OnSceneObjectPartUpdated removal — already completed)
- M-series audit findings
