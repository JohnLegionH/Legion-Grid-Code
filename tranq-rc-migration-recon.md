# Legion → Tranquillity RC/develop Migration Recon

**Date:** 2026-08-09 · **Mode:** READ-ONLY (no source edits, builds, or commits)
**Analyst target:** OpenSim-NGC/OpenSim-Tranquillity `develop` (new `Source/` layout, .NET 8)
**Legion source under study:** `/d/legion-grid-source` @ branch `slua-tier2-tables`
**Live grid DB:** MySQL `opensim` (3 regions, 3,660 prims, 14 users) — the marketplace DB `legion_market` is a *different* project and is not touched here.

---

## 0. Executive summary / go-no-go

| Area | Verdict |
|---|---|
| **Target ref** | `upstream/develop` = `81e5c2449d` (confirmed: release/v1.0 merged + PRs #181–#186 + #176). RC tag `tranquillity-rel-1.0.32-rc` = `2095eeff` = head of `release/v1.0`. Confirmed. |
| **★ DB migration safety** | **NOT SAFE as-is.** `RegionStore` is a **hard boot-breaker** (duplicate-column + 3 silently-skipped columns). `UserAccount` is a benign silent-skip. `EstateStore` is clean. Pre-migration DDL reconciliation required. |
| **LibOMV** | **GO.** Bin DLLs are *stock* older LibreMetaverse (0.9.4), **no Legion patches to lose**. NGC feed (1.2.x) is a superset. One mechanical caveat: assembly rename `OpenMetaverseTypes` → `OpenMetaverse.Types`. |
| **Exclusive work** | ~9,500+ LOC of Legion-only work. Clean adds: Jolt (~7,800), DirectDelivery (~548), Http3AssetService slice. Collisions: DisplayNames, search, land/estate, FlotsamAssetCache, Phlox. |
| **Experience data** | `experiences` (4) + `experience_permissions` (3) convert **mechanically**. `script_experiences` (5) is **trapped** — Tranquillity has no relational target for it. |
| **Phlox** | Legion & upstream Phlox have **diverged** (KVP API signatures, osPlaySoundURL). Not a drop-in either direction. |

---

## 1. Divergence: my local `develop` vs freshly-fetched `upstream/develop`

Fetched `upstream develop --tags` into `/d/tranquillity-develop`.

- `upstream/develop` HEAD = **`81e5c2449d`** "Experience: SL conformance … (#184)" (2026-08-07). ✅ matches FACTS.
- My local `develop` HEAD = `645b0f3bb3` (2026-07-31).
- Merge base = `e1b0a31795`.
- **My local is behind by 13, ahead by 25.**

**(A) 13 commits upstream has that my local lacks** — the official NGC merges:
`#184` Experience SL conformance · `#183` Phlox per-script stats + suspend/resume · `#182` Phlox engine · `#181` bot/NPC framework · `#185` BinaryFormatter removal · `#186` moving_start/end + LinksetData group accessor · LibOMV pulled to top level · version 1.0/1.1-alpha + NBGV · `#176` hosted services.

**(B) 25 commits my local has that upstream lacks** — my *independent ports* of the same features (Phlox, experience T1–T5b + slices A/B, bots, BinaryFormatter removal, Phlox stats/suspend), each tagged "ported from Legion Grid …".

⚠️ **Implication:** my local `develop` and upstream `develop` **both** added Phlox, experience, bots, moving-events, and BF-removal — but *independently* (my ports vs Mike's PRs). Do **not** merge my local `develop` into upstream; rebase/reset onto `upstream/develop` and cherry-pick only what upstream lacks. Treat `upstream/develop` as the authoritative base.

---

## 2. Phlox drift (upstream `Source/InWorldz.Phlox*` vs Legion `OpenSim/Addons/Phlox`)

Upstream received Phlox via #182/#183 (Legion-derived). Since then the two have **diverged**; this is not a clean re-sync.

**Files that differ (substance, not whitespace):**

| File | Substance |
|---|---|
| `Glue/ISystemAPI.cs` | **KVP signature divergence** (see below) + upstream-only `llCreateKeyValueSL/ReadKeyValueSL/UpdateKeyValueSL` decls |
| `Glue/SyscallShim.cs` | dispatch shim: upstream has functional `*SL` wrappers; Legion has `osPlaySoundURL` shim (index 674) |
| `Types/Defaults.cs` | function registry — upstream 674 entries, Legion 672; different tail functions |
| `VM/Interpreter.cs`, `ByteCompiler/BytecodeGenerator.cs` | small runtime/emit deltas tracking the KVP changes |
| `Phlox.ScriptEngine/LinksetDataPhloxExtensions.cs` | **upstream-only** adapter: Tranquillity LinksetData → SL semantics |
| `Phlox.ScriptEngine/PhloxExperienceAdapter.cs` | **upstream-only** adapter: Phlox experience calls → Tranquillity `IExperienceService` |

**KVP API divergence (bytecode-incompatible):**
- Upstream base: `int llCreateKeyValue(k,v)`, `string llReadKeyValue(k)`, `int llUpdateKeyValue(k,v,check)` — synchronous; plus functional `*SL` wrappers.
- Legion base: `string llCreateKeyValue(k,v)`, `string llReadKeyValue(k)`, `string llUpdateKeyValue(k,v,int checkedFlag,string originalValue)` — **async request-key returns** (Legion wired KVP to its own experience/DirectDelivery backend). Legion's `*SL` variants are no-op stubs.
- These signatures differ in arity and return type → shim stack-frame layouts differ → **bytecode compiled on one will misbehave on the other.**

**Legion-only surface:** `osPlaySoundURL` (index 674) — scripts using it won't compile on upstream.

**TableIndex high-water mark:**
- Upstream = **673** (`osGetAvatarList`) — `Source/InWorldz.Phlox/Types/Defaults.cs`.
- Legion = **674** (`osPlaySoundURL`) — `OpenSim/Addons/Phlox/InWorldz.Phlox/Types/Defaults.cs`.
- The **"661"** figure is stale; current Legion tip (`slua-tier2-tables`) is at **674**. Legion leads upstream by the KVP-async rework + `osPlaySoundURL`.

**Regression risk:** moving Legion's live grid onto upstream Phlox = scripts using `osPlaySoundURL` fail to compile, and any script relying on Legion's async-KVP semantics changes behavior. Reconcile the KVP API and re-add `osPlaySoundURL` before cutover, or keep Legion's Phlox and only adopt upstream's Tranquillity adapters (`LinksetDataPhloxExtensions`, `PhloxExperienceAdapter`).

---

## 3. Legion-exclusive inventory (in Legion, not in upstream `develop`)

| # | Item | Verdict | Legion LOC (approx) | Portability |
|---|------|---------|---------------------|-------------|
| 1 | **Jolt physics** + vendored `joltc` patch + vehicles | CONFIRMED | ~7,800 C# + native `joltc.dll`/patch | **Clean add** (no upstream equiv) |
| 2 | **DirectDeliveryConnector** | CONFIRMED | ~548 | **Clean add** |
| 3 | Classifieds/Places search (+`ParseFakeParcelID`) | PARTIAL | ~500 across ~20 files | Collision; `ParseFakeParcelID` **not** a real fix (byte-identical to upstream) |
| 4 | Display-names Pass B EventQueue reply | CONFIRMED | ~340 in `BunchOfCaps.cs` | **Collision** (upstream refactored into `DisplayNameModule.cs`) |
| 5 | WebRTC / Janus | **DENIED** | — | Already upstream (`Addons/os-webrtc-janus`) — nothing to port |
| 6 | Land/estate audit fixes | CONFIRMED | ~200 across ~5 shared files | Collision (edits inside upstream-shared modules) |
| 7 | Phlox "M-series" memory work | CONFIRMED | ~500 | Mixed — `Http3AssetService` clean add; Scene/SOP/SP edits collide |
| 8 | FlotsamAssetCache size limits + `FileCleanupTimer` | CONFIRMED | ~67 unique | Collision; `FileCleanupTimer` is **stock** (in both), size-limit block is Legion-only |

**Key files (selected):**
- Jolt: `OpenSim/Region/PhysicsModules/LegionJolt/*` (`LegionJoltScene.cs` 3,782; `JoltPrim.cs` 744; `JoltCharacter.cs` 335; `JoltVehicleBody.cs` 124), `OpenSim/Addons/LegionPhysics/Legion.Physics/JoltPhysicsBackend.cs` 2,570, `native/joltc/*` (patch + `win-x64/joltc.dll`).
- DirectDelivery: `OpenSim/Server/Handlers/DirectDelivery/DirectDeliveryPostHandler.cs` (425), `DirectDeliveryConnector.cs` (123).
- Search: `Region/CoreModules/Framework/Search/BasicSearchModule.cs`, `Avatar/UserProfiles/UserProfileModule.cs`, `Data/{MySQL,PGSQL,SQLite,Null}/*SimulationData.cs|*UserProfilesData.cs`, `LLClientView.cs`.
- Land/estate: `CoreModules/World/Estate/EstateManagementModule.cs` (+53), `World/Land/LandManagementModule.cs` (+92), `Land/LandObject.cs` (+24), `Permissions/PermissionsModule.cs` (+17).
- FlotsamAssetCache: Legion 1,803 LOC vs upstream 1,740 — Legion-only `m_MaxFileCacheSizeMB` + `EnforceFileCacheSizeLimit(...)`.
- Http3AssetService (Legion-only): `OpenSim/Services/AssetService/Http3AssetService.cs`.

**Cleanest ports:** #1 Jolt, #2 DirectDelivery, Http3AssetService slice of #7.
**Reconcile against a different upstream impl:** #4 (module vs BunchOfCaps), #3/#6/#8 (edits inside shared files).
**Nothing to do:** #5.

---

## 4. Display-names collision

**Storage — SAME table/columns, DIFFERENT migration definition and code shape:**
- Both write `UserAccounts.DisplayName` / `UserAccounts.NameChanged` via `IUserAccountService.SetDisplayName`.
- **API arity differs:** upstream `SetDisplayName(agentID, displayName)` (service stamps timestamp) vs Legion `SetDisplayName(principalID, displayName, int nameChanged)` (caller supplies it). `NameChanged` type: upstream `uint` (+ `DateTime` on `UserData`) vs Legion `int`.
- **Module layout:** upstream has a dedicated `Source/OpenSim.Region.ClientStack.LindenCaps/DisplayNameModule.cs` (+`IDisplayNameModule`); **Legion has neither** — the whole feature lives inline in `BunchOfCaps.cs`. → **hard code conflict.**

**EventQueue live-nametag refresh — BOTH do it** (not Legion-only, contrary to the hypothesis):
- Upstream: `DisplayNameUpdate` broadcast via `ForEachClient` + `SetDisplayNameReply` to the setter (success only).
- Legion: `DisplayNameUpdate` via `ForEachRootScenePresence` (root only) + `SetDisplayNameReply` on **both success and failure**, with explicit in-memory rollback + `InvalidateCache` on store failure.

**Empty-string vs NULL — COMPATIBLE:** live rows store `''` (all 14), never NULL. Both codebases branch on `string.IsNullOrEmpty`/`IsNullOrWhiteSpace` (`IPeople.IsNameDefault`, `ViewerDisplayName`), so `''` is read identically as "no custom name." **No data migration needed.**

**Migration tie-in (see §5):** both define `UserAccount` `:VERSION 7` with conflicting DDL; Legion's `varchar(64) NOT NULL` already occupies slot 7 on live, so upstream's `varchar(31) NULL` never runs — harmless on the live DB (same columns exist, wider), but a **fresh** upstream install would drift to `varchar(31)`.

Other behavioral deltas Legion adds: configurable `DisplayNamesThrottleDays` (default 3 vs upstream fixed 7), first-ever set never throttled, clear/revert throttle-exempt and doesn't advance `NameChanged`, explicit cache invalidation, and a narrow "Pass C" `setdisplayname` service method so Robust's broad `AllowSetAccount` gate can stay closed.

---

## 5. ★ DB migration safety (most important)

Method: dumped live `opensim.migrations` (read-only), compared each store's applied version + actual live DDL against upstream `Source/OpenSim.Data.MySQL/Resources/*.migrations`. OpenSim applies migrations by `(store, version)` number — **if live's version ≥ upstream's for a store, upstream's DDL at those numbers never runs (silent skip).**

### Collision table

| Store | upstream max | Legion res max | **LIVE applied** | Status |
|---|---|---|---|---|
| **RegionStore** | 68 | 67 | **67** | ⛔ **HARD BLOCKER** |
| **UserAccount** | 7 | 7 | **7** | ⚠ silent-skip (benign) |
| **EstateStore** | 38 | 36 | **36** | ✅ clean (37/38 will run) |
| Experience | 4 | absent | (none) | ✅ new store, created fresh |
| UserAlias | 1 | absent | (none) | ✅ new table |
| FSAssetStore / LogStore / XAssetStore | 1/1/2 | 1/1/2 | (none) | ✅ never used on live; create fresh |
| AgentPrefs, AssetStore, AuthStore, Avatar, FriendsStore, GridStore, GridUserStore, HGTravelStore, IM_Store, InventoryStore, MuteListStore, Presence, UserProfiles, os_groups_Store | = | = | = | ✅ at parity, DDL byte-identical |

Only **3 stores** have differing DDL: RegionStore, UserAccount, EstateStore. All others are byte-identical after CRLF normalization.

### ⛔ RegionStore — hard boot-breaker (must fix before cutover)

Legion and upstream renumbered `prims` columns differently in the 65–68 range. Live `prims` (ground truth) has Legion's columns and **lacks** upstream's:

| Upstream migration (skipped because live=67) | Upstream expects | **Live prims actually has** |
|---|---|---|
| `:VERSION 65` | `linksetdata MEDIUMTEXT` | **missing** — live has `lnkstBinData blob` (Legion #65) |
| `:VERSION 67` | `AllowUnsit`, `ScriptedSitOnly` | **both missing** (Legion #67 was a vestigial `StartStr` no-op note) |
| `:VERSION 68` **(WILL RUN, live=67)** | `ALTER TABLE prims ADD COLUMN StartStr text` — **no `IF NOT EXISTS`** | live **already has** `StartStr text` (Legion added it early) → **MySQL error 1060 duplicate column → migration aborts on boot** |

Net: on first boot against upstream, RegionStore migration **dies at #68** (duplicate column); even if forced past, upstream code referencing `linksetdata`, `AllowUnsit`, `ScriptedSitOnly` hits missing columns.

**Required remediation (pre-migration, on live schema):**
1. `ALTER TABLE prims ADD COLUMN linksetdata MEDIUMTEXT DEFAULT NULL;` and backfill/convert from `lnkstBinData` if any linkset data exists (check: does upstream read `linksetdata` as text vs Legion's blob — a format conversion, not just rename).
2. `ALTER TABLE prims ADD COLUMN AllowUnsit TINYINT(3) NULL DEFAULT 1, ADD COLUMN ScriptedSitOnly TINYINT(3) NULL DEFAULT 0;`
3. Prevent the #68 duplicate: since `StartStr` already exists, bump the RegionStore migrations row to **68** *before* boot (so #68 is skipped), OR patch upstream #68 to `ADD COLUMN IF NOT EXISTS`. Bumping the applied version is the safer, source-untouched option.

### ⚠ UserAccount — silent-skip (benign on live, drift on fresh installs)

Both `:VERSION 7`; live already at 7 so upstream's #7 skips. Live keeps Legion's `DisplayName varchar(64) NOT NULL DEFAULT ''` + `NameChanged int NOT NULL DEFAULT 0` (upstream wanted `varchar(31) NULL`). Compatible at runtime (§4). **Action:** optional — normalize to one definition to avoid prod-vs-fresh schema drift; not required for boot.

### ✅ EstateStore — clean

Live at 36; upstream 37/38 (`CREATE TABLE IF NOT EXISTS estate_allowed_experiences`, `estate_key_experiences`, `estate_blocked_experiences`) **will run** and create the experience-estate tables empty. No collision. (Populating them from Legion's experience data is a §6 task, not a migration issue.)

---

## 6. Experience data conversion map

Legion uses a **rich multi-table** experience model; Tranquillity uses **single-table allow-BIT + `TaskInventoryItem.ExperienceID`**.

| Legion table (live rows) | Tranquillity target | Conversion |
|---|---|---|
| `experiences` (**4**) — `experience_id,owner_id,group_id,name,description,maturity,properties,logo,marketplace,slurl,created,updated` | `experiences` — `public_id,owner_id,name,description,group_id,logo,marketplace,slurl,maturity,properties` | **MECHANICAL.** `INSERT…SELECT` with `experience_id→public_id`; drop `created/updated`. All 4 short names fit `VARCHAR(42)`. |
| `experience_permissions` (**3**, all `granted=1`) — `experience_id,agent_id,granted(tinyint),created` | `experience_permissions` — `experience,avatar,allow BIT(1)` | **MECHANICAL.** `experience_id→experience`, `agent_id→avatar`, `granted→allow` (1→b'1'); drop `created`; PK becomes `(experience,avatar)`. This is the "single-table allow BIT". |
| `experience_keyvalue` (0), `experience_kvp` (0) | `experience_kv` (`experience,key,value`) | MECHANICAL but **empty** — nothing to move. |
| `experience_allowed` / `experience_blocked` / `experience_agent_blocked` / `experience_trusted` (all **0**) | estate tables `estate_allowed/key/blocked_experiences` (created by EstateStore #37/38) | Empty — no data to move; tables created fresh by migration. |
| **`script_experiences` (5)** — `item_id,experience_id,region_id,created,updated` | **NO relational target** | ⛔ **TRAPPED.** |

**Why `script_experiences` is trapped:** Tranquillity carries the script→experience binding on `TaskInventoryItem.ExperienceID` (`Source/OpenSim.Framework/TaskInventoryItem.cs:123`). Verified there is **no** persistence for it:
- `primitems` table (live *and* upstream DDL) has **no `ExperienceID` column**.
- Upstream MySQL data layer has **zero** `ExperienceID` references.
- `SceneObjectSerializer` does **not** emit/read `ExperienceID` → it isn't written to object/prim-inventory XML either.
- `ExperienceService` persists only experience info, permissions, and KV — no item binding table.

So Tranquillity's `ExperienceID` is effectively **runtime-only** on the in-world `TaskInventoryItem`; Legion's 5 clean relational rows have **no mechanical import target**. To carry them over you must, per item, locate the containing object and set `ExperienceID` on the live `TaskInventoryItem` (then rely on whatever re-persist path exists) — i.e. reach into serialized prim inventory / rezzed object state. Given only 5 rows across 2 regions, the pragmatic path is **manual re-association in-world** after cutover rather than an automated data migration. (Also flag upstream: `ExperienceID` may not survive a region restart at all given the serializer gap — worth confirming with NGC.)

**Summary:** 7 experience rows convert mechanically (4 experiences + 3 permissions); 5 script bindings are trapped and need manual/in-world re-association; all admission/KV tables are empty.

---

## 7. LibOMV — GO

Reflection + PDB-provenance analysis of `/d/legion-grid-webrtc/bin/OpenMetaverse*.dll`:

| DLL | AsmVer | Verdict |
|---|---|---|
| OpenMetaverse.dll | 0.9.4.0 | **STOCK** (older LibreMetaverse) |
| OpenMetaverseTypes.dll | 0.0.0.0 | STOCK (undotted legacy name) |
| OpenMetaverse.StructuredData.dll | 0.0.0.0 | STOCK |
| OpenMetaverse.Rendering.Meshmerizer.dll | 0.0.0.0 | STOCK |

- **No Legion patches present.** Full public-API diff vs NGC 1.2.x shows NGC is a strict **superset** (buffer pooling `IByteBufferPool`, `ToBytesMultiple`, `InventoryCacheEntry`); bin-unique members = 1 trivial ctor artifact. `PacketType` enum identical (388 members) — no custom Legion packets. No `"legion"` string in the binary.
- Embedded PDB path shows the bin DLLs were built from `D:\opensimWork\libomv\…` (a tree not on this machine and **not** `/d/libomv-src`). `/d/libomv-src` is actually the **NGC feed source** (remotes = OpenSim-NGC, HEAD detached at `1.2.8-beta`, zero "legion" commits) — i.e. not a Legion fork.
- The bin lineage is simply *older* (still `System.Drawing`/SmartThreadPool/ComponentAce zlib; copyright "…2006-2015"). NGC replaced these with SkiaSharp/CoreJ2K/`ZLibStream`.

**GO — the NGC NuGet LibOMV can replace the vendored DLLs with nothing lost.** Mechanical caveats (not blockers):
1. **Assembly rename** `OpenMetaverseTypes` → `OpenMetaverse.Types`. Every `<Reference Include="OpenMetaverseTypes">` HintPath in Legion (`OpenSim.Addons.Groups.csproj`, `os-webrtc-janus/*`, `LegionPhysics/Legion.Vehicles.csproj`, `OfflineIM`, …) must be repointed to the NuGet package. Grep `/d/legion-grid-source` for `OpenMetaverseTypes` and fix all refs. (Upstream already pulls LibOMV top-level via #172/the top-level pull commit, so most of this is handled by adopting upstream's build.)
2. Version jump 0.9.4 → 1.2.x brings behavioral changes (SkiaSharp image path, threadpool, buffer pooling) — smoke-test texture/J2K decode and packet send.
3. `AssemblyVersion` 0.9.4.0 → 1.2.x.0 — update any explicit `Version=`/binding redirects (OpenSim uses HintPath, likely fine).
4. Requires `GITHUB_ACTOR`/`GITHUB_TOKEN` with `read:packages` for the private NGC feed (per BUILDING.md) — an ops prerequisite, not a code blocker.

---

## Recommended migration order

1. **Base:** reset local `develop` onto `upstream/develop` `81e5c244`; do not merge my parallel ports (§1). Cherry-pick only upstream-absent work.
2. **Pre-flight DB (on a copy first):** apply the RegionStore remediation (§5) — add `linksetdata`/`AllowUnsit`/`ScriptedSitOnly`, bump RegionStore migration row to 68. Optionally normalize UserAccount #7.
3. **LibOMV:** switch to NGC feed; repoint `OpenMetaverseTypes` refs (§7).
4. **Port clean adds:** Jolt (+joltc), DirectDelivery, Http3AssetService.
5. **Reconcile collisions:** DisplayNames (adopt upstream module, graft Legion's throttle/rollback/root-scope), search, land/estate, FlotsamAssetCache size-limit block, Phlox KVP API + `osPlaySoundURL`.
6. **Experience:** run mechanical convert for `experiences` + `experience_permissions`; manually re-associate the 5 `script_experiences` in-world post-cutover.
7. **Verify** on a DB copy end-to-end before touching the live `opensim` DB.

---

## 8. Delta check — Groups / Friends / God-mode (permissions & estate admin)

Diffed Legion `slua-tier2-tables` files against their `upstream/develop` equivalents (paths mapped: Legion `OpenSim/Region/<x>` → upstream `Source/OpenSim.Region.<x>`; Legion `OpenSim/Addons/Groups` → upstream `Addons/OpenSim.Addons.Groups`). **All raw diffs are inflated by upstream's repo-wide modernization** (file-scoped namespaces, implicit usings, `[Extension]`/Mono.Addins removal, `MySql.Data`→`MySqlConnector`, `Mono.Data.Sqlite`→`System.Data.SQLite`); every classification below is from whitespace/namespace-normalized diffs.

### Summary table

| Subsystem | Verdict | Legion-exclusive LOC | Schema impact | Port difficulty |
|---|---|---|---|---|
| **Groups** (os_groups_*, GroupsModule, XmlRpcGroups) | ALREADY UPSTREAM (style-only; upstream *ahead* on 2 files) | **0** | **NONE** (`os_groups_Store.migrations` byte-identical v3=v3; zero DDL) | LOW — nothing to port |
| **Friends** (FriendsModule/Service, HG friends) | MOSTLY-COSMETIC (Legion is older baseline; upstream refactored) | **0** | **NONE** (`FriendsStore.migrations` identical v4=v4; FriendsData SQL text unchanged, only ADO driver differs) | TRIVIAL — nothing to port |
| **God-mode proper** (GodController, GodsModule, Scene.Permissions, LLClientView god paths) | ALREADY UPSTREAM (2 minor DIVERGENT config/behavior points) | **~4** (all in `GodController.cs`) | **NONE** | Trivial |
| **Estate/Perms admin** (Permissions, EstateManagement, estate console) | LEGION-EXCLUSIVE + DIVERGENT (mixed) | **~118** (all self-contained) | **NONE new** | Moderate |

### ★ Schema verdict (point 4): the migration question stays CLOSED.
**No delta in any of these three subsystems requires new DB schema.** Groups and Friends migrations are byte-identical to upstream; god-mode/permissions touch no tables; the Legion-exclusive estate-admin code operates on existing `EstateSettings`/`RegionSettings`/inventory. The only estate-schema pressure remains the upstream Experience-estate tables (EstateStore 37/38, already handled in §5). §5 is not reopened.

### Groups — verdict: nothing to port
0 files with Legion-only functional logic. 21 files style-only, 2 migrations identical. The only non-cosmetic deltas run the *other* way (upstream ahead): `Service/GroupsService.cs` gained null-guards + `UUID.TryParse` refactor (~11 lines Legion lacks); `MySQLGroupsData.cs` migrated to `MySqlConnector`. No Phlox/Experience entanglement (`GroupPowers.ExperienceAdmin/Creator` are stock libOMV enum flags in unchanged code). **Action: adopt upstream Groups wholesale; optionally let Legion absorb upstream's guard fixes.**

### Friends — verdict: nothing to port
0 Legion-exclusive logic. 1 migration identical, 20 files cosmetic-only, 2 driver-swap-only. 3 files where upstream is *newer* (Legion behind): `CallingCardModule` name `XCallingCardModule`→`CallingCardModule` (⚠ check INI refs), `FriendsModule` `MainServer.Instance.GetHttpServer` API shape, `FriendsCommandsModule` cleaned up a cast-through workaround. Fully decoupled from Phlox/Experience. **Action: adopt upstream Friends drop-in (low risk).**

### God-mode proper — verdict: parity, 2 config nuances
`GodsModule.cs`, `GodNamesModule.cs`, `Scene.Permissions.cs`, `IPermissionsModule.cs`, `AssetPermissions.cs`, and the LLClientView/Scene/ScenePresence god-kick/godlike/teleport-home/GodLevel paths are all identical (style + line-offset only). Only `GodController.cs` diverges, in 2 spots where **Legion is behind/looser**:
1. `region_owner_is_god` default — Legion `true` vs upstream `false`.
2. `SyncViewerGodLevel` — Legion does not auto-promote viewer UI to god-level 200; upstream does.
No Legion-exclusive god features. **Action: decide policy on the 2 config points; otherwise take upstream.**

### Estate/Perms admin — verdict: cherry-pick ~118 LOC, but Legion is also behind upstream here
**LEGION-EXCLUSIVE (self-contained; Phlox only in comments) — port these:**
- `PermissionsModule.cs` (~18 LOC): estate-manager parcel-power fixes — `CanAbandonParcel`/`CanReclaimParcel` pass real `GroupPowers.LandRelease` + `allowManager=true`; `CanEditParcelProperties` *honors* the `allowManager` param (upstream hardcodes `false`, denying appointed EMs sale/divide/join/eject/freeze); `CanCopy/EditUserInventory` add Copy/Modify-perm guards.
- `EstateManagementModule.cs`: `HandleEstateObjectReturn` (estate-wide "Return Objects", cross-scene, scripted-only, audit-logged) ~50 LOC; config-default flips `IgnoreEstateMinor/PaymentAccessControl` → `false` (SL parity, enforce Deny-Anonymous/Age-Unverified); multi-engine LandStat aggregation via `RequestModuleInterfaces<IScriptModule>()`.
- `EstateManagementCommands.cs` (not in original list — flagging): `estate reload [all]` console command (~50 LOC), depends only on `Scene.ReloadEstateData()` which exists in both trees.

**DIVERGENT — Legion is BEHIND upstream (do NOT overwrite these when porting):**
- Upstream has the Experience-estate delta executor (`handleEstateExperienceDeltaRequest`/`execExpDeltaRequests`, add/remove Key/Allowed/Blocked experiences) ~256 LOC — **absent** on `slua-tier2-tables`.
- Upstream has PBR terrain (`SetEstateTerrainTextures`, `TerrainPBR1..4`) ~90 LOC + the `IEstateModule.SetEstateTerrainTextures` addition — absent in Legion.
- Upstream has a ban-admin guard ("Cannot ban an Administrator") — absent in Legion.
- Upstream `CanCopyObjectInventory` has a full group-`ObjectManipulate`-powers impl; Legion is a `return true` TODO stub.

**Correction to §3:** the earlier "EMM +53 / PermissionsModule +17" figures were vs an older upstream baseline. Against *current* upstream, EMM/EstateModule are net **behind** (upstream added Experience+PBR+ban-guard since). Legion's real exclusive estate value is the four permission fixes + `HandleEstateObjectReturn` + config enforcement flips + multi-engine LandStat + `estate reload`. **Action: cherry-pick the exclusive items onto upstream's newer EMM; expect merge conflicts since both sides edited these files.**
