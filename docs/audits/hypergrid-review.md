# Hypergrid (HG) Implementation Review — Legion Grid

**Scope:** Recon and assessment of the Hypergrid stack in this Legion Grid OpenSimulator fork
(OpenSim 0.9.3.0 base + Halcyon/InWorldz Phlox engine), branch `github-snapshot`.
**Driving goal:** Determine whether/how an inbound HG visitor (avatar whose home is a *foreign* grid)
can be limited to a subset of regions/parcels on this grid, while staying fully interoperable with
other HG grids (local policy only, no protocol change).
**Date:** 2026-06-20  **Mode:** Read-only. No source files were modified.

> **Headline finding:** A per-region "off-limits to foreigners" control **already exists, is fully
> wired, and is live in this grid's HG configuration profile** — `[AuthorizationService]
> Region_<Name> = DisallowForeigners`. It keys directly on `IsLocalGridUser` and is enforced on both
> HG arrival and in-grid teleport. The blacklist case ("this region is off-limits to foreigners")
> needs **zero code** — only config. The whitelist case ("foreigners may *only* reach these regions")
> and per-parcel granularity are **not** achievable with the current code/config and would need a
> small, local, protocol-neutral addition. Details below.

---

## 0. Caveats on evidence

- The **reference checkout** at `/d/legion-grid-reference` exists but contains **only `bin/` and config
  files (78 `.cs` files, no `OpenSim/` source tree)** — no `GatekeeperService.cs`,
  `HGEntityTransferModule.cs`, etc. So source-level divergence was assessed **in absolute terms**;
  only the two HG config-include files could be diffed.
- The two diffable config files — `bin/config-include/GridHypergrid.ini` and
  `StandaloneHypergrid.ini` — are **byte-identical to the reference** (stock).
- The repo contains a **nested duplicate source tree** at `opensim-dotnet8-modernization/`. All
  citations in this report refer to the **top-level `OpenSim/` tree**, which is the one the build and
  `GridHypergrid.ini` reference. Where grep returned both, the duplicate was ignored.
- Phlox (`OpenSim/Addons/Phlox/*`) was **not** read, per scope.

---

## 1. Component state table

| Component | Status | Evidence (file:line) | Legion-specific notes |
|---|---|---|---|
| **GatekeeperService** (grid front door) | **Wired**, stock shape | `OpenSim/Services/HypergridService/GatekeeperService.cs:318-597` (`LoginAgent`), admission gates `338-388` (clients/macs), `430-447` (foreign allow/deny), `565-568` (`QueryAccess`) | No Legion divergence detected; classic OpenSim 0.9.x gatekeeper. |
| **GatekeeperServiceInConnector** (HTTP front) | **Wired** | `OpenSim/Region/CoreModules/ServiceConnectorsIn/Hypergrid/HypergridServiceInConnectorModule.cs:128` loads `GatekeeperServiceInConnector`; `/foreignagent` endpoint | Stock. |
| **UserAgentService** (home-grid/outbound) | **Wired** (opposite direction) | `OpenSim/Services/HypergridService/UserAgentService.cs` (754 lines) | Governs *our residents leaving*; **not** an inbound-foreigner gate. See §2.2. |
| **HGEntityTransferModule** (the teleport in) | **Wired** | `.../EntityTransfer/HGEntityTransferModule.cs:214-232` (`GetFinalDestination`), `599` (lure→hyperlink) | Stock HG override; landing resolution for *inbound* lives in Gatekeeper, not here. |
| **EntityTransferModule** (base, in-grid TP) | **Wired** | `.../EntityTransfer/EntityTransferModule.cs:1757-1767`, `2549`, `2608` (all `QueryAccess`) | In-grid teleport runs the same `QueryAccess` gate (carries `homeURI`). |
| **UserManagementModule.IsLocalGridUser** (foreign-vs-local primitive) | **Wired**, the key signal | `.../UserManagement/UserManagementModule.cs:1259-1282` | Foreign = no local `UserAccount` for that UUID (returns `false`). See §3. |
| **AuthorizationService** (per-region flags) | **Wired & live** | `.../ServiceConnectorsOut/Authorization/AuthorizationService.cs:46-129`; connector `LocalAuthorizationServiceConnector.cs:44-122` | **This is the existing per-region foreigner gate.** See §4. |
| **HGInventoryAccessModule** | **Wired** | `.../InventoryAccess/HGInventoryAccessModule.cs:56-57,92-96,154-161,213,524,538` | `RestrictInventoryAccessAbroad` (default `true`), `OutboundPermission` (default `true`); keys on `IsLocalGridUser`. |
| **HGAssetMapper** | **Wired**; fragile push | `.../InventoryAccess/HGAssetMapper.cs:194-217` (`Get`), `219-356` (`Post`) | Synchronous asset push, throws on failure, **no retry/queue**. See §5. |
| **HGSuitcaseInventoryService** | **Wired** | `OpenSim/Services/HypergridService/HGSuitcaseInventoryService.cs:44-51`, `249,269,333-342,399-401,579-604` | Confines a visiting/abroad user to the Suitcase folder tree; deletes are no-ops. |
| **HGAssetService** | **Wired** | `OpenSim/Services/HypergridService/HGAssetService.cs:110-126,173-192`; `OpenSim/Framework/AssetPermissions.cs:12-84` | `DisallowExport`/`DisallowImport` per AssetType gates. |
| **HypergridLinker** | **Wired** | `OpenSim/Services/GridService/HypergridLinker.cs:120-135` (console cmds), `500` (`Hyperlink|NoDirectLogin` flags), `331-335` (self-grid guard) | Stock. No per-region inbound restriction here. |
| **HGWorldMapModule** | **Wired**, foreigner-aware | `.../World/WorldMap/HGWorldMapModule.cs:177-185` | Sends `map-server-url` only to foreigners (`!IsLocalGridUser`). Cosmetic, not access. |
| **HGLureModule** | **Wired** | `.../Avatar/Lure/HGLureModule.cs:232-250` | Routes cross-grid lures through Gatekeeper; no per-region gating of its own. |
| `OpenSim/Region/CoreModules/Hypergrid/` | **Absent** | — | No such directory; HG modules are scattered (Lure, WorldMap, EntityTransfer, ServiceConnectorsIn). |

---

## 2. Gatekeeper admission path (the front door)

### 2.1 `LoginAgent` — what actually gates an inbound foreigner

`GatekeeperService.LoginAgent(...)` — `GatekeeperService.cs:318-597`. In order:

1. **Client/MAC/ID0 filters** — `AllowedClients`/`DeniedClients` regex (`338-366`), `DeniedMacs` (`368-377`),
   `DeniedID0s` (`379-388`). Grid-wide, viewer-based, **not** foreign-specific.
2. **Authenticate** — verifies the service token was minted for *this* grid and calls back the visitor's
   home `UserAgentService.VerifyAgent` (`393-398`, impl `599-639`, `CheckAddress` `643-663`).
3. **Impersonation guard** — if a *local* `UserAccount` exists for the incoming UUID, the agent must be a
   resident "coming home", else refused (`404-425`).
4. **Foreign-agents gate** — `430-447`: `bool allowed = m_ForeignAgentsAllowed;` with per-home-URI
   exception lists (`AllowExcept` / `DisallowExcept`, parsed `211-223`, matched in `IsException` `670-689`).
   This is the **all-or-nothing front-door lever** — grid-wide, keyed on the visitor's *home URI*, not on
   destination region.
5. **Ban service** (`454-459`), **dup-presence** kill (`469-492`).
6. **Per-region access check** — `565-568`:
   ```
   m_SimulationService.QueryAccess(destination, aCircuit.AgentID,
       aCircuit.ServiceURLs["HomeURI"].ToString(), true, aCircuit.startpos, ..., out reason)
   ```
   **This is the pivotal call.** It forwards the visitor's UUID *and* HomeURI into the destination
   region's `Scene.QueryAccess`, which is where per-region policy is (and can be) enforced. See §4.

### 2.2 Landing-region resolution

- `AllowTeleportsToAnyRegion` (read `GatekeeperService.cs:104`, default **`true`**) controls whether an
  inbound HG teleport may target an arbitrary region or is forced to the default gateway region:
  - `GetHyperlinkRegion` (`275-315`): if `false`, **ignores the requested regionID** and returns
    `m_DefaultGatewayRegion` ("Teleporting to the default region.").
  - `LinkLocalRegion` (`225-273`): same fallback to the first `GetDefaultHypergridRegions` entry.
- So today the only landing controls are binary: **(a)** admit-or-refuse at the gate
  (`ForeignAgentsAllowed`), and **(b)** force-all-to-default-region (`AllowTeleportsToAnyRegion=false`).
  There is **no built-in "foreigners may land only in regions X, Y, Z"** list here.

### 2.3 UserAgentService (outbound, noted briefly)

`UserAgentService.cs` governs **our own residents leaving** to foreign grids
(`ForeignTripsAllowed_Level_<N>`, `DisallowExcept_Level_*`, `AllowExcept_Level_*`,
`LevelOutsideContacts`). It is the *opposite direction* and has **no inbound-foreigner gating**.
Flag only: it is the counterpart a *remote* grid would use to stop its users coming here, which is not
under our control and not relevant to local inbound policy.

---

## 3. Foreign-vs-local primitive — `IsLocalGridUser`

`UserManagementModule.IsLocalGridUser(UUID uuid)` — `UserManagementModule.cs:1259-1282`:

- No scenes yet → `true` (`1261-1262`).
- Cached `UserData` with `HasGridUserTried` → returns its `.IsLocal` (`1264-1268`).
- No account service → `true` (`1270-1271`).
- Otherwise `GetUserAccount(scope, uuid)`: **`account == null` → `false` (foreign)**, else `true`
  (`1273-1281`).

**Determination of "foreign" = the UUID has no local `UserAccount` on this grid.** This is the single
signal every access-policy hook keys on, and it is already consulted by `AuthorizationService`,
`HGInventoryAccessModule`, and `HGWorldMapModule`. It requires only the avatar UUID — which every
admission/teleport hook already has.

---

## 4. The existing per-region foreigner gate (DisallowForeigners) — fully traced

This is the most important result of the review. The chain is **live in production's HG profile**:

```
GatekeeperService.LoginAgent                         GatekeeperService.cs:565
  └─ SimulationService.QueryAccess(dest, agentID, HomeURI, ...)
       └─ Scene.QueryAccess(agentID, agentHomeURI, ...)   Scene.cs:6063-6104
            └─ Scene.AuthorizeUser(aCircuit, false, ...)  Scene.cs:6087 → 4431-4540
                 └─ AuthorizationService.IsAuthorizedForRegion(...)  Scene.cs:4444
                      └─ if DisallowForeigners && !IsLocalGridUser(uuid) → DENY
                                                       AuthorizationService.cs:109-116
```

- **The flag & enforcement:** `AuthorizationService.cs:46-51` defines
  `AccessFlags { None, DisallowResidents=1, DisallowForeigners=2 }`. The constructor reads
  `Region_<RegionName> = <flag>` from the `[AuthorizationService]` config section
  (`AuthorizationService.cs:72-84`). `IsAuthorizedForRegion` then enforces:
  - `DisallowForeigners` → `!m_UserManagement.IsLocalGridUser(userID)` ⇒ "No foreign users allowed in
    this region" (`109-116`).
  - `DisallowResidents` → only Gods/Admins (`118-125`).
- **The wiring is active:** `GridHypergrid.ini:20` sets
  `AuthorizationServices = "LocalAuthorizationServicesConnector"`. That connector
  (`LocalAuthorizationServiceConnector.cs:44`, `INonSharedRegionModule` = per-region) enables itself when
  that module name matches (`64-78`), registers as the scene's `IAuthorizationService` (`89-96`),
  instantiates `new AuthorizationService(m_AuthorizationConfig, scene)` per region (`107`), and
  **delegates** `IsAuthorizedForRegion` to it (`114-122`). Not a stub.
- **The gate is on by default:** `Scene.m_strictAccessControl = true` (`Scene.cs:282`), overridable via
  `[Startup] StrictAccessControl` (`Scene.cs:1013`). `AuthorizeUser` short-circuits to allow only if
  `StrictAccessControl=false` (`Scene.cs:4437-4438`).
- **It also covers in-grid teleports:** the same `QueryAccess` runs on local teleports
  (`EntityTransferModule.cs:1757-1767`, carrying `homeURI`) and crossings (`2549`, `2608`). So a foreigner
  who landed at an allowed region and then tries to teleport into a `DisallowForeigners` region is
  **also refused** by the identical check.

**Config to use it today (no code change):**
```ini
[AuthorizationService]
    Region_Staff_Island   = "DisallowForeigners"   ; HG visitors refused; teleport fails with reason
    Region_Admin_Sandbox  = "DisallowResidents"    ; only gods/managers
```
Documented at `GridCommon.ini.example:224-232`. (Default for unlisted regions = `None` = open.)

---

## 5. Overall HG health

**Works end-to-end:** Inbound admission (auth callback, impersonation guard, foreign gate, ban, dup
presence), per-region authorization (`DisallowForeigners`/`DisallowResidents`), inventory isolation for
abroad/visiting users (Suitcase tree, `RestrictInventoryAccessAbroad` default `true`), asset
export/import gating (`DisallowExport`/`DisallowImport`), region linking + HG map tiles, and HG lures.
No stubbed or dormant components were found in the inbound path; the stack is coherent stock-0.9.x HG.

**Fragile / risky:**
- **Serial, no-retry asset push** — `HGAssetMapper.Post` (`HGAssetMapper.cs:219-356`) walks dependent
  assets and posts them **synchronously**, wrapping each in try/catch and **throwing on failure with no
  retry or background queue** (`284-304`); `Get` marks the whole pull failed if any dependency fails
  (`HGAssetMapper.cs:209`). On a slow/flaky foreign asset server this can stall or partially transfer a
  teleport's assets. This is stock OpenSim behavior, not a Legion regression, but it is the most likely
  real-world failure mode.
- **`AllowTeleportsToAnyRegion=true`** (the effective default) means inbound foreigners can target any
  region; the only landing constraint today is the per-region `DisallowForeigners` blacklist.
- **`QueryAccess` ignores `agentHomeURI`** today (`Scene.cs:6063` receives it but the body never reads
  it) — foreignness is recomputed via `IsLocalGridUser(UUID)` instead. Not a bug, but note the HomeURI
  *is* already plumbed to the region if a future policy wanted to key on the specific origin grid.

**No Legion-specific divergence** was detected in any inbound HG access component; the fork's HG layer
tracks stock OpenSim. (The Halcyon/Phlox divergence is in the script engine, out of scope here.)

---

## 6. Access-control layer map (for the goal)

| Layer | Exists? | What it enforces today | Foreign-aware? | Evidence |
|---|---|---|---|---|
| **(a) Gatekeeper admission** | Yes | Grid-wide admit/refuse of foreigners (`ForeignAgentsAllowed` + `AllowExcept`/`DisallowExcept` per home-URI). Client/MAC/ID0 filters. | Yes — keyed on visitor **home URI** | `GatekeeperService.cs:430-447,670-689` |
| **(b) Landing-region resolution** | Partial | `AllowTeleportsToAnyRegion=false` forces *all* inbound to the single default gateway region. Otherwise any region. No per-region/allow-list. | No (binary, not list) | `GatekeeperService.cs:104,275-315` |
| **(c) Per-region authorization (arrival + in-grid TP)** | **Yes** | `DisallowForeigners` (block foreigners) / `DisallowResidents` (gods only), per region, by name. Enforced on arrival **and** subsequent in-grid teleports/crossings. | **Yes — `IsLocalGridUser`** | `AuthorizationService.cs:109-116`; `Scene.cs:4444,6087`; `EntityTransferModule.cs:1757` |
| **(d) Estate** | Yes | Estate ban list, access list, group access, public-access flag, deny-minors/anonymous. | **No** (UUID only; not foreign-aware) | `Scene.cs:4467-4536`; `EstateSettings.cs` IsBanned/HasAccess |
| **(e) Parcel** | Yes | Parcel ban/access lists, deny-minors/anonymous, telehub landing checks; runtime eject. | **No** (UUID only; not foreign-aware) | `LandObject.cs` IsBanned/IsRestricted/IsEitherBannedOrRestricted; `LandManagementModule.cs` EnforceBans; `Scene.cs:6107-6234` CheckLandPositionAccess |

So foreign-vs-local is **already distinguishable** at layer (c) and is **already used** there. Layers (d)
and (e) have the avatar UUID in hand and could *call* `IsLocalGridUser`, but currently do **not**.

---

## 7. Gap analysis — what config alone CANNOT do today

Achievable **today, config only**:
- ✅ "This region is off-limits to foreigners" — per region, by name: `Region_<Name> = DisallowForeigners`
  (`AuthorizationService.cs:109-116`). Works on arrival and in-grid teleport.
- ✅ "Foreigners off this grid entirely" — `ForeignAgentsAllowed = false` (`GatekeeperService.cs:430-447`).
- ✅ "Allow/deny foreigners from specific *grids*" — `AllowExcept`/`DisallowExcept` by home URI
  (`GatekeeperService.cs:434-438`).
- ✅ "All foreigners land only at the default region" — `AllowTeleportsToAnyRegion = false`
  (`GatekeeperService.cs:279-292`).

**Not** achievable with config alone:
- ❌ **Whitelist semantics** ("foreigners may *only* reach regions A, B, C"). The only per-region lever is
  a **blacklist** (`DisallowForeigners` marks a region as *off-limits*). To express a whitelist you must
  manually flag *every other region* as `DisallowForeigners`, and re-flag each newly added region — error
  prone and unmaintainable at scale.
- ❌ **A single allow/deny *region list*** for foreigners (one setting naming the permitted set). No such
  setting exists; the data model is one flag per region.
- ❌ **Per-parcel** foreigner restriction. Parcel access (`LandObject.cs`) is UUID-list based and has **no
  foreign-vs-local notion**; nothing reads `IsLocalGridUser` at the parcel layer.
- ❌ **"Bounce to a landing region"** for a denied foreigner. `DisallowForeigners` **refuses** at
  `QueryAccess` (teleport fails with a message); it does not redirect. `AllowTeleportsToAnyRegion=false`
  redirects, but it does so for *everyone* and only to the *single default* region, not per-policy.

**Bottom line:** the built-in levers are "all-or-nothing at the front door" + "land only at default" +
**a per-region foreigner *blacklist***. The blacklist covers "make region X off-limits" perfectly. It
does **not** cover "restrict foreigners to a small allowed subset" cleanly, nor parcel granularity, nor
redirect-instead-of-refuse.

---

## 8. Recommended approaches (ranked, least-invasive first)

> All options below are **local policy**: they live entirely inside this grid's region authorization and
> key on `IsLocalGridUser(UUID)`. They change *who this grid admits to which of its own regions* — they do
> **not** alter any HG wire protocol, gatekeeper handshake, or service URL. Other HG grids see only normal
> "access denied"/"teleport failed" responses they already handle. **Full interoperability is preserved.**

### Option A — Use what exists: per-region `DisallowForeigners` (zero code)
- **Hook:** none — config only. `[AuthorizationService] Region_<Name> = DisallowForeigners`.
- **Keys on foreignness:** `AuthorizationService.IsAuthorizedForRegion` → `IsLocalGridUser`
  (`AuthorizationService.cs:111`).
- **Best when:** the set of *forbidden* regions is small (a few sensitive regions). Already enforced on
  arrival and in-grid teleport. **Recommend John start here** to confirm the behavior end-to-end.
- **Limit:** blacklist only; refuses (no redirect); region-level only.

### Option B — Add whitelist/allow-list semantics inside `AuthorizationService` (small, local)
- **Exact hook point:** `AuthorizationService` (`OpenSim/Region/CoreModules/ServiceConnectorsOut/
  Authorization/AuthorizationService.cs`), constructor `64-87` and `IsAuthorizedForRegion` `89-129`.
- **Shape (spec, not implemented):** add a new flag, e.g. `ForeignersAllowedOnlyHere`, plus a grid-wide
  default mode read from `[AuthorizationService]` (e.g. `ForeignersDefault = Deny`). When the default is
  Deny, `IsAuthorizedForRegion` denies any `!IsLocalGridUser` region that is **not** explicitly marked
  allowed — i.e. flip the per-region semantics from blacklist to whitelist without touching any other
  file. Because everything stays inside this one already-wired module, the entire `QueryAccess` →
  `AuthorizeUser` chain (arrival + in-grid TP) inherits it for free.
- **Keys on foreignness:** same `m_UserManagement.IsLocalGridUser` already held by the module
  (`AuthorizationService.cs:57,111`).
- **Config model:** per-region flags + one grid-wide default; lives in the existing
  `[AuthorizationService]` section. No new service, no DB.
- **Backward-compat:** default `ForeignersDefault = Allow` reproduces today's behavior exactly; a denied
  foreigner gets the existing "not allowed in this region" refusal other grids already handle.
- **Recommend as the primary build** if John wants true "foreigners only in this subset".

### Option C — Redirect-instead-of-refuse (bounce to a landing region)
- **Exact hook point:** the inbound landing decision in `GatekeeperService.GetHyperlinkRegion`
  (`GatekeeperService.cs:275-315`) and/or `LinkLocalRegion` (`225-273`), which already own the
  default-region fallback. A foreigner-aware variant could, when policy denies the requested region,
  return a **designated foreigner landing region** instead of `null`/refuse.
- **Keys on foreignness:** Gatekeeper has the visitor's HomeURI directly (`aCircuit.ServiceURLs
  ["HomeURI"]`); for UUID-based local-account testing it would consult `IUserManagement`/account service.
- **Config model:** a `ForeignerLandingRegion` setting in `[GatekeeperService]` + the allow-list from
  Option B.
- **Backward-compat:** preserved — redirect uses the existing HG teleport response; the visitor simply
  arrives at the landing region. More invasive than B because it touches the gatekeeper landing path;
  recommend only if "bounce, don't refuse" is a hard requirement.
- **Note:** this is in addition to, not instead of, B — B decides *deny*, C decides *what to do on deny*.

### Option D — Per-parcel foreigner restriction (most invasive; only if needed)
- **Exact hook point:** `LandObject.IsRestrictedFromLand_inner` /
  `IsEitherBannedOrRestricted` (`OpenSim/Region/CoreModules/World/Land/LandObject.cs`) and
  `LandManagementModule.EnforceBans` (`LandManagementModule.cs`), reached via
  `Scene.CheckLandPositionAccess` (`Scene.cs:6107-6234`).
- **Shape (spec):** add a parcel flag/policy "no foreigners" and have these methods consult
  `IUserManagement.IsLocalGridUser(avatar)` (they already have the avatar UUID). Runtime eject is already
  implemented in `EnforceBans`, so the enforcement machinery exists.
- **Config model:** parcel-level (per `LandData`), not `.ini` — would need a UI/estate-tool or DB flag.
- **Backward-compat:** preserved (local land policy). But this is the largest change: it spans the land
  module, parcel data model, and possibly viewer parcel UI. Recommend only if region granularity (B) is
  insufficient.

**Ranking:** **A (config now)** → **B (whitelist in AuthorizationService)** → **C (redirect)** →
**D (parcel)**. B is the sweet spot for "HG visitors may only reach these regions" with minimal, local,
protocol-neutral code in a module that is already wired into both arrival and in-grid teleport.

---

## 9. Open questions for John

1. **Granularity:** Is **region-level** sufficient (Options A/B), or do you genuinely need **per-parcel**
   foreigner control (Option D, much larger)? Most "visitor sandbox" goals are satisfied at region level.
2. **Blacklist vs whitelist:** Do you want to *name the forbidden* regions (today's `DisallowForeigners`,
   Option A) or *name the permitted* regions and deny by default (Option B)? The latter is safer as the
   grid grows but needs the small code change.
3. **Deny vs redirect:** When a foreigner targets a forbidden region, should they be **refused** (teleport
   fails with a message — today's behavior) or **bounced to a designated landing region** (Option C)?
   Refuse is free; redirect is extra work in the gatekeeper.
4. **Interaction with estate bans:** `DisallowForeigners` is evaluated **before** estate checks in
   `AuthorizeUser` (`Scene.cs:4444` runs ahead of the estate ban block at `4469`). Confirm the intended
   precedence: should a foreigner-allow ever override an estate ban (no, presumably), and should estate
   *managers* who are foreign be exempt? (Today gods/admins bypass at `Scene.cs:4439`; estate managers do
   **not** bypass `IsAuthorizedForRegion`.)
5. **Origin-grid granularity:** Do you want "all foreigners" uniformly, or "foreigners *except* from
   trusted grids X, Y"? The HomeURI is available at the gate (`AllowExcept`/`DisallowExcept`) and could be
   threaded into a region-level policy, but that adds a second key (HomeURI) alongside `IsLocalGridUser`.
6. **`AllowTeleportsToAnyRegion`:** Currently effectively `true`. Do you want to keep arbitrary-region
   inbound teleports (relying on per-region `DisallowForeigners`/Option B to gate), or set it `false` so
   *all* foreigners funnel through one default region first? The two strategies compose but overlap.

---

*End of report. No source files were modified during this review.*
