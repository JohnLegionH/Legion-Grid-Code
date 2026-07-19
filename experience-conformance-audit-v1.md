# Legion Experience System — SL Conformance Audit v1 (Pass 1 of 2)

Date: 2026-07-19 · Read-only; no code changed. Legion-vs-SL only.
Legion: `slua-tier2-tables` @ `f7ff0e8cd1` — **exact match to tag `port-source-2026-07-18`** (verified via `git describe --exact-match`).

## Reference verification

| Reference | Result |
|---|---|
| Legion source | **PASS** — expected branch/HEAD/tag, verified |
| Halcyon `D:\halcyon-reference-fresh` | **FAILED verification — EXCLUDED from this audit.** Path exists, InWorldz header confirmed, but `LSLSystemAPI.cs` is 16,442 lines vs the expected 18,763 (the older `D:\halcyon-reference` copy is 11,185). Neither matches. Since Halcyon is heritage-context-only and this audit is Legion-vs-SL, the audit proceeds WITHOUT the Halcyon reference rather than aborting the deliverable; resolve the expected-line-count discrepancy before Pass 2 if heritage comparison is wanted. Nothing was read from it beyond the header/line count; nothing written. |
| SL reference | Documented SL semantics from the LSL wiki corpus (knowledge-based). Items I could not pin precisely are marked **SL-UNVERIFIED**, never asserted. |

## Summary

| Verdict | Count |
|---|---|
| PASS | 9 |
| DIVERGENCE (High) | 3 |
| DIVERGENCE (Low/Med) | 6 |
| GAP | 0 |
| SL-UNVERIFIED (nuances flagged inside otherwise-judged items) | 5 |
| Legion extension (not divergence) | 1 (`llClearKeyValue`, per carve-out) |

**Bottom line (details below): the script-callable surface is structurally SL-conformant — every SL Experience function exists and wires end-to-end to a working backend with SL-shaped async dataserver behavior. But three findings change script- or agent-observable behavior enough to matter: the auto-grant consent model, the `llGetExperienceDetails` list layout, and the 255-char key cap.**

## 1. Script-callable surface — existence and wiring

Confirmed set (all wired end-to-end: FunctionSig → shim → impl → service → dataserver post where applicable):

- KV 610–617: `llCreateKeyValue`, `llReadKeyValue`, `llUpdateKeyValue`, `llDeleteKeyValue`, `llKeyCountKeyValue`, `llKeysKeyValue`, `llDataSizeKeyValue`, `llClearKeyValue` (617 = Legion extension) — `LSLSystemAPI.cs:11352-11583`.
- 659–661: `llRequestExperiencePermissions`, `llAgentInExperience`, `llGetExperienceDetails` — `:12613-12742`.
- **636: `llGetExperienceErrorMessage`** — present (`ISystemAPI.cs:692`, `SyscallShim.cs:723,6632`); initially suspected GAP, is not one.
- Events `experience_permissions` / `experience_permissions_denied` registered in the Phlox event table (`SupportedEventList.cs:321,328`) with SL arg shapes (`(key agent)` / `(key agent, integer reason)` as posted at `:12625-12696`).
- Experience-gated agent-affecting functions: `HasExperiencePermission` gates the animation-override trio (`:2138,2164,2205`) and agent-environment/agent-affecting sites (`:11907,11930,12032`) — the SL pattern of experience permission substituting for classic runtime permissions. (Which exact SL functions must be experience-enabled is broad; coverage completeness is a Pass-2 item.)

## 2. Per-item verdicts

### KV dataserver round-trip (async pattern) — **PASS**
SL: each KV call returns a request key immediately; the result arrives via `dataserver(key queryid, string value)` with CSV `"1,<data>"` on success, `"0,<XP_ERROR code>"` on failure. Legion: every KV function generates `UUID.Random()`, returns it synchronously, runs the service call on `Task.Run`, and posts `PostScriptEvent(m_itemID, "dataserver", { reqID, payload })` with exactly that CSV form (`:11354-11379` and siblings). Both halves verified — request key AND actual dataserver post per function.

### `llCreateKeyValue(string k, string v)` → key — **DIVERGENCE (Medium)** on key cap; otherwise PASS-shaped
- SL: success `"1,<value>"`; key up to **1011** chars, value up to 4095; creating an existing key fails (`XP_ERROR_STORAGE_EXCEPTION` per wiki; nuance SL-UNVERIFIED).
- Legion `:11363`: rejects `key.Length > 255` with `0,13` — **but SL (and Legion's own service layer, `ExperienceService.cs:599`, `MAX_KEY_LENGTH=1011`) allow 1011**. Keys of 256–1011 chars work in SL and fail in Legion. The LSL-layer 255 gate contradicts both. Same gate in `llUpdateKeyValue` (`:11424`). Value cap 4095 enforced at the service (`:601`) — PASS.
- Duplicate-create and generic error are indistinguishable at the service, both map to 13 (self-documented `:11366-11367`) — SL-valid code, imprecise; Low.

### `llReadKeyValue(string k)` — **DIVERGENCE (Low/Med)** on empty values
- SL: success `"1,<value>"`; missing key `"0,14"` (`XP_ERROR_KEY_NOT_FOUND`).
- Legion `:11393-11394`: uses `!string.IsNullOrEmpty(val)` — **a stored empty-string value is reported as `0,14` (not found)**. SL distinguishes an existing empty value from a missing key (SL-UNVERIFIED nuance on SL's exact empty-value handling, but conflating them is observably lossy either way). Missing-key code 14 itself: PASS.

### `llUpdateKeyValue(string k, string v, integer checked, string original)` — **PASS with two Low divergences**
- SL: CAS when `checked`=TRUE (fail → `"0,15"` RETRY_UPDATE); unconditional otherwise; success `"1,<value>"`. Legion matches shape and codes (`:11417-11432`).
- Low #1 (self-documented `:11419-11422`): CAS against an empty `original` degrades to unconditional (service treats empty check as "no check").
- Low #2: CAS-fail, key-missing, and error all emit 15; SL distinguishes missing key (14) from CAS mismatch (15) (SL-UNVERIFIED nuance).

### `llDeleteKeyValue(string k)` — **PASS with Low divergence**
- SL: success echoes deleted value `"1,<value>"`; missing key → `"0,14"`. Legion echoes the value via read-before-delete (`:11455-11458`) — PASS on the echo; but a missing key emits **13** (STORAGE_EXCEPTION) instead of 14 (`:11457-11459`) — wrong error code, Low.

### `llKeyCountKeyValue()` — **PASS** (`"1,<count>"`, `:11482-11484`).

### `llKeysKeyValue(integer first, integer count)` — **PASS** with SL-UNVERIFIED clamps
`"1,k1,k2,…"`; empty/exhausted range → `"0,14"` (`:11510-11514`) matching SL's documented treat-empty-as-failure. The `count` default 100 / clamp 1000 (`:11504-11505`) is Legion policy; SL's server-side clamp value SL-UNVERIFIED. Note: keys containing commas would be ambiguous in the CSV — same ambiguity exists in SL's format (not a divergence).

### `llDataSizeKeyValue()` — **DIVERGENCE (Low)** on enforcement; format nuance SL-UNVERIFIED
`"1,<used>,<quota>"` with quota = 128 MiB constant (`:11537-11542`). Whether SL's second value is total-quota or free-remaining: SL-UNVERIFIED. Self-documented limitation: **the quota is informational only — Legion does not enforce it** (`:11538-11539`). Script-observable only at the 128 MiB boundary (writes succeed in Legion where SL would fail `XP_ERROR_QUOTA_EXCEEDED`); Low today, worth an enforcement follow-up.

### `llClearKeyValue()` — **Legion extension** (per carve-out). Enumerate-then-delete capped at 10,000 keys per call (`:11568`); an experience with more keys clears partially per call — worth a doc note, not a conformance item.

### `llRequestExperiencePermissions(key agent, string name)` — **DIVERGENCE (High): auto-grant consent model**
- SL: for an agent who hasn't previously accepted the experience, the viewer shows a **consent dialog**; the script then receives `experience_permissions` or `experience_permissions_denied` per the agent's choice. Prior participants skip the dialog.
- Legion `:12686-12696`: after the block/admission/presence checks, a fresh agent is **auto-granted** — permission persisted, `experience_permissions` fires immediately. The in-code rationale (`:12686`) is that viewer-native experience dialogs need viewer support the grid can't rely on. Deliberate, but it inverts SL's consent model: an agent is enrolled in an experience without agreeing. Script-observable difference is small (the event just always succeeds for unblocked agents); **agent-observable difference is fundamental**. High.
- What DOES match SL: already-granted → immediate `experience_permissions` (`:12667-12674`); agent-blocked → denied 4 (`:12677-12684`); no-experience script → denied 5 (`:12623-12630`); block/admission failures → denied 4 (`:12635-12653`); denial event carries `(agent, reason)` ints.
- Low nuance: parcel/land-scope denials emit 4 (`XP_ERROR_NOT_PERMITTED`); SL added 17 (`XP_ERROR_NOT_PERMITTED_LAND`) for land-scope denials — Legion defines 17 (`ExperienceInfo.cs`) but never emits it (its own constants file mislabels 17/18 "Legion-only"; they are SL codes). Root-presence requirement for the target agent (`:12656-12664`): SL-UNVERIFIED but reasonable.

### `llAgentInExperience(key agent)` — **DIVERGENCE (Medium, part SL-UNVERIFIED)**
- SL: TRUE if the agent is **participating** in this script's experience — documented wording implies granted-and-present (the function is used to check "can I act on this agent here").
- Legion `:12704-12715`: checks `IsAgentGranted` only — **no presence check, no admission/block check**. An agent who granted once but is in another region (or the experience is blocked on this parcel) still returns TRUE. The presence requirement is SL-UNVERIFIED at the wiki-wording level, but the block-list bypass is internally inconsistent with Legion's own `HasExperiencePermission` (which the gated functions use, `:12531-12553`) — two different answers to "is this agent usable" depending on the call. Medium.

### `llGetExperienceDetails(key exp)` — **DIVERGENCE (High)**
- SL (documented): returns `[ name, owner key, experience id, state (integer), state message, group key ]` — six entries including the experience **state** (e.g. valid/suspended) and state message.
- Legion `:12732-12741`: returns `[ name, owner, description, group, maturity, "" ]` — its in-code comment asserts an "SL format" that does not match the SL-documented layout. Any SL script indexing this list (state at index 3, group at 5) reads wrong data on Legion: index 2 gives description instead of the experience id, index 3 gives the group key instead of state, index 4 maturity instead of state-message. **High: silent wrong-data for ported SL scripts.** (SL layout cited from documented knowledge — if Pass 2 verifies differently, downgrade.)

### `llGetExperienceErrorMessage(integer error)` — **PASS (existence + wiring)**; message-text parity vs SL's documented strings is a Pass-2 spot-check.

### Permission/admission model & precedence — **PASS (structure), with the SL caveat**
Legion's precedence (`HasExperiencePermission` `:12540-12552` and the same ladder in `llRequestExperiencePermissions`): parcel-block OR region-block → deny (block absolute); then admission = region-allowed OR grid-wide OR parcel-allow; then per-agent grant. This block-wins > allow ladder matches SL's documented land model (experiences run where enabled: land-enabled or grid-wide/land-scope, with parcel/estate blocking overriding). Exact SL tie-break wording: SL-UNVERIFIED at fine granularity; nothing observed contradicts it. Note (self-documented `:12600-12601`): block/allow checks use the **object's** parcel, not the target agent's — flagged in-code as a fast-follow; Medium nuance for cross-parcel agent-affecting calls.

### Service/data layer — **PASS**
6 tables map cleanly to the SL data model: `experiences` (profile incl. maturity/properties), `experience_permissions` (per-agent grant/block — SL's participant list), `experience_keyvalue` (KV store), `experience_allowed`/`experience_blocked` (region lists behind the Region/Estate panel), `script_experiences` (script↔experience association — SL's compile-with-experience equivalent). Limits: key 1011 / value 4095 enforced at the service (`ExperienceService.cs:599-633`) = SL's documented caps; quota 128 MiB defined but unenforced (see llDataSizeKeyValue). Per carve-out, region-local direct-MySQL topology is not judged.

## 3. Script-callable vs viewer-cap surface

- **Script-callable (the priority):** complete function set, correct async model, working backend — verdicts above.
- **Viewer-cap/management surface (secondary):** Legion implements 3 caps (`RegionExperiences`, `GetExperienceInfo`, `FindExperienceByName`) + the `estateexperiencedelta` wire — enough for the Region/Estate Experiences panel and picker. SL viewers additionally use profile/preferences caps (`UpdateExperience`, `ExperiencePreferences`, admin/contributor queries, agent/creator lists, `GetMetadata`, group experiences) that Legion does not serve — the viewer's full Experience-profile UI is non-functional against Legion. This is a **management-surface GAP** (not script-callable), consistent with Legion's auto-grant design choice. Recorded for completeness; not counted against script conformance.

## 4. Bottom line

**Yes, Legion's script-callable Experience surface is SL-conformant in structure and wiring** — all functions exist, all reach a working service, the dataserver async contract and error-code vocabulary are SL's. **Three real conformance issues stand out**, in priority order:

1. **`llGetExperienceDetails` list layout** (High) — silently wrong data for any SL-written script indexing the list.
2. **Auto-grant in `llRequestExperiencePermissions`** (High, deliberate) — inverts SL's consent model; scripts barely notice, agents do. If viewer support ever allows, a dialog path would close it; until then it's a documented policy divergence.
3. **255-char key cap in the LSL layer** (Medium) — contradicts both SL and Legion's own service limit (1011); two-line fix candidate.

Plus the Low tail: delete-miss error 13-vs-14, empty-value reads as not-found, CAS empty-original degradation, quota unenforced, `llAgentInExperience` ignoring presence/blocks, land denials never using code 17. No GAPs: every SL Experience script function is present, including `llGetExperienceErrorMessage` (636).

Pass 2 candidates: verify the SL-UNVERIFIED items against live SL or captured traces (llGetExperienceDetails layout foremost), spot-check `llGetExperienceErrorMessage` strings, audit experience-gated coverage of the full SL agent-affecting function list, and re-resolve the Halcyon reference expectation.
