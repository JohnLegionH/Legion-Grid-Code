# Tranquillity Sync + Legion→Tranquillity Port Audit v1

Date: 2026-07-18
Mode: read-only audit (only write action: `git fetch upstream` in tranquillity-develop, plus this file)
Legion source of truth: `D:\legion-grid-source` @ `slua-tier2-tables` (`f7ff0e8cd1`)
Tranquillity local: `D:\tranquillity-develop` @ `develop` (`698538b6a7`)
Mike's upstream head (fetched this session): `upstream/develop` = `cd3b07b1f1`

---

## PHASE 0 — Upstream sync state

- Remotes: `origin` = JohnLegionH/OpenSim-Tranquillity (fork), `upstream` = **OpenSim-NGC/OpenSim-Tranquillity (Mike)**. Fetched `upstream` only; no merge/rebase/ff performed.
- **Mike's delta since last sync (`develop..upstream/develop`): exactly 1 commit**
  - `cd3b07b1f1` "Fix for Region Server loading (#178)" — touches 3 ServiceConnectorsIn modules (Land/Neighbour/Simulation). **Does not touch Phlox/SLua/Experience.**
- Reverse view: local `develop` is **9 commits ahead** of upstream — John's port-in-progress series (Phlox VM/compiler, Phlox.ScriptEngine, scheduler, bot framework, moving_start/end + LinksetData, config/docs/conformance runner, C5 identity fixes ×2, llHTTPRequest load-context fix, build hygiene). These are *local-only*; nothing of the Phlox/SLua port is in Mike's tree yet.
- Other upstream branches present (not audited): dev-future, feature/fix-lslhttp, feature/hosted-services, feature/robust-di, moneyservice_di, release/tranquillity-1.0.6-beta.

## PHASE 1 — Port baseline

Baseline used: **the July-3 Phlox import into local develop (`e5136a6d02` + siblings)**, which captured Legion's SLua Tier-2 completion state as of 2026-07-03 12:53. Everything on `slua-tier2-tables` after ~July 3 is presumptively unported; the audit below verified item-by-item rather than trusting dates. (Stated per task instruction: the SLua Tier-2 conformance completion is the baseline.)

---

## PHASE 2 — Port audit

### A. SLua engine — essentially fully ported; one function behind

Legion max TableIndex: **674** (`OpenSim/Addons/Phlox/InWorldz.Phlox/Types/Defaults.cs`); Tranquillity max: **673** (`Source/InWorldz.Phlox/Types/Defaults.cs`).

11 of 12 sampled SLua commits verified PRESENT in the Tranquillity copy (tables, closures, dynamic typing, metatables, Lua patterns, LLEvents:on/DetectedEvent, vector/rotation, OOP-def bug fix, math/*string* conformance, stdlib breadth, dynamic-typing serialization). Namespaces are identical (`InWorldz.Phlox.*`); only directory layout differs (`OpenSim/Addons/Phlox/...` vs `Source/...`). No packaging blocker.

| Item | Legion commit | Status | Class |
|---|---|---|---|
| SLua Tier-1/Tier-2 language features + conformance fixes (thru 2026-06-27) | `de31a68c52`..`4e509a25e2` series | In Tranquillity local develop | **[ALREADY-THERE]** (via John's port, not Mike) |
| `osPlaySoundURL` (TableIndex 674, 4-file registration) | `0d4fcb43cf` (07-04) | Missing (import was 07-03) | **[PORT-CLEAN]** — mechanical 4-file OSSL registration |

### B. Phlox engine — closures ported; two operational subsystems missing

| Item | Legion commit(s) | Tranquillity | Class / risk |
|---|---|---|---|
| OnScriptRemoved event | `62cf6ffce6` | PRESENT (`PhloxEngine.cs:245,611`) | [ALREADY-THERE] |
| OnScriptInjected + sensor/volume-detect/control state restore | `9b5c4ee8c0` | PRESENT (`Interpreter.cs:131`, `LSLSystemAPI.cs:120-149`) | [ALREADY-THERE] |
| CHANGED_REGION_START dispatch | `b70188e84a` | PRESENT (`PhloxExecutionScheduler.cs:215-224`) | [ALREADY-THERE] |
| M-14/M-14b s_primCharacters region-keying + cleanup | `059171188b`, `2a428262b7` | PRESENT | [ALREADY-THERE] |
| C-1..C-5 closures | (bundled in the July-3 import content) | PRESENT per sampling above | [ALREADY-THERE] |
| **PHLOX-STATS** per-script time/memory surface | `8746f87e68` (07-08) | ABSENT — Tranquillity has stub Top-Scripts APIs returning 0/empty | **[PORT-ADAPT]**, MEDIUM risk (~140 lines across Interpreter/Scheduler/Engine; verify lock placement) |
| **PHLOX-SUSPEND** transient suspend/resume + console cmds | `d3dab74082` (07-14), `fe31bac769` (07-08) | ABSENT — SuspendScript stub returns false | **[PORT-ADAPT]**, HIGH risk (~200+ lines; executor gating + scheduler state machine) |
| EXP-PERSIST-1 Phlox-side hooks | `1e60281745` | N/A — service-layer only, no Phlox hooks needed | n/a |

### C. Experience system — largest gap; Tranquillity has a divergent partial skeleton

Key finding: Tranquillity is NOT empty here — it has its **own older partial Experience implementation** with a *different* interface and *different* 2-table schema. This makes the port an adapt/replace job, not a copy job.

Legion inventory (13 core commits: Phase 31, OPS-4 bootstrap, EXP-PERSIST-1, EXP-KV-ASYNC-1, parcel slices, EXP-CAPS-1..3, EXP-PRECEDENCE-1, EXP-REGIONMGMT-1, EXP-REGIONBLOCK-1, SLICE2-DONE):
- Service: `OpenSim/Services/ExperienceService/ExperienceService.cs` (idempotent 6-table bootstrap), `IExperienceService.cs`, `ExperienceInfo.cs` (XP_ERROR_* codes)
- Module: `OpenSim/Region/CoreModules/Experience/ExperienceModule.cs` (INonSharedRegionModule; caps RegionExperiences GET/POST, GetExperienceInfo var-path, FindExperienceByName 1-based pagination; estateexperiencedelta)
- Tables (6, not 5 as the task stated — `script_experiences` was added by EXP-PERSIST-1): `experiences`, `experience_permissions`, `experience_keyvalue`, `experience_allowed`, `experience_blocked`, `script_experiences`
- KV functions: indexes **610-617** (llCreateKeyValue…llClearKeyValue; SL-async dataserver pattern returning request-UUID, XP_ERROR payloads; 618-620 not used — actual top of range is 617 plus SL wrapper). ~14 wired touchpoints total (KV + parcel-enforcement reads + persistence lookup + caps + estate delta).
- **IUserAccountService coupling: NONE.** `SetDisplayName` (line 218) is Display-Names-only; Experience code never touches IUserAccountService. Porting Experience does NOT drag in Display Names.

Tranquillity side: stub `OpenSim.Services.ExperienceService` (10 KB, console cmds only), different `IExperienceService` signatures, `MySQLExperienceData` with v3/v4 migrations (2 tables: `experiences` + `experience_permissions` w/ different columns, `experience_kv` vs Legion's `experience_keyvalue`), caps-only `ExperienceModule` as **ISharedRegionModule** (wrong lifetime for Legion's per-region design), and **synchronous** KV functions in the ported Phlox (return int, not async request-UUID).

| Component | Class | Risk |
|---|---|---|
| ExperienceService.cs (replace stub) | [PORT-CLEAN] | LOW (bootstrap handles schema) |
| IExperienceService interface (replace) | [PORT-ADAPT] | HIGH — signature clash with existing Tranquillity connectors/handlers |
| Schema (6-table vs existing v4 2-table) | [PORT-ADAPT] | MEDIUM — needs migration path, table-name mismatch (`experience_kv`) |
| MySQLExperienceData | [PORT-ADAPT] | MEDIUM |
| Phlox KV async conversion (610-617) | [PORT-ADAPT] | HIGH — sync→async dataserver rewire in the ported engine |
| ExperienceModule (INonSharedRegionModule + caps + estate delta) | [PORT-ADAPT] | MEDIUM — replaces ISharedRegionModule caps stub |
| Parcel experience enforcement (block-wins, #18 storage) | [PORT-CLEAN] | MEDIUM |
| EXP-PERSIST-1 script association | [PORT-CLEAN] | MEDIUM |

### D. Bug fixes — Mike-contribution candidates (checked against `upstream/develop` head, not local)

| # | Fix | Legion commits | Upstream verdict | Risk | Class |
|---|---|---|---|---|---|
| 1 | Land root-presence guard family (buy-pass, siblings, eject/freeze residency, SetParcelOtherCleanTime R2, reference CAP/UDP guard) | `210cb882b4`, `624c35a1e5`, `66821841b9`, `9efe2865c3`, `22951b3742` | **ABSENT** — upstream `LandManagementModule.cs` has zero IsChildAgent guards (buy-pass line 556, properties-update 1624, abandon 1687 all unguarded) | LOW — same handler shapes | **[PORT-CLEAN]** ★ top candidate |
| 2 | Estate null-assign ×2 + `estate reload` cmd (+ July-14 multi-scene console guard `f1221906bc`) | `bac0a8f42a`, `f1221906bc` | **ABSENT** — upstream still assigns null es on load-fail in BOTH sites; no estate reload cmd | MEDIUM (two PRs: safety fix, then command w/ ConsoleScene guard) | **[PORT-CLEAN]** |
| 3 | FlotsamAssetCache BinaryFormatter→XmlSerializer + cache-dir versioning | `ff1cb83c4b` | **ABSENT** — upstream BinaryFormatter at read (522) and write (1008) | LOW | **[PORT-CLEAN]** ★ |
| 4 | IsFriendWithPerms UUID-swap | — | **NO DELTA FOUND** — upstream PermissionsModule.IsFriendWithPerms is byte-identical to Legion's (same `GetRightsGrantedByFriend(user, objectOwner)` order). Either already converged or the item refers to something else | n/a | **[ALREADY-THERE]** (see open questions) |
| 5 | Avatar cloud fix: `SaveBakedTextures(id)` in SaveAppearance | `70763e49c9` (one-line hunk in monolithic Phase-34 commit) | **ABSENT** — upstream `HandleAppearanceSave` calls SetAppearanceAssets but not SaveBakedTextures | LOW — one line, cleanly extractable | **[PORT-CLEAN]** |
| 6 | BinaryFormatter remediation (YEngine SYSERIAL/THROWNEX, Util dead helpers, unsafe-flag removal) | `a75104e516`, `bc9cd3cd2d`, `4fbd5ed852` (+ #3) | **ABSENT across the board** | LOW — self-contained per file | **[PORT-CLEAN]** ★ (urgent for .NET9) |
| 7 | MySQLSimulationData transactions + kill-switch + retry (PERSIST-1.1-TX) | `2a9444c0a5`, `f7ff0e8cd1` | **ABSENT** — upstream autocommit-per-statement in StorePrimInventory/StoreObject/RemoveObject | MEDIUM — invasive data-access change; coordinate with Mike | **[PORT-ADAPT]** |

### [LEGION-ONLY] candidates — John decides

- **moving_start/moving_end + group LinksetData accessor** — prior decision says Legion-exclusive, BUT it is already in local develop (`b828fdc06a`) as part of the port series. Decide: keep in the PR series to Mike, or strip before upstreaming.
- **Bot/NPC framework + bot persistence** (`3650bf0124`, Phase-34 bot persistence) — same situation: prior decision Legion-exclusive, yet already sitting in local develop.
- **Display Names** (IUserAccountService.SetDisplayName + A/C commits) — not experience-coupled (verified), so it is separable; flag as Legion-only unless John wants to offer it upstream.
- `llClearKeyValue` (617) is a Legion extension beyond SL spec — decide whether it goes upstream with the KV port.

---

## Proposed port order (small, buildable, testable slices)

Bug fixes first (Mike-contribution candidates), then engine gaps, then Experience.

1. **BF-1**: FlotsamAssetCache BinaryFormatter fix (#3). One file, testable by cache round-trip.
2. **BF-2**: Remaining BinaryFormatter remediation (#6) — YEngine opcode swap, Util cleanup, flag removal.
3. **BF-3**: SaveBakedTextures one-liner (#5).
4. **BF-4**: Land guard family (#1) as its own PR series in Legion's original order (reference guard → R2 → buy-pass → siblings → eject/freeze).
5. **BF-5**: Estate safety PR (null-assign ×2), then estate-reload command PR (bring `f1221906bc` multi-scene guard with it).
6. **BF-6**: PERSIST-1.1-TX (#7) — discuss with Mike first (kill-switch semantics, ProcessBackup retry interplay).
7. **ENG-1**: osPlaySoundURL registration (index 674) — brings SLua tables to parity.
8. **ENG-2**: PHLOX-STATS (unblocks Top Scripts reads).
9. **ENG-3**: PHLOX-SUSPEND (depends conceptually on ENG-2's stats story).
10. **EXP-A..G**: Experience system in dependency order: schema migration (6-table, reconcile `experience_kv` naming) → service+interface replace (touches existing Tranquillity connectors/handlers — the highest-friction step) → EXP-PERSIST-1 → Phlox KV async conversion → caps module (convert to INonSharedRegionModule) → parcel/region enforcement (block-wins + precedence) → console/test commands. Estimated 40-60 h total.

## Open questions for John

1. **IsFriendWithPerms UUID-swap**: no such delta exists between the trees today. Was this fixed pre-snapshot (buried in `28fbc70908`) and independently upstream, or does it refer to a different call site (e.g., inside FriendsModule.GetRightsGrantedByFriend)? Clarify or drop from the list.
2. **moving_start/end and bot framework are already in local develop's port series** despite being flagged Legion-exclusive. Strip before PRing to Mike, or offer them?
3. Experience KV: port Legion's **8 functions (610-617)** as-is including the non-SL `llClearKeyValue`, or hold 617 back?
4. Tranquillity's existing partial Experience stack (different IExperienceService + v3/v4 migrations): replace outright (recommended) or adapt Legion to its table/interface names? Replacing means writing a v4→v5+ migration for any grid already running the stub tables.
5. PERSIST-1.1-TX: propose to Mike as PR, or keep Legion-side until it has more live soak time (deployed to Legion grid 3 days ago)?
6. The 9 local develop commits are unpushed to `origin` (John's fork) as far as this audit observed — intended, or should they be pushed for backup?
