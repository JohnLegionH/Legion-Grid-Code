# Experience Viewer-Capability Audit — v1 (Pass 1, read-only)

**Date:** 2026-07-19
**Baseline:** `/d/legion-grid-source` @ `slua-tier2-tables`, HEAD `2baa516eea` (EXP-CONFORMANCE-3)
**Scope:** the 14 Experience-related simulator capabilities SL exposes (wiki "Current Sim Capabilities", edited 2026-06-17), versus what Legion serves. Read-only — no code changes made or proposed in-line; the fix plan is Part C.
**Companion doc:** `experience-parity-ledger.md` (master status ledger; that doc, not this one, is the authority on completeness claims).
**Prior audit:** `experience-conformance-audit-v1.md` (script surface — Phlox LSL API).

**Sources:**
- **SL viewer source** — github.com/secondlife/viewer, `main` branch, fetched 2026-07-19. All 14 caps are requested from the Seed cap in `llviewerregion.cpp` (~3272-3285); cap resolution wired in `llstartup.cpp` (~3313). Wire formats below marked VERIFIED were read from actual viewer code; nothing marked VERIFIED is guessed.
- **Legion** — `OpenSim/Region/CoreModules/Experience/ExperienceModule.cs` (caps), `OpenSim/Services/Interfaces/IExperienceService.cs` + `ExperienceInfo.cs`, `OpenSim/Services/ExperienceService/ExperienceService.cs` (29-method service, 6-table MySQL schema).
- **NGC/Tranquillity** — `/d/tranquillity-develop`, `Source/OpenSim.Region.ClientStack.LindenCaps/ExperienceModule.cs` (13 of 14 caps). **REFERENCE ONLY** for wire-format cross-checks — not a port instruction (see `tranquillity-port-audit-v1.md` for why porting is not on the table).

**Headline:** Legion serves 3 of 14 caps. The 3 served are structurally correct but this audit found **four response-shape gaps in served caps** (maturity encoding, missing `expiration`, missing `extended_metadata`, missing pagination URLs) plus the already-known trusted/default gaps in RegionExperiences. Of the 11 missing caps: 5 are BACKEND-READY (caps handler only), 4 need small service additions, 2 need design input. One of the 14 (`IsExperienceContributor`) is verified **never called** by the viewer.

---

## Part A — Per-cap audit

Legend for LEGION STATE: **SERVED** (registration cited) / **NOT SERVED**.
Legend for BACKEND READINESS (missing caps only): **BACKEND-READY** (caps handler only) / **NEEDS-SERVICE-WORK** (new service method or query) / **NEEDS-DESIGN** (decision or subsystem question first).

### A.1 GetExperienceInfo — SERVED

- **Purpose / UI:** universal experience lookup — backs every experience name/detail display (list items, profile floater, script editor, log panel). Viewer: `llexperiencecache.cpp` `requestExperiences()` / `requestExperiencesCoro()`, batched every 0.5 s. **Wire format VERIFIED.**
- **Wire (viewer):** GET `cap/id/?page_size=<N>&public_id=<uuid>&public_id=<uuid>...`. Parses `experience_keys` (array of maps; uses `public_id`, `name`, `properties`, `expiration`, `description`, `quota`, `maturity`, `extended_metadata`, `slurl`, `agent_id`, `group_id`, `private_id`) and `error_ids` (array of UUIDs → negative-cached as `PROPERTY_INVALID`).
- **Legion:** registered ExperienceModule.cs:231-234 (varPath). Handler :396-425 accepts UUIDs under *any* query key (so `public_id` works), returns `experience_keys` via `ExperienceToOSD` (:257-275) + `error_ids`. **Conformance gaps found:**
  1. **Maturity encoding.** Legion serializes `info.Maturity` raw — 0/1/2 (ExperienceModule.cs:269, ExperienceInfo.cs:21). SL convention per Tranquillity reference is SIM_ACCESS-style 13 (General) / 21 (Mature) / 42 (Adult) — Tranquillity's UpdateExperience explicitly validates 13/21/42 (Tranquillity ExperienceModule.cs:804-904). Viewer-side comparison constants not yet extracted from viewer source → encoding divergence is near-certain but the exact viewer expectation is **WIRE-FORMAT-UNVERIFIED at the value level**. Ledger CAP-GEI-MAT.
  2. **`expiration` missing.** Viewer caches each entry with `expiration`; Legion never emits it. Tranquillity hardcodes `expiration: 600`. Effect on viewer cache behavior when absent: unverified. Ledger CAP-GEI-EXP.
  3. **`extended_metadata` missing.** Viewer reads it (HTML/LLSD-encoded string carrying `logo` and `marketplace`); the profile floater's logo comes from here. Legion stores both fields (schema: `logo`, `marketplace`) but never serializes them → experience logos never display. Tranquillity reference: GetExperienceInfoGetHandler:1037-1095. Ledger CAP-GEI-META.
  - `properties` translation is correct (Legion translates internal flags to viewer bitmask VP_PRIVATE=1<<5, VP_DISABLED=1<<6 at :267-268, matching viewer PROPERTY_* constants). `quota` hardcoded 128 matches Tranquillity.
- **Tranquillity ref:** GetExperienceInfoGetHandler:1037-1095; request key `public_id` (repeatable); 60 s ExpiringCache.

### A.2 FindExperienceByName — SERVED

- **Purpose / UI:** experience search — picker panel (`llpanelexperiencepicker.cpp` `find()` ~173) used by the Experiences floater Search tab, standalone picker, and the region/estate allowed/blocked/trusted list editors. **Wire format VERIFIED.**
- **Wire (viewer):** GET `cap?page=<n>&page_size=30&query=<escaped>`. Parses `experience_keys` (full experience maps, inserted into cache; picker reads `public_id`, `name`, `maturity`, `agent_id`) and **`next_page_url` / `previous_page_url`** — presence of these enables the pagination buttons.
- **Legion:** registered :236-238. Handler :428-451: parses `query`/`page`/`page_size` (default 30, clamp 1-30 — matches viewer's SEARCH_PAGE_SIZE=30), pages over `FindExperiences` (LIKE search, **DB-capped at 50 rows**, ExperienceService.cs:416). Returns `experience_keys` only. **Gaps:** (1) no `next_page_url`/`previous_page_url` → viewer pagination buttons permanently disabled; combined with the 50-row service cap, results beyond ~2 pages are unreachable. Ledger CAP-FBN-PAGE. (2) rows inherit the three ExperienceToOSD gaps above (maturity/expiration/extended_metadata).
- **Tranquillity ref:** FindExperienceByNameGetHandler:632-690 (also lacks pagination — "todo: handle pages").

### A.3 RegionExperiences — SERVED

- **Purpose / UI:** Region/Estate floater → Experiences panel (Allowed/Trusted/Blocked lists). Viewer: `llexperiencecache.cpp` `regionExperiencesCoro()`, panel logic `llfloaterregioninfo.cpp` `refreshFromRegion` ~3196 (GET) / `sendUpdate` ~3224 (POST); cap resolved against the *selected* region. A legacy UDP EstateOwnerMessage path (`sendEstateExperienceDelta`) coexists. **Wire format VERIFIED.**
- **Wire (viewer):** GET bare URL; POST body `{allowed:[uuid...], blocked:[uuid...], trusted:[uuid...]}`. Response parsed in `processResponse` (~3058): `default` (UUID of the default/key experience — pinned in the trusted list), `allowed`, `blocked`, `trusted`.
- **Legion:** registered :221-223. GET/POST at :294-344; POST requires estate owner/manager/admin, parses `allowed`/`blocked`/`trusted`; response via `BuildRegionExperiencesLLSD` (:277-289) = `allowed`/`blocked`/`trusted` (trusted always empty). Legion also handles the UDP `EstateExperienceDelta` (:130-190). **Gaps:** (1) **trusted** deliberately not implemented — in-code rationale at :317-320 (trusted bypasses per-agent consent; security-sensitive; "not small and clear"); POSTed trusted entries are logged and dropped (:382-385). That is a deliberate in-code deferral but **has no recorded adjudication from John** → ledger CAP-RE-TRUST stays OPEN until ratified. (2) no `default` key in the response (viewer reads it; Tranquillity emits an empty `default`). Ledger CAP-RE-DEF. Allowed/blocked GET/POST round-trip is conformant. Denial/parse-error paths echo current lists rather than an error — viewer tolerates this (it re-reads the lists).
- **Tranquillity ref:** RegionExperiencesGetHandler:980-1035 — GET only (no POST!), reads estate settings; emits `allowed`/`trusted` + `default`/`disabled`. Legion's GET+POST is closer to SL than Tranquillity here.

### A.4 GetExperiences — NOT SERVED

- **Purpose / UI:** Experiences floater **Allowed** and **Blocked** tabs — the agent's own permission lists. Viewer: `llfloaterexperiences.cpp` `refreshContents()` → `retrieveExperienceListCoro()`. **Wire format VERIFIED.**
- **Wire (viewer):** GET bare URL. Parses `experiences` (allowed UUIDs) and `blocked` (blocked UUIDs); arrays of UUIDs resolved via GetExperienceInfo.
- **Backend readiness: NEEDS-SERVICE-WORK (small).** `GetAgentExperiences(agentId)` (IExperienceService.cs:33) returns the granted=1 list — that is the `experiences` array. There is **no method returning an agent's granted=0 (blocked) list**; needs one trivial query over `experience_permissions` (`agent_id=? AND granted=0`). Schema fully supports it.
- **Tranquillity ref:** GetExperiencesGetHandler:1238-1300; keys `experiences`/`blocked`; empty arrays emitted as `<undef/>`.

### A.5 AgentExperiences — NOT SERVED

- **Purpose / UI:** Experiences floater **Owned** tab (GET); the Owned tab's **"Acquire an Experience"** button (POST — creates/purchases a new experience). Viewer: `llfloaterexperiences.cpp` `retrieveExperienceList` (GET), `sendPurchaseRequest` (POST). **Wire format VERIFIED.**
- **Wire (viewer):** GET bare URL → parses `experience_ids` (array of UUIDs). POST bare URL with **empty LLSD body** → acquire; viewer re-reads `experience_ids`, diffs against pre-purchase list, opens the profile floater in edit mode for the new id; presence of a `purchase` key in the GET response enables the Acquire button; failure → `ExperienceAcquireFailed` notification.
- **Backend readiness:** **GET: BACKEND-READY** — `GetExperiencesByOwner(ownerId)` (IExperienceService.cs:22) is exactly this. **POST (acquire): NEEDS-DESIGN** — `CreateExperience` exists (IExperienceService.cs:19), but who may acquire, how many, whether it costs anything, and default property flags are policy questions (SL gates this on premium membership). Decision D3 in Part C.
- **Tranquillity ref:** AgentExperiencesGetHandler:1191-1236 — GET only; **does not implement the POST/acquire path** (so Tranquillity is not a complete reference for this cap).

### A.6 GetCreatorExperiences — NOT SERVED

- **Purpose / UI:** three viewer consumers — this is the highest-leverage missing cap:
  1. Experiences floater **Contributor** tab (`llfloaterexperiences.cpp`).
  2. **Live-script editor** Experience panel — populates the experience combo for associating a script with an experience (`llpreviewscript.cpp` `requestExperiences()` ~1468).
  3. **Compile queue** — the set of experiences the agent may associate during mass-recompile (`llcompilequeue.cpp` ~523; mismatch → "CompileNoExperiencePerm").
  Despite the name, the viewer treats it as "experiences I can contribute scripts to". **Wire format VERIFIED.**
- **Wire (viewer):** GET bare URL → parses `experience_ids` (array of UUIDs).
- **Backend readiness: BACKEND-READY for the owner-only core** (`GetExperiencesByOwner`); the full SL semantic is owner ∪ experiences of groups where the agent holds `GroupPowers.ExperienceCreator`. Legion's groups stack **already defines** `GroupPowers.ExperienceCreator` (GroupsService.cs:98-99, XmlRpcGroupsServicesConnectorModule.cs:113-114); the group-union part needs the GroupExperiences service query (A.10) → that part is NEEDS-SERVICE-WORK.
- **Tranquillity ref:** GetCreatorExperiencesGetHandler:1097-1142; union logic ExperienceModule.cs:446-472 via `GetGroupPowers` + `GroupPowers.ExperienceCreator`.

### A.7 GetAdminExperiences — NOT SERVED

- **Purpose / UI:** Experiences floater **Admin** tab. Viewer: `llfloaterexperiences.cpp` `updateInfo("GetAdminExperiences", ...)`. **Wire format VERIFIED.**
- **Wire (viewer):** GET bare URL → parses `experience_ids`.
- **Backend readiness:** same shape as A.6 — **BACKEND-READY owner-only**; group union (`GroupPowers.ExperienceAdmin`, defined in Legion) needs the GroupExperiences query → NEEDS-SERVICE-WORK.
- **Tranquillity ref:** GetAdminExperiencesGetHandler:1144-1189; union logic ExperienceModule.cs:418-444.

### A.8 ExperiencePreferences — NOT SERVED

- **Purpose / UI:** experience profile floater **Allow / Block / Forget** buttons (`llfloaterexperienceprofile.cpp` ~252/263/411); results broadcast on the `experience_permission` event pump so the Allowed/Blocked tabs live-update. **Wire format VERIFIED.**
- **Wire (viewer):**
  - GET `cap?<experience_id>` (raw UUID as the entire query string, no `key=`) — query one experience's state.
  - PUT bare URL, body `{ "<experience_id>": { "permission": "Allow" | "Block" } }`.
  - DELETE `cap?<experience_id>` — forget.
  - Response (all three): map with arrays `experiences` (allowed) and `blocked`; viewer scans them for the UUID to derive Allow/Block/Forget.
- **Backend readiness: BACKEND-READY** — `GrantPermission` / `DenyPermission` / `ForgetPermission` / `IsAgentGranted` / `IsAgentBlocked` (IExperienceService.cs:27-32) cover all three verbs and the response state.
- **⚠ Consent-model interaction (decision D1):** this cap manages the very per-agent grants that Legion's **auto-grant** policy writes without asking (experience-conformance-audit-v1 §llRequestExperiencePermissions, High divergence). Implementing it is *compatible* with auto-grant — it would give residents their only opt-out (Block wins over auto-grant, per the served permission ladder) — but the SL-conformant end state ("Forget" returns you to *undecided*, meaning SL would show a consent dialog next time; under Legion auto-grant, "Forget" means *you will be silently re-enrolled next trigger*) **cannot be finalized until the consent-model decision is made**. Ship-ability: yes; closable as conformant: no, until D1.
- **Tranquillity ref:** HandleExperiencePreferences:143-162 (multimethod GET/PUT/DELETE).

### A.9 GetMetadata — NOT SERVED

- **Purpose / UI:** which experience a script is associated with — live-script editor Experience panel (`llpreviewscript.cpp` `loadAsset` ~2040), task-inventory item properties sidepanel (`llsidepaneliteminfo.cpp` ~332), compile queue (`llcompilequeue.cpp` ~384). Viewer: `llexperiencecache.cpp` `fetchAssociatedExperience()`. **Wire format VERIFIED.**
- **Wire (viewer):** POST body `{ "object-id": <uuid>, "item-id": <uuid>, "fields": ["experience"] }` (hyphenated keys). Response: `experience` (UUID) — viewer then chains into GetExperienceInfo. Missing key / failure → callback gets `{error, message}`.
- **Backend readiness: BACKEND-READY** — resolve the part via scene (`GetSceneObjectPart`), read the item's experience association; Legion persists script↔experience in `script_experiences` (`GetScriptExperiencePersisted(itemId)`, IExperienceService.cs:59, EXP-PERSIST-1). Note Tranquillity ignores the `fields` array ("todo: iterate over fields") and SL's full field set for this cap is undocumented → any fields beyond `experience` are **WIRE-FORMAT-UNVERIFIED**; the viewer only ever asks for `experience`.
- **Tranquillity ref:** GetMetadataPostHandler:733-802; reads `inv_item.ExperienceID`, responds `{experience: uuid}` or `<undef/>`.

### A.10 GroupExperiences — NOT SERVED

- **Purpose / UI:** group profile **Experiences** tab (`llpanelgroupexperiences.cpp` `activate()` ~75). **Wire format VERIFIED.**
- **Wire (viewer):** GET `cap?<group_id>` (raw UUID as query string). Response: `experience_ids` (array of UUIDs).
- **Backend readiness: NEEDS-SERVICE-WORK (small), not NEEDS-DESIGN.** The design question posed at audit kickoff — "does Legion's schema even have a group linkage?" — is answered **yes**: `experiences.group_id` CHAR(36) NOT NULL DEFAULT zero-UUID (ExperienceService.cs:96), carried on `ExperienceInfo` (ExperienceInfo.cs:18), persisted by `UpdateExperience` (ExperienceService.cs:328), and already serialized as `group_id` in cap responses (ExperienceModule.cs:272). What's missing is only a `GetExperiencesByGroup(groupId)` query (mirror of `GetExperiencesByOwner`). Caveat: nothing in Legion *sets* group_id today (UpdateExperience cap is unserved), so the tab would be empty until A.14 ships — sequencing note, not a blocker.
- **Tranquillity ref:** GroupExperiencesGetHandler:692-731; SQL `WHERE group_id = ?group` (direct column, no join table).

### A.11 IsExperienceAdmin — NOT SERVED

- **Purpose / UI:** experience profile floater — gates the **Edit** button: shown only if `status` is true **and** the region's UpdateExperience cap is present (`llfloaterexperienceprofile.cpp` `postBuild` ~155, `experienceIsAdmin` ~905). **Wire format VERIFIED.**
- **Wire (viewer):** GET `cap?experience_id=<uuid>` (named parameter — unlike A.8/A.10's raw-UUID style). Response: `status` (boolean).
- **Backend readiness: BACKEND-READY for owner-only** (`GetExperience` → compare `OwnerId`); full semantic = owner OR group `ExperienceAdmin` power (power exists in Legion; group check is module-side via client group powers, no new schema).
- **Tranquillity ref:** IsExperienceAdminGetHandler:944-978; semantics ExperienceModule.cs:474-494.

### A.12 UpdateExperience — NOT SERVED

- **Purpose / UI:** experience profile floater edit mode → **Save** (`llfloaterexperienceprofile.cpp` `doSave` ~592). Cap presence alone also gates the Edit button (A.11). **Wire format VERIFIED.**
- **Wire (viewer):** POST; body = the cached experience map updated with `name`, `description`, `slurl` (empty string clears), `maturity` (int), `extended_metadata` (LLSD-XML-serialized string with `marketplace` + `logo_image_id`), `properties` (viewer toggles PROPERTY_DISABLED 1<<6 / PROPERTY_PRIVATE 1<<5), `group_id`; the coro strips `quota`/`expiration`/`agent_id` before POST. Response: `experience_keys[0]` = authoritative updated record (re-cached), and optional `removed` — map of rejected-field → `{error_tag, extra_info, en}` driving per-field validation notifications.
- **Backend readiness: BACKEND-READY** — `UpdateExperience(ExperienceInfo)` (IExperienceService.cs:20) persists every field the viewer can send (name, description, group_id, slurl, maturity, properties, logo, marketplace). Handler work: authz via A.11 semantics, `extended_metadata` decode, maturity validation (13/21/42 — ties to CAP-GEI-MAT), property-bit translation, optional `removed` validation map (may be empty in v1 — viewer only inspects it when present). This cap is also **the only writer of `group_id`**, so it unblocks A.10's usefulness.
- **Tranquillity ref:** UpdateExperiencePostHandler:804-904 — includes the maturity 42/21/13 mapping, `slurl == "last"` preserve quirk, and silently-return-unchanged on non-admin.

### A.13 ExperienceQuery — NOT SERVED

- **Purpose / UI:** no floater — **experience-driven environment (EEP "environment push")**. On parcel change the viewer asks the sim whether currently-injecting experiences are still permitted on this parcel; any experience mapped to `false` has its sky/water injections cleared (`llenvironment.cpp` `testExperiencesOnParcelCoro` ~3367). Missing cap → viewer treats region as not enforcing. **Wire format VERIFIED.**
- **Wire (viewer):** GET `cap?parcelid=<S32 local id>&experiences=<id1>,<id2>,...` (comma-separated). Response: `experiences` — a **map** of experience-id string → boolean.
- **Backend readiness: NEEDS-DESIGN.** The permission data exists (parcel/region allow/block ladder is implemented for scripts — `HasExperiencePermission`), so the *answer* is computable. The design question is whether Legion supports experience-driven environment injection at all (EEP push via llReplaceAgentEnvironment etc.) — if the sim never pushes experience environments, this cap has nothing to police. Scope it with Legion's EEP state, not as a standalone item.
- **Tranquillity ref:** **not implemented** (the 1 of 14 Tranquillity lacks) — no reference available; viewer source is the only wire authority here.

### A.14 IsExperienceContributor — NOT SERVED

- **Purpose / UI:** **none — verified unused.** The only reference in the entire viewer repo is the Seed-cap request list (`llviewerregion.cpp` ~3282). The viewer requests the URL but never calls it; contributor status is inferred from GetCreatorExperiences. (Verified by full-clone grep: 1 hit.) **Wire format for the viewer side: N/A (VERIFIED unused).** Server-side shape known only from Tranquillity: GET `?experience_id=<uuid>` → `{status: bool}` — for SL's actual server behavior this remains WIRE-FORMAT-UNVERIFIED, and it does not matter for any current viewer.
- **Backend readiness: BACKEND-READY** (same check as A.11 with `ExperienceCreator` power). Zero UX value; implement last, purely for cap-surface parity.
- **Tranquillity ref:** IsExperienceContributorGetHandler:906-942; semantics ExperienceModule.cs:496-516.

---

## Part A summary table

| # | Cap | Viewer UI it powers | Wire fmt | Legion | Readiness / conformance |
|---|-----|--------------------|----------|--------|------------------------|
| 1 | GetExperienceInfo | all experience name/detail display | VERIFIED | **SERVED** :231 | 3 response gaps: maturity encoding, `expiration`, `extended_metadata` |
| 2 | FindExperienceByName | search / pickers / region-list editors | VERIFIED | **SERVED** :236 | pagination URLs missing (+ inherits row gaps) |
| 3 | RegionExperiences | Region/Estate Experiences panel | VERIFIED | **SERVED** :221 | allowed/blocked conformant; `trusted` dropped, `default` missing |
| 4 | GetExperiences | floater Allowed + Blocked tabs | VERIFIED | not served | NEEDS-SERVICE-WORK (blocked-list query) |
| 5 | AgentExperiences | floater Owned tab + Acquire (POST) | VERIFIED | not served | GET BACKEND-READY; POST NEEDS-DESIGN (D3) |
| 6 | GetCreatorExperiences | Contributor tab + **script editor** + compile queue | VERIFIED | not served | BACKEND-READY (owner core); group union NEEDS-SERVICE-WORK |
| 7 | GetAdminExperiences | floater Admin tab | VERIFIED | not served | same as #6 |
| 8 | ExperiencePreferences | profile Allow/Block/Forget | VERIFIED | not served | BACKEND-READY; final semantics gated on consent decision D1 |
| 9 | GetMetadata | script↔experience association display | VERIFIED | not served | BACKEND-READY (`script_experiences`) |
| 10 | GroupExperiences | group profile Experiences tab | VERIFIED | not served | NEEDS-SERVICE-WORK (by-group query; `group_id` exists in schema) |
| 11 | IsExperienceAdmin | profile Edit-button gate | VERIFIED | not served | BACKEND-READY (owner) + group power (exists) |
| 12 | UpdateExperience | profile edit Save | VERIFIED | not served | BACKEND-READY (service `UpdateExperience` covers all fields) |
| 13 | ExperienceQuery | EEP environment-push policing | VERIFIED | not served | NEEDS-DESIGN (couples to Legion EEP support) |
| 14 | IsExperienceContributor | **none — viewer never calls it** | VERIFIED-unused | not served | BACKEND-READY; parity-only |

Architecture note (not a cap gap): Legion's `ExperienceService` is instantiated per-region directly against MySQL (ExperienceModule.cs:103) — no Robust connector. Grid-wide consistency currently relies on all regions sharing the `[Experience]` connection string. Fine for caps work; recorded in the ledger as an observation (OBS-1), not a parity row.

---

## Part C — Proposed fix plan (plan only, no code)

### Decisions needed from John first

- **D1 — Consent model** (auto-grant vs SL dialog). Open per experience-conformance-audit-v1 (High divergence, deliberate policy, no adjudication recorded). **Gates final semantics of ExperiencePreferences (A.8)** — the cap manages the permissions auto-grant writes silently; "Forget" under auto-grant means "silently re-enrolled next trigger", which inverts SL's meaning. Also couples to trusted experiences (D4) and to the never-emitted timeout code 18.
- **D2 — 128 MiB KV quota enforcement** (open per prior audit; unrelated to caps but on the same decision docket).
- **D3 — AgentExperiences POST (acquire)**: who may create experiences via the viewer button, limits/cost, default flags. GET can ship without this; the Acquire button errors gracefully (`ExperienceAcquireFailed`) until decided.
- **D4 — Trusted experiences**: ratify (→ DEFERRED-BY-DECISION) or schedule the in-code deferral at ExperienceModule.cs:317-320. Interacts with D1 (trusted's whole point is consent bypass — under auto-grant the distinction is currently moot, which is itself an argument to defer until D1 resolves).
- **D5 — Maturity encoding** (CAP-GEI-MAT): confirm viewer expectation (extract the comparison constants from `llpanelexperiencepicker.cpp`/profile floater — one fetch), then fix wire encoding 0/1/2 → 13/21/42. Small; adjudicate inside Slice 0.

### Slice 0 — repair the served caps (small, no design, immediate)

Fix the four response-shape gaps in already-served caps — these bite every future slice since ExperienceToOSD backs all list rows:
1. Maturity wire encoding (after D5 confirmation).
2. Emit `expiration` (Tranquillity uses 600).
3. Emit `extended_metadata` (`logo`, `marketplace` — both already in schema/ExperienceInfo).
4. FindExperienceByName `next_page_url`/`previous_page_url` + revisit the 50-row `FindExperiences` cap.
5. RegionExperiences: emit `default` (zero-UUID until a default-experience concept exists).

### Slice 1 — script-tooling caps (BACKEND-READY; highest UX value)

**GetCreatorExperiences** (owner-only core) + **GetMetadata**. Unblocks: live-script editor Experience panel (associate a script with an experience — today invisible/broken against Legion), item-properties association display, compile queue. This is the biggest "stops being broken" per line of code; both are pure caps handlers over existing service methods (`GetExperiencesByOwner`, `GetScriptExperiencePersisted`/scene inventory).

### Slice 2 — the Experiences floater (mostly ready)

**AgentExperiences GET** (Owned tab; POST returns a clean failure until D3) + **GetExperiences** (Allowed/Blocked tabs; add the one blocked-list service query) + **ExperiencePreferences** (Allow/Block/Forget; ship with today's semantics, ledger row stays OPEN pending D1 — note it gives residents their first opt-out under auto-grant, which is a resident-facing safety win independent of the D1 outcome).

### Slice 3 — profile editing

**IsExperienceAdmin** + **UpdateExperience** (owner-admin first; group-admin arrives with Slice 4 plumbing). Unblocks the profile Edit/Save path — and is the only writer of `group_id`, prerequisite for Slice 4 being visible. Includes maturity validation (13/21/42) and `extended_metadata` decode; `removed` validation map may start minimal (viewer only inspects it when present).

### Slice 4 — group integration

New service query `GetExperiencesByGroup` + **GroupExperiences** cap + upgrade A.6/A.7/A.11 to the full owner∪group-power semantics (`GroupPowers.ExperienceAdmin`/`ExperienceCreator` already exist in Legion's groups stack) + **GetAdminExperiences** (Admin tab). Depends on Slice 3 (nothing can set `group_id` before UpdateExperience ships).

### Slice 5 — long tail

**IsExperienceContributor** (trivial, viewer-unused, parity-only) + **ExperienceQuery** (scope together with Legion's EEP/environment-push status — NEEDS-DESIGN; if Legion has no experience-driven environment injection, document that dependency in the ledger rather than shipping a dead cap).

### Open questions (carried into the ledger)

1. D1-D5 above.
2. Viewer maturity comparison constants (one targeted viewer-source read) — closes CAP-GEI-MAT's verification gap.
3. Does the viewer misbehave when `expiration` is absent from GetExperienceInfo rows (current Legion behavior)? Determines Slice 0 item 2's severity.
4. SL server behavior for IsExperienceContributor (WIRE-FORMAT-UNVERIFIED; academic given the viewer never calls it).
5. GetMetadata `fields` beyond `experience` — SL's full field set undocumented (viewer only requests `experience`).
6. The prior audit's SL-UNVERIFIED tail (empty-string reads, CAS-vs-empty-original, CAS error distinction, llKeysKeyValue clamps, llDataSizeKeyValue second value, root-presence, ladder tie-breaks) still needs live-SL or trace verification — Pass 2.
