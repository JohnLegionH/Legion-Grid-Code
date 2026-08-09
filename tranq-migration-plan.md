# Legion Grid → Tranquillity — Data & Grid Migration Plan (runbook)

> ⚠️ **PROVENANCE FLAG (read first):** This file **did not exist** when Phase 0 ran on
> 2026-08-09 — the task said "amend `tranq-migration-plan.md`" but no such file was present
> anywhere under `/d` (only `docs/sdk-migration-plan.md`, which is the *separate* SDK/csproj
> build-conversion plan, phases 0–7, unrelated to this data migration). This document was
> therefore **created fresh** and populated with the Phase-0 content specified in the task.
> The phase **numbering** here (0, 3, 4, 8) is a scaffold matching the task's references; phases
> 1–2 and 5–7 are stubs pending John's canonical runbook. Companion recon:
> `tranq-rc-migration-recon.md` (same directory).

Source of code deltas & migration hazards: **`tranq-rc-migration-recon.md`** (§1–§8).

---

## Phase 0 — Freeze & export

**Source database: confirmed `opensim`.** All active `ConnectionString`s in the live bin
(`D:\opensim - Use this december 2025\bin\`) point at `opensim`. The four `opensim_test*`
schemas are unreferenced leftovers. `legion_market` is the separate marketplace web DB.

**Environment facts:**
- MySQL runs in Docker container **`opensim_mysql`** (image `mysql:8.0`, Linux) → **table names are
  CASE-SENSITIVE**. Use exact case everywhere (`UserAccounts`, not `useraccounts`).
- Live bin: `D:\opensim - Use this december 2025\bin\`.
- **RegionStore is never imported** — regions are rebuilt from OARs (Phase 3.3). The §5 RegionStore
  migration hazard is **deleted from scope**; do not script or plan any RegionStore fix.

### 0.A credentials — do NOT carry forward
When copying config to the new tree, **exclude** these — both contain live root DB credentials:
- `GridCommon - Copy.ini`
- `GridCommon.ini.bak-killswitch`

### IAR target accounts (Phase for per-user inventory export/import)
| Avatar | UUID |
|---|---|
| Legion Hienrichs | `4fbdfd2a-e0c6-4003-b2f8-8714fcc7b968` |
| Truly Bazar | `a7d2ff2e-dc32-44d8-aa61-3d22070a4964` |
| Tiana Mcminnar | `0f62cf39-71b8-49e1-94ea-ebdf54be01e2` |
| Creatively Bazar | `31565b78-cee3-4820-9e4b-bd327276d582` — ⚠️ **confirm with John** |

---

## Phase 3 — Build & run the new tree

### 3.3 — Rebuild regions from `Regions.ini` (manual, unchanged)
Regions come from OARs; the region definitions below are rebuilt exactly (from the live bin's
`bin/Regions/Regions.ini` + `bin/Regions/Region_Elm.ini`, read 2026-08-09):

| Region | UUID | Size X×Y | Location | Internal port | Notes |
|---|---|---|---|---|---|
| **Ebony** | `c44606b1-43e1-45fb-8ae8-201545dc2f6a` | 256×256 (default) | 1000,1000 | 9000 | Estate; ExternalHost `legiongrid.ddns.net`; MaxPrims 45000 |
| **Transylvania** | `0e99ab97-d710-4714-9230-ddd50b722000` | 256×256 (default) | 1000,1001 | 9001 | Estate; MaxPrims 45000 |
| **Elm** | `806332b8-7294-4101-842d-e6e2d5385e55` | 1024×1024 (varregion) | 1000,1003 | 9002 | Estate "Bazar Creations"; MaxPrims 100000, MaxAgents 100 |

(Ebony/Transylvania omit `SizeX/Y` → default 256. Elm sets 1024×1024. All three match live DB `regions`=3 and the `script_experiences.region_id` values.)

### 3.x — Watch-items
- **`XCallingCardModule` → `CallingCardModule` rename** (recon §8, Friends): upstream renamed the
  module. Grep the new tree's INI/config for any `XCallingCardModule` reference and update it, or the
  module won't load under its old name.
- **`Old Guids=true` re-derivation:** the live connection strings carry `Old Guids=true`. On the new
  MySqlConnector / EF-style data layer this flag may behave differently (same *family* of issue as the
  `GuidFormat=None` fix). Verify GUID round-tripping on the new data layer before trusting imports;
  re-derive the correct connection-string GUID option for MySqlConnector.

---

## Phase 4 — DB table import (source `opensim`, case-sensitive)

Generated from `information_schema.TABLES` against `opensim`, 2026-08-09. **Exact case preserved.**
**`migrations` is explicitly excluded** (never import — the new tree owns its own migration state).

### USER-SIDE — IMPORT
| Table | Rows | Note |
|---|---|---|
| `UserAccounts` | 14 | core accounts |
| `auth` | 13 | passwords/auth |
| `UserData` | 0 | (empty) |
| `UserSettings` | 1 | |
| `usersettings` | 0 | (empty; distinct from `UserSettings` — case-sensitive!) |
| `AgentPrefs` | 1 | |
| `Avatars` | 293 | appearance/wearables (per-attribute rows) |
| `Friends` | 6 | |
| `GridUser` | 33 | home/last-position (grid-level) |
| `im_offline` | 1 | offline IMs |
| `mutelist` | 0 | (empty) |
| `inventoryfolders` | 577 | |
| `inventoryitems` | 3738 | |
| `userprofile` | 34 | |
| `userpicks` | 1 | profile picks |
| `usernotes` | 1 | profile notes |
| `classifieds` | 0 | profile classifieds (empty) |
| `partnerships` | 0 | (empty) |
| `partnership_logs` | 0 | (empty) |
| `os_groups_groups` | 1 | groups |
| `os_groups_membership` | 1 | |
| `os_groups_principals` | 1 | |
| `os_groups_roles` | 3 | |
| `os_groups_rolemembership` | 2 | |
| `os_groups_invites` | 0 | (empty) |
| `os_groups_notices` | 0 | (empty) |
| `assets` | 10281 | ★ ~834 MB — the bulk of the dump |
| `experiences` | 4 | §6: mechanical convert (`experience_id`→`public_id`, drop created/updated) |
| `experience_permissions` | 3 | §6: mechanical (`granted`→`allow BIT`) |
| `experience_keyvalue` | 0 | §6: → `experience_kv` (empty) |
| `experience_kvp` | 0 | §6: → `experience_kv` (empty) |

### REGION-SIDE — SKIP (rebuilt from OAR)
`prims` (3283), `primshapes` (3521), `primitems` (24), `terrain` (4), `bakedterrain` (4),
`land` (4), `landaccesslist` (0), `regionsettings` (4), `regionenvironment` (0),
`regionwindlight` (0), `regionextra` (0), `regionban` (0), `spawn_points` (0),
`regions` (0, RegionStore — never import), `estate_groups` (0), `estate_managers` (0),
`estate_map` (4), `estate_users` (0), `estateban` (0),
`experience_allowed` (0), `experience_blocked` (0), `experience_agent_blocked` (0),
`experience_trusted` (0) [estate-experience admission → map to upstream `estate_*_experiences` via EstateStore 37/38, all empty].

### TRANSIENT / SESSION — SKIP (regenerated at runtime)
`Presence` (0), `hg_traveling_data` (0).

### ⚠️ FLAG — do not auto-classify, decide manually
| Table | Rows | Why flagged |
|---|---|---|
| `admin_settings` | 3 | **Non-standard OpenSim table** — Legion web-admin config. Tranquillity likely has no equivalent; almost certainly a Legion-web concern, not a grid import. Confirm with John before importing/dropping. |
| `script_experiences` | 5 | User/experience-side, but **no relational import target in Tranquillity** (recon §6 — binding lives on `TaskInventoryItem.ExperienceID`, not persisted relationally). **Trapped**: manual in-world re-association post-cutover, not a table import. |
| `estate_settings` | 1 | Region-side, but carries estate ownership/config. Verify it's re-established after OAR import; may need manual estate-owner/manager re-setup. |
| `tokens` | 1 | Auth/session token — transient; almost certainly skip, but confirm it isn't a persistent grant. |

Total: 57 tables + `migrations` (excluded). 30 USER-SIDE (import), 23 REGION-SIDE (skip),
2 transient (skip), 4 flagged (3 of the flagged overlap the above buckets; listed once here).

---

## Phase 8 — Code port (Legion-exclusive work onto upstream `develop`)

Base = upstream `develop` @ `81e5c244` (recon §1). Port order & difficulty per recon §2–§4, §8:

| Item | Verdict | LOC | Action |
|---|---|---|---|
| **Groups** | Already upstream (upstream *ahead*) | **0** | **Adopt upstream wholesale.** Nothing to port. Optionally absorb upstream's GroupsService null-guards + `MySqlConnector` swap. |
| **Friends** | Already upstream; Legion older | **0** | **Adopt upstream wholesale.** Watch: `XCallingCardModule`→`CallingCardModule` INI rename (Phase 3). |
| **God-mode proper** | Parity; 2 config points | **~4** | **Policy decision, not a port:** `region_owner_is_god` default (Legion `true` vs upstream `false`) + viewer god-level auto-promote to 200. Decide with John. |
| **Estate / Perms admin** | Legion-exclusive **+ divergent** | **~118** | **Cherry-pick onto upstream's NEWER `EstateManagementModule` — expect merge conflicts** (both sides edited it). Port: 4 PermissionsModule estate-manager parcel-power fixes (~18), `HandleEstateObjectReturn` (~50), config-enforcement flips (`IgnoreEstateMinor/PaymentAccessControl`→false), multi-engine LandStat, `estate reload` console cmd (~50). Do **not** clobber upstream's newer Experience-delta executor (~256), PBR terrain (~90), ban-admin guard, or real `CanCopyObjectInventory`. |
| **FlotsamAssetCache size-limit** | Legion-exclusive block | **~67** | Port `m_MaxFileCacheSizeMB` + `EnforceFileCacheSizeLimit` into upstream's newer FlotsamAssetCache.cs. |
| **Search (classifieds/places)** | Collision (~20 shared files) | **~500** | Reconcile Legion's grid-wide search arc against upstream's same-named files. `ParseFakeParcelID` is **not** a real fix (byte-identical). |
| **DisplayNames** | Collision | **~340** | Adopt upstream's `DisplayNameModule.cs`; graft Legion's throttle-days config, failure-reply, root-scope broadcast, cache-invalidation/rollback. Data compatible (`''`≡NULL). |
| **Phlox KVP + osPlaySoundURL** | Divergent (bytecode-incompatible) | — | Reconcile KVP API signatures (Legion async-string vs upstream sync-int); re-add `osPlaySoundURL` (TableIndex 674). |
| **Jolt physics (+joltc + vehicles)** | Clean add | **~7,800** | Drop-in; no upstream equivalent. |
| **DirectDeliveryConnector** | Clean add | **~548** | Drop-in. |
| **Http3AssetService** | Clean add | — | Drop-in (Legion-only). |

**Nothing to do:** WebRTC/Janus (already upstream).

---

## Phases 1, 2, 5, 6, 7 — TBD
Stubs pending John's canonical runbook. Expected coverage: dependency/tooling prep (1–2),
LibOMV feed switch (recon §7 — GO, rename `OpenMetaverseTypes`→`OpenMetaverse.Types`),
verification & cutover (5–7).
