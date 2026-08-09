# Experience Finish-Line Plan — v1

**Date:** 2026-07-20 · **Read-only audit + plan; no code changed.**
**Baseline:** `slua-tier2-tables`. **Branch HEAD:** `b900a84218`. **Deployed HEAD:** `5bdfadcd4f` (see Part A).
**Inputs:** `experience-parity-ledger.md`, `experience-caps-audit-v1.md`, code verification (this pass), SL wiki + Firestorm 7.2.2 research (this pass).
**Goal:** every ledger row VERIFIED-CONFORMANT or DEFERRED-BY-DECISION. The single deliberate deviation is **acquire policy** (SL requires Premium → Legion makes it grid-configurable, default estate-managers + region-owners).

---

## PART A — Ledger reconciled against reality (corrections)

**Code-claim verification:** every row currently marked VERIFIED-CONFORMANT was spot-checked against the source at `b900a84218`. **All passed** — the consent flow, the 8 cap handlers, the 8-table schema, `IsExperienceContributor` (owner-only), the association write, the `ExperienceToOSD` serialization (13/21/42 + expiration + extended_metadata), and trusted enforcement all exist and do what the ledger claims. No stubs, no partials, no superseded rows. So the ledger's *code* claims are true.

But three reality-gaps must be corrected:

### CORRECTION A1 (critical) — the milestone is NOT live; deployed HEAD lags branch by 2 commits
Live-bin hashes vs source prove **the deployed grid is Slice 1+2 (`5bdfadcd4f`)**. The live `OpenSim.Region.ClientStack.LindenCaps.dll` and `OpenSim.Region.Framework.dll` are still the **2026-07-15 baseline**. Therefore:
- **Slice 2.1 (`b506141780`, the association WRITE) and Slice 2.2 (`b900a84218`, the named contributor check) are committed but NOT deployed.**
- **This is why `code 5` persists in-world.** The live `UpdateScriptTask` handler is the baseline that *ignores* the `experience` field entirely — nothing writes `script_experiences`, so `llRequestExperiencePermissions` correctly reports code 5. It is **not** a code bug; it is an undeployed commit.
- Many ledger rows carry "deploy pending (John)" — accurate — but the ledger's tally reads as if the end-to-end chain works. It does in *code*; it does **not** on the *live grid* until 2.1/2.2 deploy. **The finish plan's slice 1 is that deploy.**

Deploy set to bring live from `5bdfadcd4f` → `b900a84218` (matched — interface `IExperienceService` + new interface `IExperienceModule` both touched):
`OpenSim.Region.Framework.dll` + `OpenSim.Region.CoreModules.dll` + `OpenSim.Region.ClientStack.LindenCaps.dll` + `OpenSim.Services.Interfaces.dll` + `OpenSim.Services.ExperienceService.dll` (+ `Phlox.ScriptEngine.dll`, rebuilt against the new `IExperienceService` — behaviorally unchanged but redeploy for a clean bind).

### CORRECTION A2 — Section 3 header is stale; DEC-1 and DEC-4 are resolved, not "pending"
Section 3 is titled "Policy decisions pending (all currently OPEN)" but:
- **DEC-1 (consent model)** is VERIFIED-CONFORMANT (implemented in `9315d95324`) — it was *decided by implementing*, not pending. (Its one remaining sub-issue is the timeout value — see A3.)
- **DEC-4 (trusted experiences)** is effectively **resolved**: the "ratify-or-schedule" decision was answered by *building it* (CAP-RE-TRUST + CAP-RE-TRUST-ENF, both VERIFIED). DEC-4 should be reclassified DEFERRED-BY-DECISION→DONE (trusted is implemented), not OPEN.
Fix: retitle Section 3, mark DEC-1 and DEC-4 as decided/done.

### CORRECTION A3 — two SL-UNVERIFIED rows are now VERIFIED by this pass (and one becomes a confirmed fix)
- **UNV-EXP-TIMEOUT** → **now a confirmed OPEN FIX, not "unverified."** SL wiki (llRequestExperiencePermissions) documents the request "will time out after **at least 5 minutes**." Legion's `EXPERIENCE_PERM_TIMEOUT_MS = 120000` (120s) is **below SL's 300s floor → non-compliant.** Change to ≥ 300000.
- **UNV-5** (llDataSizeKeyValue second value) → **resolved:** SL returns `used,TOTAL_quota` (second value is total quota, not remaining). Action: verify Legion emits total (not remaining); if it does, close UNV-5 as conformant; if not, one-line fix.

---

## PART B — Full remaining-work enumeration (every non-VERIFIED row)

SL behavior below is cited from SL wiki / Firestorm 7.2.2 (this pass). Effort: **S** ≤ a few hrs, **M** ~half-day, **L** ~day+.

| Ledger row | What SL does (cited) | What Legion needs | Readiness | Effort |
|---|---|---|---|---|
| **CAP-ADM** IsExperienceAdmin | Admin = experience **owner** OR group member with **GP_EXPERIENCE_ADMIN** (bit 49, roles_constants.h:148). Gates the profile Edit button; response `{status:bool}` | Serve the cap (GET `?experience_id=`): owner check (service) OR group-power (module via `GetGroupPowers(group_id)` & `GroupPowers.ExperienceAdmin`) | BACKEND-READY owner; group-power in module | M |
| **CAP-UPD** UpdateExperience | Profile Save. Editable: name, description, slurl, maturity (13/21/42), marketplace, logo, enable(`PROPERTY_DISABLED`), private(`PROPERTY_PRIVATE`) (llfloaterexperienceprofile.cpp updatePackage 786-836). **Group field is OWNER-ONLY** (admins edit everything except group). Response `experience_keys[0]` + optional `removed` validation map | Serve POST cap; admin-gate (CAP-ADM); decode `extended_metadata`; maturity-validate; enforce owner-only on `group_id`; service `UpdateExperience` already persists all fields. **Sole writer of `group_id`** | BACKEND-READY (service.UpdateExperience covers fields) | M |
| **CAP-GRP** GroupExperiences | Group profile Experiences tab. GET `?<group_id>` → `{experience_ids:[…]}` (llexperiencecache getGroupExperiences) | New service query `GetExperiencesByGroup(groupId)` (mirror `GetExperiencesByOwner`; `experiences.group_id` exists) + cap handler | NEEDS-SERVICE-WORK (small) | M |
| **CAP-GAE** GetAdminExperiences | Admin tab. GET → `{experience_ids:[…]}` = owner ∪ groups where agent holds GP_EXPERIENCE_ADMIN | Owner core ready; add group union (needs CAP-GRP query + group-power) | BACKEND-READY owner; group union w/ Slice 4 | S (with Slice 4 plumbing) |
| **CAP-GCE group-union** (sub-item; owner-core VERIFIED) | Contributor tab / script combo = owner ∪ groups where agent holds **GP_EXPERIENCE_CREATOR** (bit 50) | Broaden the owner-only list with the group union | needs Slice-4 group plumbing | S |
| **IsExperienceContributor group-power** (backing CAP-ASSOC + CAP-ICO; owner-only VERIFIED) | Contributor = owner OR group **GP_EXPERIENCE_CREATOR** (bit 50 — "can sign scripts for experiences owned by this group") | Broaden the owner-only check with group-power (module layer — service can't see group powers) | needs Slice-4 group plumbing | S |
| **CAP-ICO cap** IsExperienceContributor (the cap) | Server exposes it; **current Firestorm never calls it** (contributor inferred from GetCreatorExperiences) — parity-only | Trivial: GET `?experience_id=` → `{status:bool}` from the (now-existing) contributor check | BACKEND-READY (method exists) | S |
| **CAP-EQ** ExperienceQuery | EEP environment-push policing: on parcel change, GET `?parcelid=<id>&experiences=<csv>` → `{experiences:{uuid:bool}}`; viewer clears injections mapped false (llenvironment.cpp testExperiencesOnParcelCoro 3564-3635) | Compute per-experience allowed-on-parcel booleans (data exists via `HasExperiencePermission`). **BUT couples to whether Legion supports experience-driven EEP injection at all** | NEEDS-DESIGN (see John-decision D-EEP) | M (or document-exception) |
| **CAP-AGE-POST / DEC-3** Acquire | SL: **Premium** acquires 1 experience key, **Premium Plus** 2; no land requirement (SL KB / LL blog). Viewer "Acquire an Experience" POSTs empty body to AgentExperiences | **Deliberate deviation:** grid-configurable policy, **default estate-managers + region-owners** may acquire; POST → `CreateExperience` owned by the actor. Response echoes new list incl. `purchase` gate | BACKEND-READY (`CreateExperience` exists); needs config knob | M |
| **DEC-2** Quota enforcement | Per-experience KV store **128 MiB**; exceed → **XP_ERROR_QUOTA_EXCEEDED = 11** (SL wiki llCreateKeyValue/llUpdateKeyValue). `llDataSizeKeyValue` → `used,TOTAL` | Size-check before Create/Update KV; emit code 11 on exceed; confirm `DataSizeKeyValue` returns total quota (UNV-5) | BACKEND-READY (`DataSizeKeyValue` exists) | M |
| **UNV-EXP-TIMEOUT** Consent timeout | SL: "times out after **at least 5 minutes**" → 300s floor; on timeout fire code 18 | Change `EXPERIENCE_PERM_TIMEOUT_MS` 120000 → **≥ 300000** | trivial | S |
| **OTH-1** parcel-scope | Agent-affecting experience calls should test the **agent's** parcel, not the object's | Refine block/allow checks in the gated LSL functions | in-code fast-follow | M |
| **CAP-RE-ERR** | RegionExperiences error-response shape unconfirmed in SL | Live-SL trace, or document as best-effort (viewer tolerates current echo) | SL-UNVERIFIED | S (test) |
| **UNV-1** empty-string KV read | **Wiki silent** whether existing empty value reads as success-empty or KEY_NOT_FOUND | Live-SL test; likely fix to success-empty (key exists) | SL-UNVERIFIED | S (test) |
| **UNV-2** CAS vs empty original | **Wiki silent** on empty `original` in checked update | Live-SL test | SL-UNVERIFIED | S (test) |
| **UNV-3** CAS-fail vs missing (14 vs 15) | Mismatch → code 15 (RETRY_UPDATE); missing-key distinction undocumented | Live-SL test | SL-UNVERIFIED | S (test) |
| **UNV-4** llKeysKeyValue clamps | SL's default/clamp values undocumented | Live-SL test | SL-UNVERIFIED | S (test) |
| **UNV-6** root-presence req | SL's presence requirement for the target agent undocumented | Live-SL test | SL-UNVERIFIED | S (test) |
| **UNV-7** ladder tie-breaks | Fine-grain block/admission/grant tie-break order undocumented | Live-SL test | SL-UNVERIFIED | S (test) |
| **UNV-8** GetMetadata extra fields | Viewer only ever requests `experience` — effectively moot | Document as closed-by-viewer-behavior (no SL field vocabulary needed) | close as N/A | S |
| **UNV-9** IsExperienceContributor cap wire | Server-side cap shape academic (viewer never calls it) | Tie to CAP-ICO cap serve; use `{status:bool}` (Tranquillity-shaped) | close with CAP-ICO | S |

**Cross-checks confirmed:** no OPEN/SL-UNVERIFIED row was missed — every row in ledger Sections 2b, 3, 4, 5 that isn't VERIFIED is enumerated above. DEC-4 moves to done (Part A2). The CAP-ICO "dead cap" note was already corrected in Slice 2.2 (backing method exists, used internally).

---

## PART C — The plan: ordered, buildable/deployable slices

**Critical-path correction:** the task assumed `IsExperienceContributor` was the milestone blocker. It is **not** — it's implemented (owner-only) and committed. **The milestone blocker is DEPLOYMENT** of the already-committed association write. So slice 1 is a deploy, not new code.

### Slice 1 — DEPLOY the pending association write (no new code) — **CRITICAL PATH**
- **Closes (in reality):** CAP-ASSOC + CAP-ICO-backing become *live* → script association works → **consent testable end-to-end in-world** (create/pick experience → compile → touch → dialog → Yes/No/Block).
- **Action:** deploy the 6-DLL set from Correction A1 (matched — interface-touched). Restart. Verify `experience_agent_blocked` + `IsExperienceContributor` load clean; run the in-world milestone test.
- **Deps:** none. **Decision:** none. **Effort:** deploy only.

### Slice 2 — Profile edit (Slice-3 work): CAP-ADM + CAP-UPD
- **Closes:** IsExperienceAdmin (Edit-button gate) + UpdateExperience (Save). Enables editing name/desc/maturity/logo/marketplace/enable/private; UpdateExperience becomes the **first writer of `group_id`** (prereq for Slice 3 visibility).
- **SL-verify at code time:** editable field set + `PROPERTY_DISABLED/PRIVATE` bits (llfloaterexperienceprofile.cpp:786-836); **owner-only on `group_id`**; maturity 13/21/42; admin = owner OR GP_EXPERIENCE_ADMIN (bit 49). **Name content-filter: UNVERIFIABLE — do NOT implement a guessed filter; document as exception** (see John-decision D-NAME).
- **Deploy risk:** CoreModules (+ Interfaces/ExperienceService **if** a service `IsExperienceAdmin`/`UpdateExperience`-validation method is added → matched set). **Decision:** D-NAME (minor). **Effort:** M+M.

### Slice 3 — Group integration (Slice-4 work): CAP-GRP + CAP-GAE + group-power broadening
- **Closes:** GroupExperiences cap; GetAdminExperiences; and broadens CAP-GCE / IsExperienceContributor / IsExperienceAdmin from owner-only to **owner ∪ group-power** (GP_EXPERIENCE_CREATOR bit 50 / GP_EXPERIENCE_ADMIN bit 49). Makes group-owned experiences fully usable (list, associate, admin).
- **New:** `IExperienceService.GetExperiencesByGroup(groupId)`; module-layer group-power checks via `ScenePresence.ControllingClient.GetGroupPowers(group_id)`.
- **Depends on Slice 2** (UpdateExperience must exist to *set* `group_id`, else group lists are always empty).
- **SL-verify:** GP bit values (roles_constants.h:148-149); GroupExperiences wire `?<group_id>`→`{experience_ids}`.
- **Deploy risk:** matched set (interface gains `GetExperiencesByGroup`). **Decision:** none. **Effort:** L.

### Slice 4 — Compliance fixes (small, high-value, mostly independent)
- **Consent timeout** 120s → **300s** (Phlox) — confirmed non-compliant. **[S]**
- **Quota enforcement (DEC-2):** size-check before Create/Update KV, emit **code 11** on >128 MiB; verify `DataSizeKeyValue` returns `used,TOTAL` (UNV-5). **[M]**
- **OTH-1:** agent's-parcel vs object's-parcel for agent-affecting calls. **[M]**
- **SL-verify:** timeout floor (llRequestExperiencePermissions wiki); quota 128 MiB + code 11 (KV wiki).
- **Deploy risk:** Phlox + ExperienceService (+ Interfaces if a quota-check method added). **Decision:** none. **Effort:** S+M+M.

### Slice 5 — Acquire (the deliberate deviation) + CAP-ICO cap
- **Acquire (DEC-3):** AgentExperiences **POST** → `CreateExperience` owned by actor, **gated by a grid-config policy** (`[Experience] AcquireRoles = EstateManager,RegionOwner` default; SL's Premium requirement documented as the intentional deviation). This becomes the **one DEFERRED-BY-DECISION row.** **[M]**
- **CAP-ICO cap:** trivial GET `?experience_id=`→`{status:bool}` (closes UNV-9). **[S]**
- **Deploy risk:** CoreModules (+ config). **Decision:** acquire policy already decided (grid-config, default estate-mgrs+region-owners). **Effort:** M+S.

### Slice 6 — ExperienceQuery (needs design first)
- **CAP-EQ:** EEP per-parcel injection policing. **Blocked on John-decision D-EEP** — does Legion support experience-driven environment (EEP) injection at all? If **no**, serve a permissive/empty response and **document as a deliberate exception** (no injections to police); if **yes**, implement the boolean query. **[M or document-exception]**
- **Deps:** D-EEP. **Effort:** M.

### Slice 7 — SL-UNVERIFIED closure (live-SL test pass)
- **One focused session against a live SL experience** (John's or a helper's Premium account) to capture traces for: UNV-1 (empty read), UNV-2/3 (CAS empty/missing), UNV-4 (Keys clamps), UNV-6 (root presence), UNV-7 (ladder tie-breaks), CAP-RE-ERR (error shape). Then match or document each.
- UNV-8 closes now as N/A (viewer only requests `experience`).
- **Deps:** live-SL access (John-decision D-SLTEST). **Effort:** M (test) + S per fix.

---

## PART D — "Done" definition + exactly what remains

**COMPLETE / SL-COMPLIANT** = every ledger row is **VERIFIED-CONFORMANT** or **DEFERRED-BY-DECISION**, where the *only* DEFERRED-BY-DECISION row is **CAP-AGE-POST/DEC-3 (acquire policy)** — SL-Premium replaced by grid-configurable estate-managers+region-owners, reason documented.

**Rows between now and complete:**

| Row | Path to terminal state | Slice |
|---|---|---|
| CAP-ASSOC, CAP-ICO-backing (code done) | → live via **deploy** | 1 |
| CAP-ADM | implement (owner+group-admin) | 2 |
| CAP-UPD | implement (owner-only group field) | 2 |
| CAP-GRP | implement (GetExperiencesByGroup) | 3 |
| CAP-GAE | implement (owner∪group-admin) | 3 |
| CAP-GCE group-union | broaden to owner∪group-creator | 3 |
| IsExperienceContributor group-power | broaden to owner∪group-creator | 3 |
| CAP-ICO cap | serve `{status}` | 5 |
| CAP-EQ | implement OR document exception (D-EEP) | 6 |
| CAP-AGE-POST/DEC-3 | implement grid-config → **DEFERRED-BY-DECISION** | 5 |
| DEC-2 quota | enforce (code 11) | 4 |
| UNV-EXP-TIMEOUT | 120→300s | 4 |
| UNV-5 | verify total-quota return | 4 |
| OTH-1 | agent-parcel refinement | 4 |
| CAP-RE-ERR, UNV-1,2,3,4,6,7 | live-SL trace → match/document | 7 |
| UNV-8 | close as N/A now | — |
| UNV-9 | close with CAP-ICO cap | 5 |
| DEC-1, DEC-4 | reclassify done (Part A2) | — (ledger edit) |

**Total: 7 slices** (1 deploy-only; 5 code; 1 live-SL test). **Rough effort:** ~1 deploy + ~4–5 engineering-days of code across slices 2–6, plus one live-SL test session (slice 7). Slices 4/5/6 are largely independent of 2/3 and can parallelize after slice 1.

---

## John-decisions still needed (flagged)

1. **D-EEP (slice 6):** Does Legion support experience-driven EEP environment injection? Determines whether ExperienceQuery is implemented or documented as a no-op exception. *(Investigation may answer it without John.)*
2. **D-SLTEST (slice 7):** Access to a live SL Premium experience to capture the UNV-* KV/permission traces. Without it, those rows can only be closed as documented "best-effort, SL-unverifiable" exceptions — which is a weaker form of "done."
3. **D-NAME (slice 2, minor):** UpdateExperience name content-filter is SL-unverifiable; default is to **not** implement a guessed filter and document the exception. Confirm that's acceptable.

**Acquire policy (DEC-3) is already decided** (grid-config, default estate-managers+region-owners) — no further input needed. No other decisions outstanding.
