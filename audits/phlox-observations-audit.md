# Phlox Observations Audit
**Date:** 2026-05-26  
**Scope:** Recon only — no code changes. Three deferred observations from the Phlox event-wiring audit series.

---

## Reference Verification

Halcyon reference at `/mnt/desktop/d-drive/halcyon-reference-fresh/` confirmed real:
- Copyright header: `Copyright (c) InWorldz Halcyon Developers`
- Used below for historical comparison. All reads are read-only.

---

## Observation 1: `OnScriptRemoved` / `OnObjectRemoved` — Declared, Never Fired

### Q1 — What is the interface contract?

`IScriptModule` (`OpenSim/Region/Framework/Interfaces/IScriptModule.cs`, lines 50 and 55) declares both events as required interface members:

```csharp
event ScriptRemoved OnScriptRemoved;   // line 50
event ObjectRemoved OnObjectRemoved;   // line 55
```

Any class implementing `IScriptModule` must declare these. PhloxEngine does declare them (lines 562–563), satisfying the interface. But declaring ≠ firing.

### Q2 — Who subscribes, and what do they need?

Two modules subscribe in their `AddRegion` / initialization path:

**UrlModule** (`OpenSim/Region/CoreModules/Scripting/LSLHttp/UrlModule.cs`, lines 205–206):
```csharp
scriptModule.OnScriptRemoved += ScriptRemoved;   // cleans up per-script llSetContentType URLs
scriptModule.OnObjectRemoved += ObjectRemoved;   // cleans up all URLs for an object UUID
```
- `ScriptRemoved(UUID itemID)` — removes any URL registered by that specific script item
- `ObjectRemoved(UUID objectID)` — removes all URLs whose `hostID` matches the object UUID (lines 492–510)

**XmlRpcGridRouterModule** (`OpenSim/Region/OptionalModules/Scripting/XmlRpcRouterModule/XmlRpcGridRouterModule.cs`, lines 95–96):
```csharp
scriptEngine.OnScriptRemoved += this.ScriptRemoved;
scriptEngine.OnObjectRemoved += this.ObjectRemoved;
```
Cleans up registered xmlrpc callback handles.

### Q3 — What does Phlox actually do?

`PhloxEngine.cs` lines 562–563:
```csharp
public event ScriptRemoved OnScriptRemoved;   // declared — never fired
public event ObjectRemoved OnObjectRemoved;   // declared — never fired
```

Both are CS0067 warnings ("event is declared but never used"). Neither is ever invoked anywhere in the Phlox codebase.

**Practical effect:** When a Phlox script is deleted or its host object is removed from the region, `UrlModule` never receives notification. Any URL registered via `llHTTPServer` or `llSetContentType` accumulates and is never cleaned up for the lifetime of the region. XmlRpc callback handles also leak.

### Q4 — What does YEngine do?

`YEngine/XMREngine.cs`:
- Declares both events (lines 893–894)
- **Fires `OnScriptRemoved`** at line 1527 inside its `OnRemoveScript` handler:
  ```csharp
  OnScriptRemoved?.Invoke(itemID);
  ```
- **Never fires `OnObjectRemoved`** — only the declaration at line 894. YEngine has the same `OnObjectRemoved` gap.
- Also calls `urlModule?.ScriptRemoved(itemID)` directly at line 1076 as a belt-and-suspenders path.

### Q5 — What does Halcyon do?

Halcyon's `IScriptModule` does not declare these events at all. Halcyon Phlox handles URL cleanup by calling `m_UrlModule.ScriptRemoved(m_itemID)` directly from `LSLSystemAPI.cs` (lines 213 and 253) at script teardown time — a direct call rather than an event. The event-based pattern is an OpenSim addition that postdates the Halcyon port.

### Fix scope

**Priority: High.** `OnScriptRemoved` must fire when a script item is deleted. The correct hook point is inside `PhloxEngine`'s script removal path — the equivalent of YEngine's `OnRemoveScript` handler. `OnObjectRemoved` needs a hook when the host object leaves the region (likely `OnCleanUpScene` or object delete path). Both are Phlox-only changes.

---

## Observation 2: `CHANGED_*` Constant Numbering vs SL Spec

### Q1 — What does SL spec define?

Standard SL `CHANGED_*` constants (from LSL wiki, same as OpenSim's `LSL_Constants.cs`):

| Constant | Value |
|---|---|
| CHANGED_INVENTORY | 1 |
| CHANGED_COLOR | 2 |
| CHANGED_SHAPE | 4 |
| CHANGED_SCALE | 8 |
| CHANGED_TEXTURE | 16 |
| CHANGED_LINK | 32 |
| CHANGED_ALLOWED_DROP | 64 |
| CHANGED_OWNER | 128 |
| CHANGED_REGION | 256 |
| CHANGED_TELEPORT | 512 |
| CHANGED_REGION_START | 1024 |
| CHANGED_MEDIA | 2048 |

The SL spec stops at 2048. Values 4096, 16384, and 32768 do not appear in the official LSL spec.

### Q2 — What does OpenSim add?

OpenSim's `LSL_Constants.cs` adds two platform-specific extensions, marked with `//ApiDesc opensim specific`:

```csharp
public const int CHANGED_ANIMATION = 16384;   // line 383 — opensim specific
public const int CHANGED_POSITION  = 32768;   // line 385 — opensim specific
```

The internal `SceneObjectPart.Changed` enum (`SceneObjectPart.cs`, lines 52–69) mirrors these:

```csharp
public enum Changed : uint
{
    INVENTORY     = 1,
    COLOR         = 2,
    SHAPE         = 4,
    SCALE         = 8,
    TEXTURE       = 16,
    LINK          = 32,
    ALLOWED_DROP  = 64,
    OWNER         = 128,
    REGION        = 256,
    TELEPORT      = 512,
    REGION_RESTART = 1024,
    MEDIA         = 2048,
    MATERIAL      = 4096,     // no CHANGED_MATERIAL in any constants file
    ANIMATION     = 16384,
    POSITION      = 32768
}
```

`Changed.MATERIAL` (4096) is used internally but has no exposed `CHANGED_MATERIAL` LSL constant in either OpenSim or SL.

### Q3 — What does Phlox's `DefaultConstants.cs` have?

Phlox `DefaultConstants.cs` (lines 290–303):

| Constant | Value | Present in SL? | Present in OpenSim LSL_Constants? |
|---|---|---|---|
| CHANGED_INVENTORY | 1 | ✓ | ✓ |
| CHANGED_COLOR | 2 | ✓ | ✓ |
| CHANGED_SHAPE | 4 | ✓ | ✓ |
| CHANGED_SCALE | 8 | ✓ | ✓ |
| CHANGED_TEXTURE | 16 | ✓ | ✓ |
| CHANGED_LINK | 32 | ✓ | ✓ |
| CHANGED_ALLOWED_DROP | 64 | ✓ | ✓ |
| CHANGED_OWNER | 128 | ✓ | ✓ |
| CHANGED_REGION | 256 | ✓ | ✓ |
| CHANGED_TELEPORT | 512 | ✓ | ✓ |
| CHANGED_REGION_START | 1024 | ✓ | ✓ |
| CHANGED_REGION_RESTART | 1024 | alias | alias |
| CHANGED_MEDIA | 2048 | ✓ | ✓ |
| CHANGED_ANIMATION | 16384 | ✗ | ✓ (opensim ext) |
| **CHANGED_POSITION** | **32768** | **✗** | **✓ — MISSING from Phlox** |

Phlox includes `CHANGED_ANIMATION` (inherited from Halcyon) but is missing `CHANGED_POSITION`.

### Q4 — What is 32768 (`CHANGED_POSITION`)?

`Changed.POSITION = 32768` fires via `TriggerScriptChangedEvent(Changed.POSITION)` in the `AbsolutePosition` setter of `SceneObjectPart.cs` (line 855). It fires whenever the physics engine or scripted movement updates the position of a part. This is entirely an OpenSim-specific extension with no SL equivalent.

`YEngine/XMRInstRun.cs` line 110 treats it as ignorable in certain contexts:
```csharp
const int canignore = ~(CHANGED_SCALE | CHANGED_POSITION);
```

### Q5 — What does Halcyon do?

Halcyon `LSL_Constants.cs` (lines 303–316) has the same set as Phlox DefaultConstants — including `CHANGED_ANIMATION = 16384`, but **no `CHANGED_POSITION`**. So Halcyon Phlox also lacked this constant. The gap was introduced when OpenSim added `CHANGED_POSITION` as a platform extension but the Phlox compiler's constant table was never updated to match.

### Fix scope

**Priority: Low.** Add one entry to `DefaultConstants.cs`:
```csharp
{"CHANGED_POSITION", new ConstantSymbol("CHANGED_POSITION", SymbolTable.INT, "32768")},
```
Scripts can already receive `changed(32768)` events — they just can't reference the named constant in Phlox-compiled LSL. Phlox-only change, no runtime behavior change.

---

## Observation 3: `CHANGED_POSITION` (32768) in Relation to `STATUS_PHYSICS`

### Q1 — Does toggling STATUS_PHYSICS fire a `changed` event?

The SL LSL wiki does **not** document a `CHANGED_*` constant for STATUS_PHYSICS changes. No standard constant exists for "physics flag was toggled." Scripts that want to react to STATUS_PHYSICS changes conventionally poll `llGetStatus(STATUS_PHYSICS)` inside `changed()` when they receive any `CHANGED_*` bit, or use other mechanisms.

### Q2 — What fires when `llSetStatus(STATUS_PHYSICS, ...)` is called?

Call chain (`LSL_Api.cs` lines 1508–1541 → `SceneObjectPart.ScriptSetPhysicsStatus` line 3272 → `SceneObjectGroup.ScriptSetPhysicsStatus` line 2604 → `UpdateFlags` line 3898 → `UpdatePrimFlags` per-part):

```
llSetStatus(STATUS_PHYSICS)
  → ScriptSetPhysicsStatus(bool)        [SceneObjectPart.cs:3272]
  → SceneObjectGroup.ScriptSetPhysicsStatus [line 2604]
  → UpdateFlags(usePhysics, ...)        [line 3898]
  → UpdatePrimFlags(...)                [per-part, line 4638]
```

`UpdatePrimFlags` (lines 4638–4737) sets `PrimFlags.Physics`, adds/removes the part from the physics scene, then calls `ScheduleFullUpdate()`. It does **not** call `TriggerScriptChangedEvent(...)` at any point.

### Q3 — When does `Changed.POSITION` (32768) actually fire?

`TriggerScriptChangedEvent(Changed.POSITION)` is called from the `AbsolutePosition` setter of `SceneObjectPart.cs` at line 855. This fires each time the physics engine pushes a new position for the part — which happens every physics frame for a moving physical object.

When STATUS_PHYSICS is *enabled*: the object enters physics simulation and starts receiving position updates → `Changed.POSITION` fires indirectly and continuously while the object moves.

When STATUS_PHYSICS is *disabled*: position updates stop → `Changed.POSITION` stops firing.

There is no single `Changed.POSITION` fire that represents "physics was toggled" — it fires as a side effect of movement, not of the flag change itself.

### Q4 — Is there a gap for Phlox scripts?

Two gaps:

1. **Named constant missing:** Phlox scripts cannot write `CHANGED_POSITION` — the name doesn't exist in `DefaultConstants.cs`. A Phlox script checking `(change & CHANGED_POSITION) != 0` won't compile; it must use the raw integer 32768. YEngine scripts can use the name freely.

2. **No "physics toggled" notification:** Neither Phlox nor YEngine fires a `changed` event specifically for STATUS_PHYSICS state changes. This matches SL behavior (no such constant exists in spec), so this is not a bug — it is spec-correct.

### Q5 — What does Halcyon do?

Same behavior. Halcyon does not define `CHANGED_POSITION` and does not fire a changed event on STATUS_PHYSICS toggle. This is consistent with SL.

### Fix scope

**Priority: Low** (same fix as Observation 2). Adding `CHANGED_POSITION` to Phlox `DefaultConstants.cs` resolves the named-constant gap. No behavior change needed — the event fires correctly, just without the named constant available in the compiler.

---

## Cross-Cutting Findings

| # | Issue | Severity | Fix location | Standalone? |
|---|---|---|---|---|
| 1a | `OnScriptRemoved` never fired — URL leaks | High | PhloxEngine.cs (script removal path) | Yes |
| 1b | `OnObjectRemoved` never fired — URL leaks | Medium | PhloxEngine.cs (object removal path) | Yes |
| 2 | `CHANGED_POSITION` missing from DefaultConstants | Low | DefaultConstants.cs (1 line) | Yes |
| 3 | Same root cause as #2, no new action | — | — | — |

**Note:** YEngine also never fires `OnObjectRemoved` (only `OnScriptRemoved`). So the `ObjectRemoved` leak exists in both engines. Worth investigating whether UrlModule cleanup is actually adequate with only `ScriptRemoved` (which fires per script item, not per object), or if `ObjectRemoved` is truly needed for objects with no scripts (bare objects that somehow have URLs).

---

## Recommended Implementation Sequence

1. **CHANGED_POSITION constant** — 1-line addition to `DefaultConstants.cs`. Safe, no runtime risk, zero behavior change. Do first.

2. **OnScriptRemoved** — Add firing in the PhloxEngine script removal handler. Pattern from YEngine: `OnScriptRemoved?.Invoke(itemID)`. Identify the correct removal hook point before implementing (requires a quick recon of PhloxEngine's `OnRemoveScript` / `RemoveScript` path).

3. **OnObjectRemoved** — Requires identifying what "object removed" means in the Phlox engine's lifecycle. Likely the `OnObjectRemoved` EventManager event or the scene object delete path. Do after `OnScriptRemoved` is verified working.
