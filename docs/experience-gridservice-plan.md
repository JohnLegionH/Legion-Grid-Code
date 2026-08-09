# Legion Experience — Grid-Service Rebuild Plan

**Date:** 2026-07-21 · **Read-only for the decisions; this is the ordered build plan. NO code written yet.**
**Basis:** `experience-reference-design.md` (reference-correct = Legion's verified behavior on a grid-service topology). **John CONFIRMED the topology rebuild** (Legion runs hypergrid already + ships as a distribution for multi-host operators).
**Goal:** promote Legion's `ExperienceService` from region-local-direct-MySQL to a Robust grid service **without touching one line of Experience behavior**, adopting Tranquillity's (StolenRuby/NGC) connector topology.

**The core fact that makes this safe:** *every* piece of Legion's Experience behavior already sits **above** the `IExperienceService` interface. The region caps module holds the service as `IExperienceService m_Service` (`ExperienceModule.cs:51`) and only touches it through that interface; the async-KV wrapper, the consent flow, and the error table all live in the Phlox LSL layer and the caps handlers, which call `m_Service.*`. The *only* place the concrete class is named is one line — `ExperienceModule.cs:110 m_Service = new ExperienceService(m_ConnectionString)`. **Change what implements `IExperienceService` in the region and 100% of behavior is preserved by construction.**

---

## STEP 1 — The 6 open questions, and whether John's decisions resolve them

The 6 questions **verbatim** from `experience-reference-design.md` → "Open questions for John's design decision":

> **1. Topology commitment (the central decision):** Do you want Legion's Experience to *be* reference-correct (promote `ExperienceService` to a Robust grid service via Tranquillity's connector shell), or is region-local-shared-DB acceptable for Legion's deployment reality? … is Legion ever going to be **multi-host or hypergrid-facing** for Experience data? If yes → do the topology rebuild.
>
> **2. If yes to (1):** adopt Tranquillity's four connector classes as the shell but keep Legion's `IExperienceService`/schema/behavior — confirm this over the alternative of writing a fresh connector from scratch … Recommendation: adopt-and-adapt the shell, credit StolenRuby/NGC.
>
> **3. Crediting:** if any of Tranquillity's connector topology is reused, credit **StolenRuby** (original scaffold) and **Mike Dickson / OpenSim-NGC (Utopia Skye)** (integration). No behavioral code should be credited/propagated.
>
> **4. Do NOT treat Tranquillity's "~80% conformant" framing as license to backport its behavior** — every behavioral difference is a regression vs Legion (Part B). This should be recorded so the port audit's stale "stub" note and the scope doc's "80%" note don't get re-litigated.
>
> **5. Live-SL trace access (D-SLTEST):** the reference model's few remaining unverifiable edges (empty-value KV read, CAS-vs-empty-original, ladder tie-breaks, RegionExperiences error shape) are the same DEFERRED-BY-DECISION tail in Legion's ledger — unchanged by this research; still need a Premium trace.
>
> **6. ExperienceQuery (CAP-EQ):** neither fork implements it meaningfully … it couples to per-agent EEP injection, which Legion stubs. Confirm it stays a documented no-op … until EEP injection exists.

**Resolution against John's decisions:**

| # | Question | John's decision | Status |
|---|---|---|---|
| **1** | Topology commitment | **Q-multihost: YES** — do the rebuild (hypergrid live, multi-host distribution) | ✅ **RESOLVED** — this plan exists because of it |
| **2** | Adopt Tranquillity's shell vs write fresh | **Adopt the connector topology/protocol shape**, keep Legion's interface/schema/behavior; **build BOTH local + remote connectors**, config-selected | ✅ **RESOLVED** — and sharpened: adopt the *pattern/shape*, not literal code (interfaces differ — 33 Legion methods vs Tranquillity's 9 grouped verbs), so it's pattern-credit not code-copy |
| **3** | Crediting | **Credit StolenRuby / Mike Dickson / NGC** in headers + commits for the adopted topology; new work under Legion's banner | ✅ **RESOLVED** — see Attribution Plan below |
| **4** | Don't backport behavior | Implicit in "keep ALL Legion behavior; only the access path changes" | ✅ **RESOLVED / AFFIRMED** — this plan changes zero behavior; recorded here so it isn't re-litigated |
| **5** | Live-SL trace tail | *(not addressed — and correctly so)* | ⚪ **ORTHOGONAL** — behavioral tail, untouched by a topology rebuild. Stays DEFERRED-BY-DECISION in the ledger. **No new decision needed for this rebuild.** |
| **6** | ExperienceQuery no-op | *(not addressed — and correctly so)* | ⚪ **ORTHOGONAL** — behavioral, carries over unchanged (still a documented no-op). **No new decision needed for this rebuild.** |

**Two extra John-decisions this task added, mapped:**
- **Data/migration: PRESERVE data — 8-table schema stays, only the access path changes.** ✅ **Confirmed a plumbing change, NOT a schema migration.** The 8 tables (`experiences`, `experience_permissions`, `experience_keyvalue`, `experience_allowed`, `experience_blocked`, `experience_trusted`, `experience_agent_blocked`, `script_experiences`) are created/owned by `ExperienceService` exactly as today. What changes is *who opens the MySQL connection*: in **local** mode the region still does (identical to today); in **remote** mode **Robust** does and regions talk HTTP. → The only "migration" is an **ops config move** of the `[Experience]` connection string from region `.ini` to Robust `.ini` when an operator switches a region to remote mode. Zero rows touched, zero schema change. (Flagged as an ops step in G3.)
- **Robust deployment: service runs IN Robust, regions are clients.** ✅ Consistent — this is precisely the Local/Remote connector pattern (G2/G3). `ExperienceService` already lives in its own assembly (`OpenSim.Services.ExperienceService.dll`, per the finish-plan deploy set), so Robust can load it as a `LocalServiceModule` with no repackaging.

**Verdict:** John's decisions **fully resolve the three topology questions (1, 2, 3) and affirm (4).** (5) and (6) are behavioral items that a topology rebuild does not touch — they remain exactly as the ledger has them and need no new decision here. **Nothing in the 6 is left dangling.** The only genuinely-new choices are small implementation defaults (wire-verb granularity, default-connector-when-unconfigured), addressed in "Before G1" below.

---

## STEP 2 — Keep / rebuild component map

**Legend:** 🟢 UNCHANGED (behavior — do not touch) · 🟠 WRAPPED (same code, now reached through the connector) · 🔵 NEW (topology).

### 🟢 UNCHANGED — all of Legion's verified behavior (zero edits)

| Component | File(s) | Why untouched |
|---|---|---|
| Service logic (33-method impl) | `OpenSim/Services/ExperienceService/ExperienceService.cs` | The connectors *call* it; they don't replace it. In local mode the region news it; in remote mode Robust news it. Same class, same code. |
| The 8-table schema + bootstrap | `ExperienceService.cs` (idempotent `CREATE TABLE` bootstrap) | Plumbing change, not migration (STEP 1). Schema is authoritative and stays. |
| `IExperienceService` contract | `OpenSim/Services/Interfaces/IExperienceService.cs` (33 methods, 5 groups) | This *is* the seam. It stays byte-identical; the connectors implement it. |
| `ExperienceInfo` DTO + XP_ERROR codes | `OpenSim/Services/Interfaces/ExperienceInfo.cs` | Serialized across the wire in G2/G3, but the type is unchanged. |
| All 14 cap handlers + wire shapes | `OpenSim/Region/CoreModules/Experience/ExperienceModule.cs` (caps bodies) | They call `m_Service.*` through the interface — indifferent to local vs remote. |
| Async dataserver KV | Phlox `LSLSystemAPI.cs` (KV 610–617 wrappers) | Sits **above** `IExperienceService`; calls `CreateKeyValue`/etc. on a `Task.Run` then posts `dataserver`. Unchanged. (Note: in remote mode each call becomes an HTTP round-trip inside that Task — the async model already absorbs the latency; perf note in G4.) |
| Real consent flow | Phlox `LSLSystemAPI.cs` `llRequestExperiencePermissions` + `LLClientView` ScriptQuestion/Experience block | Calls `GrantPermission`/`DenyPermission` through the interface. Unchanged. |
| SL error table 0–18, 128 MiB quota + code 11, maturity 13/21/42, group-power union | LSL layer + caps + service | All above or inside the unchanged service. Unchanged. |

### 🟠 WRAPPED — the one seam that moves

| Component | Current (`ExperienceModule.cs`) | After |
|---|---|---|
| Service acquisition | `:110 m_Service = new ExperienceService(m_ConnectionString);` then `:113 scene.RegisterModuleInterface<IExperienceService>(m_Service);` | ExperienceModule (caps) **stops newing/registering** the service; instead does `m_Service = scene.RequestModuleInterface<IExperienceService>();`. Provisioning moves to the **connector** (below). Caps bodies unchanged. |

### 🔵 NEW — the grid-service topology (adopted from Tranquillity's shape, Legion's own code)

Mirror Legion's **existing** idiomatic pattern (16 services already do this: `ServiceConnectorsOut/{AgentPreferences,Grid,Inventory,…}`, `Server/Handlers/*`, `Services/Connectors/*`). Smallest existing template to copy: **AgentPreferences** (`LocalAgentPreferencesServiceConnector.cs` + `RemoteAgentPreferencesServiceConnector.cs`).

| New file | Role | Topology reference (Tranquillity) |
|---|---|---|
| `OpenSim/Region/CoreModules/ServiceConnectorsOut/Experience/LocalExperienceServiceConnector.cs` | `ISharedRegionModule, IExperienceService`. In-process: news `ExperienceService(connString)`, registers itself as `IExperienceService`. **= today's behavior, single-operator path.** | `/d/tranquillity-develop/.../ServiceConnectorsOut/Experience/LocalExperienceServiceConnector.cs` |
| `OpenSim/Region/CoreModules/ServiceConnectorsOut/Experience/RemoteExperienceServiceConnector.cs` | `ISharedRegionModule, IExperienceService`. Delegates every method to `ExperienceServicesConnector` (HTTP). **= multi-host/hypergrid path.** | `.../ServiceConnectorsOut/Experience/RemoteExperienceServiceConnector.cs` |
| `OpenSim/Services/Connectors/Experience/ExperienceServicesConnector.cs` | The HTTP **client**: POSTs `ServerURI + "/experience"` with a `METHOD` verb per call, (de)serializes `ExperienceInfo`/lists. | `.../OpenSim.Services.Connectors/Experience/ExperienceServicesConnector.cs` |
| `OpenSim/Server/Handlers/Experience/ExperienceServerConnector.cs` | Robust plumbing: registers the `/experience` POST stream handler; loads `ExperienceService` as `LocalServiceModule`. | `.../OpenSim.Server.Handlers/Experience/ExperienceServerConnector.cs` |
| `OpenSim/Server/Handlers/Experience/ExperienceServerPostHandler.cs` | The Robust **server**: dispatches the `METHOD` verb → the matching `IExperienceService` call → serializes the reply. | `.../OpenSim.Server.Handlers/Experience/ExperienceServerPostHandler.cs` |

Config knob (new, mirrors every other service): `[Modules] ExperienceServices = "LocalExperienceServiceConnector" | "RemoteExperienceServiceConnector"`; remote adds `[Experience] ExperienceServerURI = ${Const|BaseURL}:${Const|PublicPort}`; Robust adds an `[ExperienceService]` section + the handler registration in `Robust.ini`.

**Wire-verb note:** Tranquillity grouped its 33-ish operations into **9 coarse METHOD verbs**. Legion's interface is finer (33 distinct methods). Recommendation: **one METHOD verb per interface method** (simpler, mechanical, matches OpenSim's other PostHandlers) rather than replicating Tranquillity's grouping. Minor; decided at G2, not a blocker.

---

## STEP 3 — Ordered build plan (individually buildable + testable; natural break points)

Every slice leaves the grid **working and testable**, and behavior **never regresses** (the local path is behavior-identical to today throughout G1–G2; remote is opt-in at G3).

### G1 — Extract `IExperienceService` behind a **Local** connector *(safest first step; zero behavior change)* ⏸ BREAK POINT

> **✅ G1 COMPLETE (2026-07-21, committed on `slua-tier2-tables`; NOT pushed/deployed).**
> - **New:** `OpenSim/Region/CoreModules/ServiceConnectorsOut/Experience/LocalExperienceServicesConnector.cs` — `ISharedRegionModule, IExperienceService`; constructs one shared `ExperienceService(ConnectionString)` in `Initialise` (same [Experience] section, same ctor, region-side) and forwards all 40 interface methods/overloads as pass-throughs. **Default-to-Local:** activates when `[Modules] ExperienceServices` is unset (existing grids need zero config); stays dormant if a different connector is named. Header credits StolenRuby / Mike Dickson / OpenSim-NGC (Utopia Skye) for the topology pattern; implementation Legion's.
> - **Seam rewired (`ExperienceModule.cs`):** `AddRegion` no longer does `new ExperienceService(...)` / `RegisterModuleInterface<IExperienceService>` (`:110/:113` removed); `RegionLoaded` now acquires `m_Service = scene.RequestModuleInterface<IExperienceService>()` (load-order-safe; logs if null); `RemoveRegion` no longer unregisters the service (connector owns it); dead `using OpenSim.Services.ExperienceService;` removed.
> - **Behavior trace (unchanged):** cap (e.g. GetExperienceInfo) → `ExperienceModule` handler → `m_Service` (now the connector) → `LocalExperienceServicesConnector` pass-through → **the same `ExperienceService`** → same 8 tables, same MySQL connection string, same result. One shared service instance (was per-region); no wire, no Robust, no schema change.
> - **Build:** 0 errors, **64 warnings (delta 0 from baseline)**. **Deploy set: `OpenSim.Region.CoreModules.dll` ONLY** — no interface/service/schema files touched, no matched set. John deploys + restarts; verify by confirming existing Experience still works end-to-end (touch the Test experience script → consent dialog → GRANTED, KV, tabs — all identical to before).
- **Builds:** `LocalExperienceServiceConnector` (news `ExperienceService`, registers `IExperienceService`). Rewire `ExperienceModule` to **consume** the registered interface (`RequestModuleInterface`) instead of newing it (`:110`/`:113`). Add `[Modules] ExperienceServices` with **default = Local when unset** (so existing grids behave identically with no config edit).
- **Depends on:** nothing.
- **Verified:** grid boots; `IExperienceService` resolves; run the full in-world milestone (create/acquire experience → edit profile → compile script into it → touch → consent Yes/No/Block → KV read/write → group/admin tabs). **Everything must behave exactly as today** — this slice proves the seam with no functional change. Regression bar: the parity-ledger's end-to-end test passes unchanged.
- **Deploy/matched-set:** `OpenSim.Region.CoreModules.dll` only (new connector + module edit). **No interface change → no wide matched set.** Config optional (local default).
- **Why first:** it's the whole architectural change *de-risked to a no-op* — if anything breaks here, it's the seam, caught before any wire code exists.

### G2 — Robust **ServerConnector + POST handler** (the wire protocol, adopting Tranquillity's shape) ⏸ BREAK POINT

> **✅ G2 COMPLETE (2026-07-21, committed on `slua-tier2-tables`; NOT pushed/deployed).**
> - **New (`OpenSim/Server/Handlers/Experience/`):** `ExperienceServiceServerConnector.cs` (Robust IN connector — loads `ExperienceService` via `[ExperienceService] LocalServiceModule`, registers `POST /experience`; mirrors `GridUserServiceConnector`) + `ExperienceServerPostHandler.cs` (**40-verb** `METHOD` dispatch over the full `IExperienceService`, `ServerUtils` form-in/XML-out).
> - **Changed:** `ExperienceInfo.cs` gains lossless `ToDictionary`/`FromDictionary` (raw-field wire, shared by G2 server + G3 client — NOT the viewer-shaped caps OSD); `ExperienceService.cs` gains an `(IConfigSource)` ctor reading `[ExperienceService] ConnectionString` for Robust `LoadPlugin`. Both additive; the string ctor / Local path is untouched.
> - **Regions unchanged:** G2 is server-side only. Regions still use the G1 Local connector; no region DLL changes, no config forces remote. The grid behaves identically; the service is now *also* reachable over HTTP.
> - **Wire shape** adopts the Tranquillity/NGC `POST /experience` + `METHOD`-verb protocol (which is also Legion's native `ServerUtils` Robust convention). Credit StolenRuby / Mike Dickson / NGC in headers.
> - **Build:** 0 errors; new files add **0 warnings** (solution 64→62). New types verified present in `OpenSim.Server.Handlers.dll` + `OpenSim.Services.Interfaces.dll` (footgun check passed — csproj Compile entries added since `EnableDefaultItems=false`).
> - **Deploy set (Robust host):** `OpenSim.Services.Interfaces.dll` + `OpenSim.Services.ExperienceService.dll` + `OpenSim.Server.Handlers.dll` (all additive/ABI-safe). **No addin-db clear** (the ServerConnector is loaded by Robust from its config connector string, not a Mono.Addins `[Extension]`). **Config:** add the `[ServiceConnectors]` line + `[ExperienceService]` section to `Robust.ini` (see report). Regions need nothing.
> - **Test:** `curl` POST `/experience` with `METHOD=findexperiences&QUERY=` → valid `<ServerResponse>` (proves the endpoint); in-world touch "Test" → GRANTED (proves regions still on Local, no regression).
- **Builds:** `ExperienceServerConnector` + `ExperienceServerPostHandler` (server side) and the **`ExperienceServicesConnector` wire contract** it must satisfy (define the verb table + serialization both ends here so the shapes match; the client is wired into regions in G3). Robust `.ini`: `[ExperienceService]` (LocalServiceModule + `[Experience]` connString) + register the handler.
- **Depends on:** G1 (the interface is the dispatch surface).
- **Verified:** start Robust with the handler; **regions still run Local (unaffected)**. Test the service in isolation over HTTP — `curl`/POST `/experience` with a `METHOD=getexperienceinfos` (and a spread across the 5 groups: a CRUD read, a permission check, a KV read, a region-list read, a script-assoc read) and assert the LLSD replies match what the local service returns for the same inputs. **Grid behavior unchanged** (nothing consumes the endpoint yet).
- **Deploy/matched-set:** Robust binaries — `OpenSim.Server.Handlers.dll`, `OpenSim.Services.Connectors.dll`, `OpenSim.Services.ExperienceService.dll`. Regions untouched.

### G3 — **Remote** connector + config selection (regions can call the service over HTTP) ⏸ BREAK POINT

> **✅ G3 COMPLETE (2026-07-22, committed on `slua-tier2-tables`; NOT pushed/deployed).**
> - **New:** `OpenSim/Services/Connectors/Experience/ExperienceServicesConnector.cs` — `BaseServiceConnector, IExperienceService` HTTP client; all **40** verbs POST form to `/experience` (`ServerUtils.BuildQueryString` + `SynchronousRestFormsRequester.MakeRequest`) and parse G2's XML via `ParseXmlResponse` → the three shapes (list `exp0…`/single `RESULT`/`NULL`), reusing **`ExperienceInfo.FromDictionary`** (exact inverse of G2's `ToDictionary`). `OpenSim/Region/CoreModules/ServiceConnectorsOut/Experience/RemoteExperienceServicesConnector.cs` — `ISharedRegionModule, IExperienceService`; activates **only** when `[Modules] ExperienceServices = "RemoteExperienceServicesConnector"`, delegates to the client. Mirrors `GridUserServicesConnector`/`RemoteGridUserServicesConnector`.
> - **DEFAULT-TO-LOCAL:** unset selector → G1 Local serves (existing grids unchanged). Remote is strict opt-in (`name != Name → return`). Remote ≡ Local behaviorally (same central `ExperienceService`, same DB).
> - **Config to flip one region remote (region OpenSim.ini):** `[Modules] ExperienceServices = "RemoteExperienceServicesConnector"` + `[ExperienceService] ExperienceServerURI = "http://<host>:8003"`. Documented in `OpenSim.ini.example` `[Experience]`.
> - **Build:** 0 errors; new files add **0 warnings** (solution 64). Types verified in `OpenSim.Services.Connectors.dll` + `OpenSim.Region.CoreModules.dll` (footgun: CoreModules Compile entry added; Services.Connectors is SDK-glob).
> - **Deploy set (region side):** `OpenSim.Services.Connectors.dll` + `OpenSim.Region.CoreModules.dll`. No interface change (reuses G2's `ExperienceInfo`). **addin-db clear recommended** on regions (new `[Extension]` region module). Deploy is safe — default-Local means nothing changes until a region opts into remote.
> - **Test:** flip one region to remote, leave another Local, restart; Experience must behave identically on both (touch→consent→GRANTED, floater lists, create/edit); curl still works.
- **Builds:** `RemoteExperienceServiceConnector` (region module delegating to `ExperienceServicesConnector`). Wire `[Modules] ExperienceServices = "RemoteExperienceServiceConnector"` + `[Experience] ExperienceServerURI`. **Ops step (STEP 1 migration note):** when switching a region to remote, move the `[Experience]` connString from the region `.ini` to Robust's.
- **Depends on:** G2 (endpoint must exist).
- **Verified:** flip **one test region** to Remote pointing at Robust; leave others Local. Re-run the full milestone **through the remote path** and diff behavior against a Local region — caps, KV async round-trip, consent, region lists must all match. Confirms the ~33-method wire is complete and correct. Behavior parity is the pass bar.
- **Deploy/matched-set:** regions gain `OpenSim.Region.CoreModules.dll` (new remote connector) + `OpenSim.Services.Connectors.dll`. **Interface unchanged** across G1–G3 → connectors are additive; no forced lockstep with unrelated assemblies. Local-mode regions need no redeploy to keep working.

### G4 — Multi-host / hypergrid validation + caps through the remote path ⏸ FINAL
- **Builds:** ideally **no new code** — config + validation. Only add code if HG surfaces a real gap (see the HG sub-question below).
- **Depends on:** G3.
- **Verified:** (a) **multi-host:** two region hosts, both Remote → one Robust; confirm a KV write on host A is readable on host B and a consent grant on A is honored on B (proves the store is genuinely central, the whole point). (b) **hypergrid:** an HG visitor touches an experience object on a Legion region — confirm the experience *resolves* (GetExperienceInfo) and permission/consent behaves per the decided HG policy. (c) **perf:** confirm KV/consent latency under the HTTP path is acceptable (the async dataserver model should hide it; measure).
- **Deploy:** config/docs; any HG-policy code is its own matched set if needed.

**Parallelism/breaks:** G1→G2→G3 are strictly ordered (each needs the prior). G4 follows G3. Natural stopping points after **each** slice — the grid is fully working and shippable at G1 (behavior-identical), G2 (endpoint live, unused), and G3 (remote opt-in proven). John can take a break after any slice without a half-built grid.

---

## Attribution plan

- **New connector/handler files** (`LocalExperienceServiceConnector`, `RemoteExperienceServiceConnector`, `ExperienceServicesConnector`, `ExperienceServerConnector`, `ExperienceServerPostHandler`) carry a header crediting the **topology/protocol pattern** to **StolenRuby** (original OpenSim-NGC Experience connector scaffold) and **Mike Dickson / OpenSim-NGC (Utopia Skye)** (integration), with a line noting the implementation is Legion's own (adapted to Legion's 33-method `IExperienceService`, not a code copy). Example: `// Grid-service connector topology adapted from the OpenSim-NGC/Tranquillity Experience stack (orig. StolenRuby, integ. Mike Dickson/Utopia Skye). Implementation: Legion Grid.`
- **Commit messages** for G2/G3 reference the NGC/StolenRuby origin of the pattern.
- **Behavioral code is neither credited nor propagated** — none of Tranquillity's behavior (sync KV, auto-grant, error codes, quota, cap handlers) enters Legion (STEP 1, Q4). Credit attaches only to the connector *shape*.
- Record a one-line pointer in the parity-ledger's Observations that OBS-1 is being resolved by this rebuild, crediting the adopted pattern.

---

## Before G1 — decisions/checks (none block starting; all have safe defaults)

1. **Default connector when `[Modules] ExperienceServices` is unset → Local.** *(Recommend: yes — existing grids keep working with zero config edit.)* Needed before G1; obvious default, proceed unless John objects.
2. **Wire-verb granularity (G2):** one METHOD per interface method (recommended) vs Tranquillity's coarse grouping. Not a G1 blocker; decide at G2.
3. **Shared vs per-region service instance:** the Local connector as `ISharedRegionModule` gives **one** `ExperienceService` per simulator (vs today's per-region instance, all on the same DB). Behavior-identical, marginally cleaner. Proceed as shared (matches every other OpenSim service connector).
4. **Hypergrid experience policy (G4, not G1):** does an HG visitor receive experience permissions/consent on the host grid's experiences, and how are foreign-owned experiences resolved? SL has no HG; this is a Legion-original policy question. **Flag for John at G4** — does not block G1–G3 (local + same-grid remote don't need it).
5. **Confirm `ExperienceService` is already its own assembly** (`OpenSim.Services.ExperienceService.dll`) so Robust loads it as `LocalServiceModule` — finish-plan deploy set indicates yes; verify at G2 kickoff (if not, split it out — small, mechanical).

**Nothing in this list blocks G1.** G1 is a pure seam extraction with a behavior-identical local path and is safe to start immediately.

---

### Source seams cited
- Legion: `OpenSim/Region/CoreModules/Experience/ExperienceModule.cs:51,110,113,214,1208`; `OpenSim/Services/Interfaces/IExperienceService.cs` (33 methods); `OpenSim/Services/ExperienceService/ExperienceService.cs`; existing pattern `OpenSim/Region/CoreModules/ServiceConnectorsOut/AgentPreferences/{Local,Remote}AgentPreferencesServiceConnector.cs`, `OpenSim/Server/Handlers/AgentPreferences/`, `OpenSim/Services/Connectors/AgentPreferences/`.
- Tranquillity topology reference: `/d/tranquillity-develop/Source/OpenSim.Region.CoreModules/ServiceConnectorsOut/Experience/{Local,Remote}ExperienceServiceConnector.cs`, `/d/tranquillity-develop/Source/OpenSim.Server.Handlers/Experience/{ExperienceServerConnector,ExperienceServerPostHandler}.cs`, `/d/tranquillity-develop/Source/OpenSim.Services.Connectors/Experience/ExperienceServicesConnector.cs`.
- Decisions basis: `experience-reference-design.md`; behavior inventory: `experience-parity-ledger.md`.
