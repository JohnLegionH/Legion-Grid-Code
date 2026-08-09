# OpenSim Experience — Reference-Design Research

**Date:** 2026-07-21 · **Read-only research; no code changed.** · Output for John's architecture decision.
**Question:** What is the *reference-correct* OpenSim Experience architecture — the one Legion's proper Experience should be built to — established from **evidence** (SL's documented model + what actually exists in the fork landscape), rather than from assuming either Legion's or Tranquillity's design is right?

**Evidence base:**
- **SL documented model:** SL wiki (`llCreateKeyValue`, `llReadKeyValue`, `llUpdateKeyValue`, `llDataSizeKeyValue`, `llGetExperienceErrorMessage`, `llRequestExperiencePermissions`), SL Knowledge Base "Experiences in Second Life", "Experience Tools" category — fetched 2026-07-21 (cited inline).
- **Legion:** `experience-parity-ledger.md`, `experience-caps-audit-v1.md`, `experience-conformance-audit-v1.md`, `experience-finish-plan-v1.md` (all Legion-vs-secondlife/viewer `main` + Firestorm 7.2.2, read-only).
- **Tranquillity (NGC):** deep read of `/d/tranquillity-develop/Source/**` Experience stack + `experience-port-audit-v2.md`/`-plan-v1.md`/`-scope.md`, plus git provenance (this session).
- **Landscape:** OpenSim wiki, opensim-dev list (2017 Experience thread), secondlife community/wiki.

---

## Bottom line (read this first)

**Neither Legion's nor Tranquillity's Experience is, by itself, reference-correct — and they are almost perfectly complementary.**

- **Legion has the correct *behavior*** (all 14 caps served with wire shapes verified against the actual viewer source; real SL consent dialog; async dataserver KV; SL-correct error table 0–18; 128 MiB quota enforced with code 11) **but the wrong *topology*** — its `ExperienceService` is instantiated **per-region directly against MySQL, with no Robust connector** (ledger OBS-1). It is grid-consistent only because all regions share one `[Experience]` connection string.
- **Tranquillity has the correct *topology*** (a genuine central **grid service** reached over Robust via the standard Local/Remote connector + ServerConnector + ServicesConnector pattern — this is exactly the shape SL uses and the OpenSim community specified in 2017) **but SL-*shaped*, not SL-*correct* behavior** — synchronous KV (breaks SL's async contract), auto-grant consent (no dialog), non-standard error codes, a 16 MiB quota reported inconsistently, and several cap handlers with `todo`/hardcoded shortcuts. It is an **unmaintained 2024 "StolenRuby" scaffold**, not hardened production code.

**The reference-correct design is: Legion's behavior layer hosted on Tranquillity's grid-service topology.** In component terms — **keep essentially all of Legion's Experience (caps, KV, consent, codes, quota, schema, service surface) and adopt only Tranquillity's *connector/service shell*** (the four Local/Remote/Server/Services connector classes) to promote Legion's region-local `ExperienceService` into a proper Robust grid service. Do **not** backport Tranquillity's behavior — every behavioral difference is Tranquillity being less correct than Legion.

**Is any existing implementation already reference-correct? No.** Stock OpenSimulator has **no** Experience system at all. Halcyon/InWorldz never had SL Experiences (they predate the feature; their Phlox engine — which both Legion and Tranquillity reuse — supplies the VM, not the Experience KV). Tranquillity is the only fork with a grid-service Experience, and it is correct-*looking*-but-flawed. Legion is the only implementation that is behaviorally SL-conformant, and it is region-local. **The proper build is a merge, not an adoption of either.**

Credit where due: Tranquillity's grid-service scaffold originates from **StolenRuby** (origin commit `26d3971448`, 2024-08-20, "Local branch for StolenRuby experience changes (#86)"), integrated by **Mike Dickson / Utopia Skye (OpenSim-NGC)**. The *architecture* is worth crediting and reusing; the *behavior* is not.

---

# PART A — The landscape + SL's actual documented model

## A.1 Fork / Experience landscape

| Distribution | Experience implemented? | Architecture | SL-conformance | Source |
|---|---|---|---|---|
| **Stock OpenSimulator** (core) | **No** — none of the experience KV functions, experience permissions, or an experience service exist | n/a | n/a — the whole feature is a documented gap vs SL | OpenSim wiki `LSL_Status/Functions`; opensim-dev 2017 thread frames it as unbuilt |
| **OpenSim-NGC / Tranquillity** | **Yes** — a real, running grid-service Experience stack (13 of 14 caps) | **Central grid service** over Robust (Local/Remote/Server/Services connectors) | **SL-shaped, not SL-correct** (sync KV, auto-grant, wrong codes, 16 MiB quota, TODO caps) | `/d/tranquillity-develop` code read (this session) |
| **Legion** | **Yes** — all 14 caps + full script surface, behaviorally SL-conformant | **Region-local**, per-region direct-MySQL, no Robust connector (OBS-1) | **Behaviorally SL-conformant** (verified vs viewer source) but wrong topology | Legion audits (this repo) |
| **Halcyon / InWorldz** (heritage) | **No** — predates SL Experiences (2014–15); never implemented them | n/a | n/a | Halcyon repo; the Phlox engine supplies the VM only |
| **DreamGrid / OSGrid / Diva etc.** (distributions of core) | **No** — they package stock OpenSim, which has no Experiences | n/a | n/a | Outworldz/OSGrid docs; inherit core's gap |

**Key landscape facts:**
1. **Experiences are a fork-only feature in the OpenSim world.** Core never implemented them. So there is no "canonical OpenSim reference" to defer to — the reference has to come from SL's documented model plus the two forks that actually built something (Legion, Tranquillity).
2. **Both Legion and Tranquillity build on the same InWorldz *Phlox* engine** — so their *script-layer* KV functions live in the same file lineage (`InWorldz.Phlox … LSLSystemAPI.cs`). This is why they are directly comparable and why porting between them is mechanically feasible. But the KV *store* and the Experience *service* are new work in each fork, not inherited from Halcyon.
3. **The OpenSim community's own design intent (opensim-dev, July 2017)** was explicitly a **new grid service** ("the experience server") plus viewer/caps tie-ins and auto-granted permissions. Tranquillity's topology follows that intent; Legion's region-local approach is a pragmatic shortcut from it.

## A.2 SL's actual documented model (what "reference-correct" means)

This is SL's real, documented architecture — the target — assembled from the SL wiki and KB (cited):

**Storage topology — central, grid-wide, per-experience.**
- Experience permissions are **persistent and apply to every script in the experience, grid-wide**: once a resident allows/blocks an experience, "subsequent calls from scripts in the experience will receive the same response automatically with no user interaction" (SL KB, *Experiences in Second Life*). A resident's decision is a single grid-wide fact, not a per-region one → implies a **central experience/permission service**, not region-local state.
- The KV store is **per-experience and grid-wide**: any script compiled into experience X, on any region, reads/writes the same key space → again a **central store**.
- SL scope caveat worth recording: SL experience *land enablement* is region/parcel-scoped (there is no "run everywhere" grid key), but the *permission grant* and the *KV data* are grid-wide. So "grid service" ≠ "runs everywhere"; it means the **data and consent state are centrally owned**.

**Key-value store contract (SL wiki, verified 2026-07-21):**
- **Async dataserver model.** `llCreateKeyValue`/`llReadKeyValue`/etc. **return a request key immediately**, and the result arrives later via a `dataserver(key queryid, string data)` event, where `data` is CSV: `"1,<value>"` on success or `"0,<XP_ERROR>"` on failure. This is a hard part of the contract — scripts written for SL listen in `dataserver`.
- **Quota: 128 MiB per experience.** Exceeding it → **`XP_ERROR_QUOTA_EXCEEDED` (11)**.
- **Limits:** key ≤ **1011** bytes; value ≤ **4095** (Mono) / 2047 (LSO).
- `llDataSizeKeyValue` returns `used, TOTAL_quota` (second value is the total, i.e. 128 MiB, not remaining).

**Canonical XP_ERROR table (SL wiki `llGetExperienceErrorMessage`, verified 2026-07-21):**

| # | Constant | # | Constant |
|---|---|---|---|
| 0 | XP_ERROR_NONE | 10 | XP_ERROR_UNKNOWN_ERROR |
| 1 | XP_ERROR_THROTTLED | 11 | XP_ERROR_QUOTA_EXCEEDED |
| 2 | XP_ERROR_EXPERIENCES_DISABLED | 12 | XP_ERROR_STORE_DISABLED |
| 3 | XP_ERROR_INVALID_PARAMETERS | 13 | XP_ERROR_STORAGE_EXCEPTION |
| 4 | XP_ERROR_NOT_PERMITTED | 14 | XP_ERROR_KEY_NOT_FOUND |
| 5 | XP_ERROR_NO_EXPERIENCE | 15 | XP_ERROR_RETRY_UPDATE |
| 6 | XP_ERROR_NOT_FOUND | 16 | XP_ERROR_MATURITY_EXCEEDED |
| 7 | XP_ERROR_INVALID_EXPERIENCE | 17 | XP_ERROR_NOT_PERMITTED_LAND |
| 8 | XP_ERROR_EXPERIENCE_DISABLED | 18 | XP_ERROR_REQUEST_PERM_TIMEOUT |
| 9 | XP_ERROR_EXPERIENCE_SUSPENDED | | |

**Consent protocol (SL wiki `llRequestExperiencePermissions`; viewer source):**
- For a resident who has **not** already accepted the experience, the sim triggers a **consent dialog** in the viewer (the `ScriptQuestion` UDP message with an `Experience` block); the viewer resolves the experience via **GetExperienceInfo**, shows a Yes/No/Mute/**Block** dialog, and replies with **ScriptAnswerYes**. Accepted → `experience_permissions`; declined/blocked → `experience_permissions_denied`; timeout (**"at least 5 minutes"** → 300 s floor) → code **18**. Prior participants skip the dialog (grid-wide-persistent grant).
- The **Block** button routes through the **ExperiencePreferences** cap — i.e. the consent dialog and the preferences cap are one coupled system.

**Viewer/management surface — 14 simulator capabilities** (SL "Current Sim Capabilities"; wire shapes read from `secondlife/viewer main` + Firestorm 7.2.2 in Legion's caps-audit): GetExperienceInfo, FindExperienceByName, RegionExperiences, GetExperiences, AgentExperiences (GET + acquire POST), GetCreatorExperiences, GetAdminExperiences, ExperiencePreferences, GetMetadata, GroupExperiences, IsExperienceAdmin, UpdateExperience, ExperienceQuery, IsExperienceContributor. (One — IsExperienceContributor — the viewer requests but never calls; contributor status is inferred from GetCreatorExperiences.)

**Other documented specifics that a reference build must honor:**
- **Maturity encoded as SIM_ACCESS 13 / 21 / 42** (General/Moderate/Adult), not 0/1/2 — confirmed from Firestorm `indra_constants.h` + the profile floater's comparison logic. Sending 0/1/2 makes every experience display as General.
- Group-power model: contributor = owner ∪ group `GP_EXPERIENCE_CREATOR` (bit 50); admin = owner ∪ group `GP_EXPERIENCE_ADMIN` (bit 49).

## A.3 Is anything out there already reference-correct?

**No.** The reference model above requires simultaneously: (a) a central grid-wide store + permission service, (b) async dataserver KV, (c) real consent, (d) the correct error/quota/maturity vocabulary, and (e) all 14 caps in their verified wire shapes. **No single existing implementation has all five.** Tranquillity has (a) and most of the cap surface; Legion has (b)(c)(d)(e) and the correct service semantics. That is the entire finding of Parts B and C.

---

# PART B — Is Tranquillity's Experience genuinely correct, or correct-looking-but-flawed? (verified vs SL, not vs Legion)

**Short answer: its *architecture* is genuinely correct and reusable; its *behavior* is correct-looking-but-flawed and should not be propagated.** The scoping's "SL-faithful, ~80% conformant" is **half-right and half-misleading**: it's SL-faithful in *topology* and in *cap surface breadth*, but it is materially *non-conformant in the behaviors that scripts and residents actually observe*.

## B.1 The architecture claim — VERIFIED TRUE (and the older "stub" reading was wrong)

Tranquillity's Experience is a **real, end-to-end grid service** — not a stub. Verified in code this session:
- Full dual-connector stack: `RemoteExperienceServiceConnector` / `LocalExperienceServiceConnector` (region side), `ExperienceServerConnector` + `ExperienceServerPostHandler` (Robust server side), `ExperienceServicesConnector` (client). The remote connector genuinely `POST`s to `serverURI + "/experience"` and the server handler dispatches on a **METHOD verb table of 9 verbs** (`getpermissions`, `updatepermission`, `getexperienceinfos`, `updateexperienceinfo`, `findexperiences`, `getgroupexperiences`, `getagentexperiences`, `getexperiencesforgroups`, `accesskvdatabase`), each backed by a real service call.
- `ExperienceService.cs` has **17 real methods, zero `NotImplementedException`**; each delegates to an `IExperienceData` plugin; `MySQLExperienceData` implements all 15 data methods with real ADO.NET.

This **matches SL's central-service topology and the 2017 opensim-dev design intent.** Legion's own earlier `tranquillity-port-audit-v1.md` §C called this a "stub (10 KB, console cmds only)" — **that characterization was wrong** (it was written against an older commit / a shallow read); Tranquillity's own `experience-port-audit-v2.md` corrects it, and this session's code read confirms the v2 correction. **This is the one place where Tranquillity is genuinely more SL-faithful than Legion.**

## B.2 The "~80% conformant" claim — verified per-cap: which caps are genuinely SL-correct vs correct-looking-but-wrong

Spot-checking Tranquillity's wire shapes against SL (the way Legion's caps-audit did):

| Cap | Tranquillity state | Verdict vs SL |
|---|---|---|
| GetExperienceInfo | Emits `experience_keys` with maturity **13/21/42** (correct), but **`quota` hardcoded 128** while `FindExperienceByName` emits `quota=16` for the same experience; `marketplace` forced empty; `expiration` hardcoded 600 | **Correct-looking-but-wrong** — cross-cap quota contradiction; marketplace never displays |
| FindExperienceByName | Reads `page`/`page_size` then `// todo: handle pages` — **no pagination, no `next_page_url`** | **Flawed** (viewer pagination dead; identical gap Legion had pre-Slice-0) |
| RegionExperiences | **GET-only**; `blocked`/`default`/`disabled` **hardcoded `<undef/>`**; no POST | **Flawed** — cannot write region lists via cap; Legion's GET+POST is closer to SL |
| AgentExperiences | **GET-only, no acquire POST** | **Flawed** (Acquire button dead) |
| GetMetadata | `// todo: iterate over fields` — returns only the `experience` UUID, ignores requested fields | Adequate for the real viewer (only asks `experience`), but not field-accurate |
| IsExperienceAdmin / IsExperienceContributor | `{status:bool}` shape; owner ∪ group-power logic present | **Correct** (and it had the group-union before Legion did) |
| UpdateExperience | Maturity 13/21/42 mapping present; but **silently no-ops for non-admins** (no error) and lets **any admin change `group_id`** (not owner-restricted) | **Correct-looking-but-wrong** on authz edges |
| ExperienceQuery | **Absent entirely** (13 of 14 caps) | **Missing** (same one Legion defers) |

So of the 13 caps present, several that *look* served carry real defects. "~80% of the cap surface is present" is fair; "~80% conformant" overstates it, because the present-but-defective caps (Find, Region, Agent, GetMetadata, Update authz) are exactly the ones a conformance audit dings.

## B.3 Where it diverges from SL's *protocol* — the behaviors that matter most

These are not cosmetic; they change what scripts and residents observe:

1. **KV is synchronous, not async dataserver.** `llCreateKeyValue`/`llReadKeyValue`/etc. return the value/int **inline**; the "SL-flavored" wrappers just stringify the sync result into `"1,<value>"` CSV — they do **not** return a request UUID or fire a `dataserver` event. **Any SL script that does `key q = llReadKeyValue(...)` then waits in `dataserver(key id, string data)` breaks on Tranquillity.** This is a genuine correctness divergence from SL's documented contract. (Legion implements the real async model.)
2. **Consent is auto-granted.** `llRequestExperiencePermissions` never sends a `ScriptQuestion`/awaits `ScriptAnswerYes`; it calls `GrantPermission` and immediately posts `experience_permissions`. Residents are enrolled without agreeing — the inverse of SL's consent model. (Legion implements the real dialog.)
3. **Non-standard error codes.** Tranquillity emits 17 for generic not-permitted and 18 for not-found — but SL defines **17 = NOT_PERMITTED_LAND** and **18 = REQUEST_PERM_TIMEOUT** (§A.2 table). These are simply wrong against SL. (Legion's table is 0–18 SL-correct.)
4. **Quota is 16 MiB** (`MAX_QUOTA`), and reported inconsistently (128 in one cap, 16 in another). SL is **128 MiB / code 11**. (Legion enforces 128 MiB with code 11.)

## B.4 Provenance / build quality — would backporting propagate flaws?

- **Provenance:** the Experience stack is a **StolenRuby scaffold** (origin `26d3971448`, 2024-08-20) integrated by Mike Dickson / Utopia Skye (NGC). Since 2024 the core Experience files saw **only mechanical churn** (project restructures, LibOMV bumps) — **zero feature/conformance work.** It is a structural scaffold that runs, frozen at its 2024 shape.
- **Quality flags found in code:** a latent **SQL-injection pattern** (`IN(...)` clauses built by string concatenation in `GetExperienceInfos`/`GetExperiencesForGroups` — currently safe only because callers pre-parse to UUID); **orphaned EF entity POCOs** (Utopia Skye 2025) that nothing on the data path consumes (raw ADO.NET is used instead); a hardcoded magic permission mask `408628` with `// Todo: fix the enum`; LLSD responses built by string concatenation rather than serializers; inconsistent indentation.
- **Would backporting propagate flaws? Yes — for behavior.** Every behavioral piece of Tranquillity is at best equal to and mostly worse than Legion's. Backporting Tranquillity's *service/connector topology* (the genuinely good part) is safe and desirable; backporting its *behavior* (KV, consent, codes, quota, cap handlers) would regress Legion.

**Part B verdict:** Tranquillity's Experience is **correct-ARCHITECTURE, correct-LOOKING-but-flawed-BEHAVIOR.** Use it as the **topology** design basis and credit StolenRuby/NGC for that. Do **not** propagate its behavior into Legion.

---

# PART C — The reference-correct design, and what of Legion/Tranquillity maps onto it

## C.1 The target architecture

A reference-correct OpenSim Experience is a **central grid service** (SL-faithful topology + 2017 opensim-dev intent) wearing **Legion's SL-conformant behavior**:

```
                 ┌─────────────────────────────────────────────┐
                 │  ExperienceService  (central, Robust-hosted) │
                 │  - authoritative store: profiles, per-agent  │
                 │    permissions (grant/block), region lists   │
                 │    (allowed/blocked/trusted), script↔exp,     │
                 │    KV store (128 MiB/exp, code 11)            │
                 │  - richer IExperienceService surface (Legion) │
                 └───────────────▲─────────────────────────────┘
                                 │ Robust HTTP  (POST /experience, METHOD verbs)
        ┌────────────────────────┴───────────────────────────┐
        │ RemoteExperienceServiceConnector (grid mode)         │
        │ LocalExperienceServiceConnector  (standalone/in-proc)│
        └────────────────────────▲───────────────────────────┘
                                 │ IExperienceService (in-region interface)
   ┌─────────────────────────────┴──────────────────────────────┐
   │ Region: ExperienceModule (caps) + Phlox script API           │
   │  - all 14 caps, wire shapes verified vs viewer source        │
   │  - async dataserver KV (request-UUID → dataserver event)     │
   │  - real consent (ScriptQuestion/ScriptAnswerYes, 300 s, c.18) │
   │  - SL error table 0–18; maturity 13/21/42; group-power union │
   └──────────────────────────────────────────────────────────────┘
```

Components:
1. **Central `ExperienceService`** — the authoritative owner of every experience fact (profiles, permissions, region lists, script associations, KV). Reached over Robust so regions on separate hosts, and hypergrid, work correctly and grid-wide consistency is a service guarantee, not a "everyone shares one DB string" convention.
2. **Connector triple** — `Local…Connector` (standalone / in-process), `Remote…Connector` (grid, HTTP), `ExperienceServerConnector`/`PostHandler` (the Robust endpoint). This is the *only* piece Legion structurally lacks.
3. **Region caps module + Phlox script API** — the behavioral surface: all 14 caps in verified wire shapes, async KV, real consent, SL codes/quota/maturity.

## C.2 Component-by-component keep-vs-rebuild map (evidence-based, not global)

| Reference component | Legion has | Tranquillity has | **Decision for a proper Legion build** |
|---|---|---|---|
| **Connector/service topology** (Local/Remote/Server/Services) | ❌ none — region-local direct-MySQL (OBS-1) | ✅ full, real, working Robust stack | **ADOPT Tranquillity's connector shell** — the one genuine gain from Tranquillity; credit StolenRuby/NGC |
| **`IExperienceService` surface** | ✅ 29 methods (blocked-list, trusted, group queries, script-assoc, agent-block) | ~17 methods, narrower | **KEEP Legion's** richer interface; host it inside the adopted connector shell |
| **Schema** | ✅ 8 tables — separate `experience_allowed`/`_blocked`/`_trusted`/`_agent_blocked`/`script_experiences` (closer to SL's distinct lists) | 3 tables — single `allow BIT` for grant+block; region lists shoved into estate settings | **KEEP Legion's** schema; it models SL's separate allow/block/trusted lists faithfully |
| **KV store contract** | ✅ async dataserver, correct CSV, request-UUID | ❌ synchronous | **KEEP Legion's** async KV; discard Tranquillity's |
| **KV quota** | ✅ 128 MiB enforced, code 11 | ❌ 16 MiB, inconsistent | **KEEP Legion's** |
| **Consent model** | ✅ real ScriptQuestion/ScriptAnswerYes, 300 s, code 18, Block via ExperiencePreferences | ❌ auto-grant | **KEEP Legion's** |
| **Error table** | ✅ 0–18 SL-correct | ❌ non-standard (17/18/4 wrong) | **KEEP Legion's** |
| **14 caps + wire shapes** | ✅ all 14, verified vs `secondlife/viewer` + FS 7.2.2 | 13/14, several `todo`/hardcoded | **KEEP Legion's**; Tranquillity is reference-only |
| **Maturity 13/21/42** | ✅ (Slice 0) | ✅ | Converged — keep Legion's |
| **Group-power union** (creator/admin bits) | ✅ (Slice 4) | ✅ (had it first) | Converged — keep Legion's; note Tranquillity as prior-art confirmation |
| **Caps module lifetime** | `INonSharedRegionModule` (per-region) | `ISharedRegionModule` | Either composes with a grid service; **keep Legion's per-region caps** |
| **Acquire policy** | grid-config (DEC-3 deliberate deviation) | absent | Keep Legion's decision |

**Net:** **rebuild exactly one thing** — Legion's service *hosting* (region-local direct-MySQL → Robust grid service), by adopting Tranquillity's connector shell and re-pointing Legion's existing `ExperienceService`/`IExperienceService`/schema behind it. **Keep everything else of Legion's.** **Reuse essentially nothing of Tranquillity's behavior.**

## C.3 The one real judgment call: does Legion actually *need* the topology rebuild?

This is the crux for John, and the honest answer is **"it depends on deployment, and the gap is operational, not behavioral":**

- For a **single-operator grid where every region already shares one `[Experience]` MySQL** (Legion's current deployment), region-local direct-MySQL is **behaviorally equivalent to grid-wide** — the store *is* central, just reached by direct DB connection instead of HTTP. All the SL-observable behavior (grid-wide-persistent permissions, shared KV) already holds. OBS-1 is a latent risk, not an active defect.
- The Robust grid service becomes **necessary** when: regions run on hosts that shouldn't/can't hold the DB credential; the grid federates or scales to multiple DB backends; **hypergrid** visitors' experience state must be brokered; or Legion wants to match SL's topology on principle and expose Experience to non-Legion region servers. It's also the *cleaner* long-term architecture and the one the OpenSim community specified.

So: **reference-correct = grid service. Legion-today = behaviorally-correct-but-topologically-short.** The rebuild is worth doing to *be* reference-correct and to unlock multi-host/hypergrid, but it is not fixing a behavioral bug — which means it can be scheduled deliberately rather than urgently, and Legion's behavior layer carries into it unchanged.

---

## Open questions for John's design decision

1. **Topology commitment (the central decision):** Do you want Legion's Experience to *be* reference-correct (promote `ExperienceService` to a Robust grid service via Tranquillity's connector shell), or is region-local-shared-DB acceptable for Legion's deployment reality? Frame it by deployment: is Legion ever going to be **multi-host or hypergrid-facing** for Experience data? If yes → do the topology rebuild; if it's always one operator/one DB → OBS-1 stays an accepted observation.
2. **If yes to (1):** adopt Tranquillity's four connector classes as the shell but keep Legion's `IExperienceService`/schema/behavior — confirm this over the alternative of writing a fresh connector from scratch (Tranquillity's is proven-working and would save the plumbing, at the cost of importing its interface names to adapt). Recommendation: adopt-and-adapt the shell, credit StolenRuby/NGC.
3. **Crediting:** if any of Tranquillity's connector topology is reused, credit **StolenRuby** (original scaffold) and **Mike Dickson / OpenSim-NGC (Utopia Skye)** (integration). No behavioral code should be credited/propagated.
4. **Do NOT** treat Tranquillity's "~80% conformant" framing as license to backport its behavior — every behavioral difference is a regression vs Legion (Part B). This should be recorded so the port audit's stale "stub" note and the scope doc's "80%" note don't get re-litigated.
5. **Live-SL trace access (D-SLTEST):** the reference model's few remaining unverifiable edges (empty-value KV read, CAS-vs-empty-original, ladder tie-breaks, RegionExperiences error shape) are the same DEFERRED-BY-DECISION tail in Legion's ledger — unchanged by this research; still need a Premium trace to close as VERIFIED rather than defensible-default.
6. **ExperienceQuery (CAP-EQ):** neither fork implements it meaningfully (both no-op/absent); it couples to per-agent EEP injection, which Legion stubs. Confirm it stays a documented no-op in the reference build until EEP injection exists.

---

### Sources
- SL wiki: [llCreateKeyValue](https://wiki.secondlife.com/wiki/LlCreateKeyValue), [llReadKeyValue](https://wiki.secondlife.com/wiki/LlReadKeyValue), [llUpdateKeyValue](https://wiki.secondlife.com/wiki/LlUpdateKeyValue), [llDataSizeKeyValue](https://wiki.secondlife.com/wiki/LlDataSizeKeyValue), [llGetExperienceErrorMessage](https://wiki.secondlife.com/wiki/LlGetExperienceErrorMessage), [llRequestExperiencePermissions](https://wiki.secondlife.com/wiki/LlRequestExperiencePermissions), [Category:Experience Tools](https://wiki.secondlife.com/wiki/Category:Experience_Tools)
- SL KB: [Experiences in Second Life](https://community.secondlife.com/knowledgebase/english/experiences-in-second-life-r1365/)
- OpenSim: [LSL Status/Functions](http://opensimulator.org/wiki/LSL_Status/Functions), [opensim-dev — Implementing Experience System (2017)](http://opensimulator.org/pipermail/opensim-dev/2017-July/026414.html)
- NGC/Tranquillity: [OpenSim-NGC/OpenSim-Tranquillity](https://github.com/OpenSim-NGC/OpenSim-Tranquillity), [Wiki/History](https://github.com/OpenSim-NGC/OpenSim-Tranquillity/wiki/History)
- Halcyon: [IslandzVW/halcyon](https://github.com/IslandzVW/halcyon)
- Local (read-only): `experience-parity-ledger.md`, `experience-caps-audit-v1.md`, `experience-conformance-audit-v1.md`, `experience-finish-plan-v1.md`, `tranquillity-port-audit-v1.md`; `/d/tranquillity-develop/Source/**` Experience stack + `experience-port-audit-v2.md`/`-plan-v1.md`/`-scope.md`; git provenance `26d3971448`.
