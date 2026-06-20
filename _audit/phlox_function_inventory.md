# Phlox LSL Function Inventory

Source files inspected:
- `OpenSim\Addons\Phlox\InWorldz.Phlox\Glue\ISystemAPI.cs` (~736 lines)
- `OpenSim\Addons\Phlox\Phlox.ScriptEngine\LSLSystemAPI.cs` (~12,789 lines)
- `OpenSim\Addons\Phlox\InWorldz.Phlox\Compiler\DefaultConstants.cs` (~865 lines)

## Counts

| Bucket | Count |
|---|---|
| `ll*` declared in ISystemAPI.cs | 491 |
| `iw*` declared in ISystemAPI.cs | 62 |
| `os*` declared in ISystemAPI.cs | 2 |
| `bot*` declared in ISystemAPI.cs | 53 |
| `ll*` IMPLEMENTED (substantive body) | 458 |
| `ll*` STUBBED (strict criteria) | 33 |
| `ll*` MISSING (declared, no body) | 0 |
| Constants in DefaultConstants.cs | 744 |

## STUBBED `ll*` functions (33)

| Function | Pattern |
|---|---|
| llCheckRezError | returns 0, comment "InWorldz Scene.CheckRezError not in OpenSim" |
| llCloseFloater | empty body, comment "Stub for compatibility" |
| llCollisionFilter | empty body, comment "NotImplemented in Halcyon" |
| llCollisionSprite | empty body, comment "NotImplemented in Halcyon" |
| llDetectedDamage | calls `Stub(...)`, returns 0.0f |
| llGetAccel | returns Vector3.Zero |
| llGetCameraAspect | hard-coded 1.7778f |
| llGetCameraFOV | hard-coded 1.0472f |
| llGetFreeMemory | hard-coded 65536 |
| llGetLinkSitFlags | returns 0 |
| llGetMemoryLimit | hard-coded 131072 |
| llGetObjectAnimationNames | empty list (Animesh not supported) |
| llGetOmega | returns Vector3.Zero |
| llGetSPMaxMemory | hard-coded 16384 |
| llGetTorque | returns Vector3.Zero |
| llGodLikeRezObject | empty body |
| llMakeExplosion | empty body, deprecated |
| llMakeFire | empty body, deprecated |
| llMakeFountain | empty body, deprecated |
| llMakeSmoke | empty body, deprecated |
| llMapBeacon | logs only; no viewer-protocol path |
| llPointAt | empty body, deprecated |
| llRefreshPrimURL | empty body, deprecated |
| llReplaceAgentEnvironment | logs only, returns 0 |
| llScriptProfiler | empty body |
| llSetAgentEnvironment | logs only, returns 0 |
| llSetLinkSitFlags | empty body |
| llSetPrimURL | empty body, deprecated |
| llSound | empty body, deprecated |
| llStartObjectAnimation | empty body (Animesh) |
| llStopObjectAnimation | empty body (Animesh) |
| llStopPointAt | empty body, deprecated |
| llTargetedEmail | Stub for non-external target types |

## EXTRA_IN_PHLOX (117) — by design

- OSSL ports (2): `osTeleportAgent`, `osGetAvatarList`
- InWorldz/Halcyon extensions (62 `iw*`): includes `iwActiveGroup`, `iwAvatarName2Key`, `iwClampFloat`, `iwClampInt`, `iwDeliverInventory`, `iwGetAgentList`, etc.
- NPC bot system (53 `bot*`): includes `botCreateBot`, `botFollowAvatar`, `botSay`, `botSensor`, etc.

These are intentional Phlox extensions, not drift from SL.

## MISSING_FROM_PHLOX

Determined by diffing against SL canonical function list (to be filled in by main runner).
