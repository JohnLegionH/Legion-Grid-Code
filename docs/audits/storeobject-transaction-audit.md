# StoreObject / RemoveObject Transactional Atomicity Audit

**Date:** 2026-07-01
**Tree:** `D:\legion-grid-source`, branch `slua-tier2-tables`
**Reference:** `D:\halcyon-reference-fresh` (read-only)
**Scope:** Recon only — no fixes, no builds, nothing run against live grid or DB.

---

## 1. Verdict

Mike Chase is correct for our tree. **No DB transaction wraps any multi-row scene-object
write in `MySQLSimulationData`** — `StoreObject`, `RemoveObject`, and `StorePrimInventory`
all issue one autocommitted statement per row (or per table for deletes) inside a C#
`lock (m_dbLock)`, which serializes callers but provides zero atomicity against a crash or
killed connection. The code itself admits this in a FIXME comment (see §2.1). The worst
single spot is `StorePrimInventory`, which is a naked DELETE-then-INSERT: a crash between
the two destroys previously-persisted inventory, not just the in-flight update. Compounding
the crash window, `SceneObjectGroup.ProcessBackup` clears the dirty flag (`HasGroupChanged
= false`) *before* calling `StoreObject` and swallows the exception, so even a transient SQL
error (not a crash) leaves a partial linkset in the DB with no retry until the object is
next modified. On the engine question: **a transaction fix would be honored** — every
relevant table is declared `ENGINE=InnoDB` in the migrations (§5), with no MyISAM anywhere
in `OpenSim/Data/MySQL/Resources` (production engines still need the §5.2 query run to
confirm).

---

## 2. Evidence

### 2.1 The lock-instead-of-transactions pattern is documented in-tree

`OpenSim/Data/MySQL/MySQLSimulationData.cs:53-61`:

```csharp
/// <summary>
/// This lock was being used to serialize database operations when the connection was shared, but this has
/// been unnecessary for a long time after we switched to using MySQL's underlying connection pooling instead.
/// FIXME: However, the locks remain in many places since they are effectively providing a level of
/// transactionality.  This should be replaced by more efficient database transactions which would not require
/// unrelated operations to block each other or unrelated operations on the same tables from blocking each
/// other.
/// </summary>
private object m_dbLock = new object();
```

To be explicit: **the lock provides no atomicity whatsoever.** It only prevents two threads
in the *same process* from interleaving their statements. Each `ExecuteNonQuery` is its own
autocommitted MySQL transaction; a process crash, `kill -9`, power loss, or dropped
connection between any two statements leaves whatever subset committed.

A `grep` for `BeginTransaction|MySqlTransaction|TransactionScope` across
`MySQLSimulationData.cs` returns **zero matches**.

### 2.2 StoreObject — per-prim REPLACE loop, 2 statements per prim, N×2 crash windows

`OpenSim/Data/MySQL/MySQLSimulationData.cs:121-249` (SQL column lists elided for length;
structure verbatim):

```csharp
public virtual void StoreObject(SceneObjectGroup obj, UUID regionUUID)
{
    lock (m_dbLock)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();

            using (MySqlCommand cmd = dbcon.CreateCommand())
            {
                foreach (SceneObjectPart prim in obj.Parts)
                {
                    cmd.Parameters.Clear();

                    cmd.CommandText = "replace into prims (" +
                            "UUID, CreationDate, " +
                            /* ... ~95 columns ... */
                            "lnkstBinData)";                     // line 135-210

                    FillPrimCommand(cmd, prim, obj.UUID, regionUUID);
                    ExecuteNonQuery(cmd);                        // line 214 — autocommit #1

                    cmd.Parameters.Clear();

                    cmd.CommandText = "replace into primshapes (" +
                            /* ... 30 columns ... */
                            "?State, ?LastAttachPoint, ?Media, ?MatOvrd)";  // line 218-239

                    FillShapeCommand(cmd, prim);
                    ExecuteNonQuery(cmd);                        // line 243 — autocommit #2
                }
            }
            dbcon.Close();
        }
    }
}
```

**a. Transaction?** None. Each prim produces two independently-committed statements; a
50-prim linkset is 100 separate commits.

**b. Statement style:** `REPLACE INTO` for both `prims` and `primshapes`. `REPLACE` is
internally DELETE+INSERT in MySQL, but as a *single statement* it is atomic per row — the
danger here is *between* statements, not within one. No explicit DELETE-then-INSERT in this
method (that pattern lives in `StorePrimInventory`, §2.4).

**c. Connection/locking:** Per-call pooled connection (`new MySqlConnection` each call)
under the shared `m_dbLock`. The lock serializes writers in-process only; it is **not** a
substitute for a transaction (see §2.1).

**d. Failure handling:** `ExecuteNonQuery` (lines 106-117) logs and **rethrows**. The
exception propagates out of `StoreObject` mid-loop with no cleanup — rows already REPLACEd
stay committed. The `using` blocks close the connection, nothing more. The rethrown
exception is then swallowed upstream (§3, `ProcessBackup`), and because the dirty flag was
cleared *before* the store, the partial state is not retried.

### 2.3 RemoveObject — SELECT + three DELETEs, each autocommitted

`OpenSim/Data/MySQL/MySQLSimulationData.cs:251-314`:

```csharp
public virtual void RemoveObject(UUID obj, UUID regionUUID)
{
    List<string> uuids = new List<string>();
    lock (m_dbLock)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();

            using (MySqlCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "select UUID from prims where SceneGroupID= ?UUID";
                cmd.Parameters.AddWithValue("UUID", obj.ToString());

                using (IDataReader reader = ExecuteReader(cmd))
                {
                    while (reader.Read())
                        uuids.Add(reader["UUID"].ToString());
                }

                if(uuids.Count == 0)
                {
                    dbcon.Close();
                    return;
                }

                // delete the main prims
                cmd.CommandText = "delete from prims where SceneGroupID= ?UUID";
                ExecuteNonQuery(cmd);                            // line 281 — autocommit #1

                /* ... builds "IN ('uuid1','uuid2',...)" string, lines 285-302 ... */

                cmd.CommandText = "delete from primshapes where UUID " + sqlparams;
                ExecuteNonQuery(cmd);                            // line 305 — autocommit #2

                cmd.CommandText = "delete from primitems where primID " + sqlparams;
                ExecuteNonQuery(cmd);                            // line 308 — autocommit #3

                dbcon.Close();
            }
        }
    }
}
```

**a.** No transaction — three separate DELETE commits.
**b.** Pure DELETE sequence across three tables. A crash after #1 leaves orphaned
`primshapes` + `primitems` rows (garbage, but the object is gone from the region — the
orphans are invisible on reload because `LoadObjects` drives off `prims`).
**c.** Same per-call connection under `m_dbLock`; no atomicity.
**d.** Exception mid-sequence leaves the deletes done so far. No cleanup. (Also noteworthy:
the `primshapes`/`primitems` deletes use string-concatenated UUID lists rather than
parameters — values come from the DB itself, so injection risk is nil, but it's fragile.)

### 2.4 StorePrimInventory — DELETE-then-INSERT, the worst case

`OpenSim/Data/MySQL/MySQLSimulationData.cs:1898-1948`:

```csharp
public virtual void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
{
    lock (m_dbLock)
    {
        using (MySqlConnection dbcon = new MySqlConnection(m_connectionString))
        {
            dbcon.Open();

            using (MySqlCommand cmd = dbcon.CreateCommand())
            {
                cmd.CommandText = "delete from primitems where primID = ?PrimID";
                cmd.Parameters.AddWithValue("primID", primID.ToString());

                ExecuteNonQuery(cmd);                            // line 1911 — DELETE ALL, autocommit

                if (items.Count == 0)
                {
                    dbcon.Close();
                    return;
                }

                cmd.Parameters.Clear();
                cmd.CommandText = "insert into primitems (" +
                        /* ... 19 columns ... */
                        "?groupID, ?lastOwnerID)";               // lines 1920-1934

                foreach (TaskInventoryItem item in items)
                {
                    cmd.Parameters.Clear();
                    FillItemCommand(cmd, item);
                    ExecuteNonQuery(cmd);                        // line 1942 — one autocommit PER ITEM
                }
            }
            dbcon.Close();
        }
    }
}
```

**a.** No transaction.
**b.** **DELETE-then-INSERT — the exact worst-case pattern flagged in the audit brief.** A
crash after line 1911 and before the INSERT loop completes destroys inventory that was
*already safely persisted*, not merely the in-flight change. With per-item autocommits, a
crash mid-loop leaves a random prefix of the item list.
**c./d.** Same connection/lock pattern; exception mid-loop leaves the partial state. The
caller makes it worse — `SceneObjectPartInventory.ProcessInventoryBackup`
(`OpenSim/Region/Framework/Scenes/SceneObjectPartInventory.cs:1512-1531`) wraps the call in
`try { ... } catch {}` (bare swallow, line 1529) after already setting
`HasInventoryChanged = false` (line 1523) — so a failed inventory rewrite is silently
forgotten and never retried.

### 2.5 Bulk variants

There is **no `StoreObjects` bulk variant** in our tree. The interface
(`OpenSim/Region/Framework/Interfaces/ISimulationDataStore.cs:54,62,68`) declares only the
singular `StoreObject`, `RemoveObject`, `StorePrimInventory`, and `MySQLSimulationData`
implements exactly those. (Halcyon, by contrast, has `BulkStoreObjects` — see §6.)

### 2.6 Production plugin wiring — MySQL is the live code path

- `bin/config-include/GridCommon.ini:3,16` — `[DatabaseService]` →
  `StorageProvider = "OpenSim.Data.MySQL.dll"` (MSSQL/PGSQL/SQLite alternatives are all
  commented out).
- `bin/Opensim.ini:37-38` — `[SimulationDataStore]` →
  `LocalServiceModule = "OpenSim.Services.SimulationService.dll:SimulationDataService"`.
- `OpenSim/Services/SimulationService/SimulationDataService.cs:57-77` reads
  `[DatabaseService].StorageProvider` (then lets `[SimulationDataStore]` override it — no
  override present in our configs) and loads the plugin:
  `m_database = LoadPlugin<ISimulationDataStore>(dllName, ...)`.

So `OpenSim.Data.MySQL.dll:MySQLSimulationData` is the class that runs in production — the
audited code path is live, not a dead plugin. **Caveat:** this is the *source-tree* `bin/`
config (which points at the `opensim_test_safe` DB). The live grid's config under
`D:\opensim - Use this december 2025\bin\` was deliberately not touched per audit rules; it
is assumed to use the same MySQL provider (it demonstrably does, since the grid runs on the
`opensim` MySQL DB), but was not read to confirm — noted in §8.

---

## 3. Call graph (periodic backup → SQL)

Every hop verified in our tree with file:line:

| # | Hop | Location |
|---|-----|----------|
| 1 | Heartbeat: every `m_update_backup` frames (default **200**, config `UpdateStorageEveryNFrames`, `Scene.cs:361,1080`) | `OpenSim/Region/Framework/Scenes/Scene.cs:1810-1812` — `if (PeriodicBackup && Frame % m_update_backup == 0) UpdateStorageBackup();` |
| 2 | `UpdateStorageBackup()` → threadpool | `Scene.cs:2067-2073` — `WorkManager.RunInThreadPool(o => Backup(false), ..., "BackupWorker")` |
| 3 | `Scene.Backup(bool)` fires the backup event | `Scene.cs:2095-2108` — `EventManager.TriggerOnBackup(SimulationDataService, forced)` |
| 4 | Each backed-up SOG is subscribed | `SceneObjectGroup.cs:1507` — `m_scene.EventManager.OnBackup += ProcessBackup;` |
| 5 | `SceneObjectGroup.ProcessBackup` | `SceneObjectGroup.cs:2331`; dirty-flag cleared at **2416** (`HasGroupChanged = false;`), store at **2421** (`datastore.StoreObject(backup_group, ...)`), per-part inventory at **2423-2425** (`part.Inventory.ProcessInventoryBackup(datastore)`), catch-all swallow at **2439-2442** |
| 6 | `SimulationDataService.StoreObject` (skips Temporary/TemporaryOnRez) | `OpenSim/Services/SimulationService/SimulationDataService.cs:82-89` → `m_database.StoreObject(obj, regionUUID)` |
| 7 | `MySQLSimulationData.StoreObject` per-prim REPLACE loop | `MySQLSimulationData.cs:121-249` (statements at 214, 243) |
| 7b | Inventory: `ProcessInventoryBackup` → `StorePrimInventory` | `SceneObjectPartInventory.cs:1512-1531` → `SimulationDataService.cs:96-99` → `MySQLSimulationData.cs:1898-1948` |

Other entry points into the same unprotected code:
- **Shutdown flush:** `Scene.Close()` → `Backup(true)` at `Scene.cs:1553` — a hung shutdown
  killed by the operator lands mid-store on exactly this path.
- **Synchronous force-backup** (deletes, link/unlink): `Scene.ForceSceneObjectBackup` at
  `Scene.cs:2160-2167` → `ProcessBackup(SimulationDataService, true)`.

**Ordering note (load-bearing):** `HasGroupChanged = false` (line 2416) executes *before*
`StoreObject` (line 2421), and the `catch` at 2439 logs and continues. So the partial-write
problem is **not limited to process death**: any thrown SQL exception (deadlock, timeout,
dropped connection) mid-linkset leaves a committed partial store that the engine believes
is clean. It will not be re-stored until something touches the object again. The same
pattern exists for inventory (`HasInventoryChanged = false` before the call,
`SceneObjectPartInventory.cs:1523`, with a bare `catch {}` at 1529).

---

## 4. Crash-window analysis — 50-prim linkset

Store order per linkset: for each prim *i* of 50: `REPLACE prims(i)` then
`REPLACE primshapes(i)`; afterwards, per prim: `DELETE primitems` + per-item `INSERT`s.
Reload behavior anchor: `LoadObjects` (`MySQLSimulationData.cs:316+`) drives off `prims`
rows joined to `primshapes`; the root prim is identified by `UUID == SceneGroupID`.

| # | Crash point | DB state afterwards | Region reload result |
|---|------------|--------------------|---------------------|
| (a) | Between prim rows (e.g. after prim 23's pair, before prim 24) — **first store** of a new object | prims/primshapes rows 1-23 present, 24-50 absent | **Partial linkset**: object rezzes with 23 of 50 prims (if the root prim was among the stored ones — parts are stored in `obj.Parts` order, root typically first). Missing children are simply gone. If the root row was *not* yet written, the 23 child rows are orphans and the object **vanishes** entirely (children without a root are dropped/left orphaned at load). |
| (a′) | Same, but object **already existed** in DB (re-store of a moved/edited linkset) | Prims 1-23 have NEW values, 24-50 retain OLD values (REPLACE preserves prior rows it hasn't reached) | **Frankenstein linkset**: half the prims at the new position/state, half at the old. Loads "successfully" — silent corruption, the nastiest variant to detect. |
| (b) | Between a prim's `prims` row (line 214) and its `primshapes` row (line 243) | `prims` row *i* is new; `primshapes` row *i* is old (or absent for a brand-new prim) | Existing prim: loads with new position but **stale shape/texture/media**. New prim: `LoadObjects` finds a prims row with no shape — prim loads with a default/missing shape or is dropped (plus everything in (a) for prims 24-50). |
| (c) | Mid `primitems` rewrite (after DELETE at 1911, during INSERT loop at 1942) | That prim's inventory: empty, or a random prefix of its items | **Destroyed/partial prim inventory** — scripts, notecards, objects inside the prim are lost, including items that were safely on disk before this backup pass. Object itself loads fine, contents gone. Worse: `HasInventoryChanged` was already cleared and the exception path is `catch {}`, so a non-crash SQL error here is silently permanent. |
| (d) | During `RemoveObject` — after `DELETE prims` (281), before `DELETE primshapes` (305) / `DELETE primitems` (308) | `prims` rows gone; orphaned `primshapes` and/or `primitems` rows remain | **Object correctly vanished** on reload (load drives off `prims`), leaving invisible orphan rows accumulating in primshapes/primitems. Benign functionally, but table bloat + orphans that a later transaction fix / cleanup should sweep. This is the *least* dangerous window. |

Additional systemic note: the backup pass stores objects one linkset at a time inside
`TriggerOnBackup`, so a crash during the pass affects at most the linkset in flight — but
*every* backup pass rolls these dice for every changed linkset, and the sim's default cadence
is every 200 frames plus a forced full flush on shutdown.

---

## 5. Engine verification

### 5.1 Migrations declare InnoDB

`OpenSim/Data/MySQL/Resources/RegionStore.migrations` — the consolidated `:VERSION 51`
migration (line 2) creates all three tables (our tree's de-facto schema is v67 per OPS-4 #3;
versions ≥51 never alter the engine):

```
:VERSION 51		#---------------------

BEGIN;

CREATE TABLE IF NOT EXISTS `prims` (        -- line 6
  ...
) ENGINE=InnoDB DEFAULT CHARSET=latin1;     -- line 100

CREATE TABLE IF NOT EXISTS `primshapes` (   -- line 102
  ...
) ENGINE=InnoDB DEFAULT CHARSET=latin1;     -- line 133

CREATE TABLE IF NOT EXISTS `primitems` (    -- line 135
  ...
) ENGINE=InnoDB DEFAULT CHARSET=latin1;     -- line 157
```

`grep -i MyISAM` across `OpenSim/Data/MySQL/Resources/` → **no matches**. No legacy MyISAM
path exists in this tree's migrations. (Historical caveat: OpenSim migrations *before* the
v51 consolidation, on grids created many years ago, could have left MyISAM tables that
`CREATE TABLE IF NOT EXISTS` would never convert. Our DB predates this source tree, so the
production check below is not optional.)

Note the `BEGIN;`/`COMMIT;` in the migration script wraps DDL, which MySQL autocommits
anyway — it does not indicate transactional data writes anywhere.

### 5.2 Run against the live `opensim` DB to confirm actual engines (DO NOT run from this audit — operator action)

```sql
SELECT TABLE_NAME, ENGINE FROM information_schema.TABLES
WHERE TABLE_SCHEMA='opensim' AND TABLE_NAME IN ('prims','primshapes','primitems','regionsettings','land','landaccesslist');
```

**If any row reports `MyISAM`: 🔴 a transaction fix is a no-op on that table** — MyISAM
ignores BEGIN/COMMIT/ROLLBACK silently. Convert with
`ALTER TABLE <t> ENGINE=InnoDB;` (during a maintenance window; it rebuilds the table)
*before* any transaction patch is considered effective.

---

## 6. Halcyon comparison (read-only, `D:\halcyon-reference-fresh`)

Halcyon's region persistence lives in
`OpenSim/Data/MySQL/MySQLRegionData.cs` (class `MySQLDataStore`, implementing
`IRegionDataStore`). Findings:

- **Halcyon also does NOT use DB transactions.** `grep` for
  `BeginTransaction|MySqlTransaction|TransactionScope` in that file: zero matches.
- **Their mitigation is batching, not atomicity.** `BulkStoreObjects` (lines ~127-295)
  accumulates up to **128 prims into a single multi-row INSERT statement**, then executes
  one statement for `prims` and one for `primshapes`:

  ```csharp
  lock (m_PrimDBLock)                       // line ~238 — C# lock, not a transaction
  {
      ...
      ExecuteNonQuery(primCommand);         // one multi-row INSERT for up to 128 prims
      ExecuteNonQuery(shapeCommand);        // one multi-row INSERT for their shapes
  }
  ```

  Because a single MySQL statement is atomic, this shrinks the per-prim crash windows
  dramatically (a ≤128-prim linkset's prims land in one commit) — but the prims statement
  and the shapes statement are still **two** commits, and inventory
  (`BulkStoreObjectInventories`, separate `INSERT INTO primitems` around line 2527) is a
  third, so cross-table partial stores remain possible.
- **RemoveObject** (lines ~573-622): same non-transactional shape as ours —
  SELECT UUIDs, `DELETE FROM prims`, then separate `RemoveShapes(uuids)` /
  `RemoveItems(uuids)` calls.
- They did **not** adopt blob serialization for whole groups (their Thoosa/ProtoBuf
  serializer is used only for individual binary columns like KeyframeAnimation), nor a
  non-relational store for region objects.

**Design takeaway:** Halcyon reduced the *probability* of partial linksets (fewer, bigger
statements + bulk API) but never solved *atomicity*. A transaction wrap in our tree would
exceed what Halcyon achieved, and the two approaches compose: transaction for correctness,
optional batching later for performance.

---

## 7. Proposed minimal patch plan (design only — NOT implemented)

**Principle:** one `MySqlTransaction` per logical object operation, on the already-per-call
connection. No interface changes, no schema changes, no caller changes.

1. **`MySQLSimulationData.StoreObject` (MySQLSimulationData.cs:121)**
   - After `dbcon.Open()`: `using MySqlTransaction tx = dbcon.BeginTransaction();`
   - Assign `cmd.Transaction = tx;` on the command.
   - Wrap the `foreach (SceneObjectPart ...)` loop in `try { ...; tx.Commit(); }
     catch { tx.Rollback(); throw; }` (rollback in a nested try — a rollback on a dead
     connection itself throws; log and rethrow the original).
   - Isolation: default REPEATABLE READ is fine; these are pure writes keyed by PK, and
     `m_dbLock` already prevents in-process write-write races. No isolation tuning needed.
   - Commit point: once per linkset (one commit per 50-prim object instead of 100).
     Side benefit: measurably *faster* — autocommit fsync per row is the current cost.

2. **`MySQLSimulationData.StorePrimInventory` (line 1898)** — **highest priority**, it's the
   DELETE+INSERT. Same wrap: BEGIN before the DELETE at 1908, COMMIT after the item loop,
   ROLLBACK on any failure — which converts "crash destroys existing inventory" into
   "update is lost, old inventory intact."

3. **`MySQLSimulationData.RemoveObject` (line 251)** — yes, it needs the same treatment,
   though for hygiene rather than data loss: wrap SELECT+3×DELETE so orphan
   primshapes/primitems rows can't be created. Low risk, same mechanical pattern.

4. **Do NOT change in this patch** (keep it minimal/reviewable):
   - `ExecuteNonQuery`/`ExecuteReader` helpers — they already rethrow; unchanged semantics.
   - The upstream dirty-flag ordering (`SceneObjectGroup.cs:2416`,
     `SceneObjectPartInventory.cs:1523`) — a rollback makes "flag cleared but store failed"
     merely a *lost update until next change* instead of *persisted corruption*, which
     defuses the worst of it. Moving flag-clearing after a successful store is a worthwhile
     **follow-up** patch but touches scene-layer semantics (races with concurrent edits
     during the store) and should be reviewed separately.
   - Other multi-row writers in the same file (`StoreLandObject`'s landaccesslist
     DELETE+INSERT loop, `StoreRegionSettings`, environment/spawn-point writes) have the
     same disease at smaller blast radius — candidates for the same wrap in a follow-up,
     not the minimal patch.

5. **Files touched: exactly one** — `OpenSim/Data/MySQL/MySQLSimulationData.cs`
   (3 methods, ~30-40 lines of diff). No migrations, no interface, no config.
   (Reminder from repo conventions: `*.csproj` is gitignored; no new files are added so no
   Compile-include concern.)

6. **Precondition:** §5.2 engine check on production must show InnoDB for
   prims/primshapes/primitems first. If MyISAM appears, ALTER to InnoDB is a prerequisite
   maintenance task.

7. **Testing sketch (for the patch phase, not now):** on the `opensim_test_safe` DB, store a
   large linkset while killing the process mid-store (debugger breakpoint or a fault
   injection between prim iterations) and verify the DB shows either the complete old state
   or complete new state; repeat for the inventory path.

---

## 8. Open questions / unverified

1. **Live production config not read** (off-limits per audit rules): the live grid's
   `GridCommon.ini` under `D:\opensim - Use this december 2025\bin\` is assumed to point at
   the MySQL provider like the source tree's copy. Certain in practice, unverified in this
   audit.
2. **Actual production table engines** — must be confirmed with the §5.2 query. The
   migrations say InnoDB, but the live `opensim` schema is older than this source tree and
   `CREATE TABLE IF NOT EXISTS` never converts a pre-existing MyISAM table.
3. **`Old Guids=true` / MySql.Data connector version behavior** with transactions on pooled
   connections — no known issue, but the exact vendored MySql.Data.dll version's
   transaction behavior wasn't audited.
4. **Attachment persistence path** (attachments store via a different path —
   `UpdateKnownItem`/asset serialization, not `MySQLSimulationData.StoreObject`) was out of
   scope; in-world objects only.
5. **Other DB plugins** (SQLite/PGSQL variants in `OpenSim/Data/`) were not audited — they
   are not wired in our config (§2.6). If anyone ever flips `StorageProvider`, this audit
   does not transfer.
6. **YEngine/Phlox script-state persistence** uses separate stores and was not examined —
   a crash mid-backup can also desync script state vs. object state, a distinct (pre-existing)
   issue a StoreObject transaction will not fix.
