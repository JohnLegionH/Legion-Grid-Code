# Deserialization Attack-Surface Audit — Legion Grid (OpenSim 0.9.3.0 + Halcyon/InWorldz Phlox)

**Branch:** `github-snapshot`  **Date:** 2026-06-23  **Mode:** read-only recon + remediation spec (no source changed)
**Scope:** top-level `OpenSim/` tree (the one the build uses), **including** `OpenSim/Addons/Phlox/`.
**Out of scope / ignored:** the nested duplicate tree `opensim-dotnet8-modernization/`. Its `BinaryFormatter` hits mirror the top-level set exactly (same files: `KeyframeMotion.cs`, `XMRInstAbstract.cs`, `FlotsamAssetCache.cs`, `Util.cs`); **no sink exists *only* in the duplicate tree**, so it adds no new findings.

Every claim is cited `file:line`. Where a data-flow could not be confirmed in code it is marked **unverified**.

---

## Part 0 — Threat model in one paragraph

`BinaryFormatter.Deserialize` (and the sibling arbitrary-type formatters) reconstruct whatever .NET type the *byte stream* names, running constructors/`OnDeserialized`/`IDeserializationCallback` and finalizers on attacker-chosen types — the classic .NET RCE primitive. The danger is therefore not "do we deserialize" but "can attacker-influenced bytes reach an arbitrary-type deserializer." This fork feeds three such formatters from data that demonstrably arrives on **foreign-rezzed objects, region crossings, and imported archives**: `KeyframeMotion`, the **YEngine** script-state migration stream, and (write-side only on the wire) their producers. **Phlox, the primary script engine, does NOT use an arbitrary-type formatter** — it uses fixed-type protobuf-net and is the model the other sinks should be converted to (see §4).

---

## Part 1 — Foreign-input entry points and the deserialization they trigger

### 1.1 Region-crossing / teleport object transfer  → KeyframeMotion + YEngine state  **(CRITICAL)**

| Hop | file:line | What happens |
|---|---|---|
| Entry (HTTP POST `object/`) | `OpenSim/Server/Handlers/Simulation/ObjectHandlers.cs:98` `DoObjectPost(OSDMap args,…)` | reads `args["sog"]` → `sogXmlStr` (foreign object XML), line `:124-134` |
| Deserialize dispatch | `ObjectHandlers.cs:134` `sog = s.DeserializeObject(sogXmlStr)` → `Scene.DeserializeObject` → `SceneObjectSerializer.FromXml2Format` | no origin/content validation before parse |
| XML parse | `OpenSim/Region/Framework/Scenes/Serialization/SceneObjectSerializer.cs:287` `XmlDocument doc=new(); doc.LoadXml(xmlData)` (also `FromOriginalXmlFormat` at `:111`) | element names not type-fixed |
| **KeyframeMotion blob decode** | `SceneObjectSerializer.cs:334` (and `:145`) `KeyframeMotion.FromData(so, Convert.FromBase64String(<KeyframeMotion>))` | base64 is attacker-controlled inside the object XML |
| **SINK** | `OpenSim/Region/Framework/Scenes/KeyframeMotion.cs:317-318` `new BinaryFormatter().Deserialize(ms)` | **arbitrary-type deserialize of foreign bytes** |
| Script-state restore | `SceneObjectSerializer.cs:156 / :342` `sceneObject.LoadScriptState(...)` → stored in `m_savedScriptState` → `SceneObjectPartInventory.cs:594` / `SceneObjectGroup.Inventory.cs:589` `e.SetXMLState(itemID, xml)` on `CreateScriptInstances` | foreign script-state XML reaches the engine |
| YEngine state decode | `OpenSim/Region/ScriptEngine/YEngine/XMREngine.cs:1165` `SetXMLState` → `XMRInstCtor.cs:537-544` `Convert.FromBase64String(<Snapshot>)` → `MigrateInEventHandler(ms)` → `MigrateIn(br)` | base64 snapshot attacker-controlled |
| **SINK (YEngine)** | `XMRInstAbstract.cs:1987` (`Ser.SYSERIAL`) and `:1999` (`Ser.THROWNEX`) `new BinaryFormatter().Deserialize(ms)`; plus `:1813` `Type.GetType(str,true)` from the same stream | **arbitrary-type deserialize + arbitrary type resolution of foreign bytes** |

Both sinks are reachable from a single foreign object crossing into an HG-enabled region. **Verified** end-to-end in code.

### 1.2 Rez-from-asset / inventory rez  → same two sinks  **(CRITICAL, conditional)**

- `OpenSim/Region/Framework/Scenes/SceneObjectPartInventory.cs:1157-1171` fetches `AssetService.Get(...)` then `GetObjectsToRez(rezAsset.Data,…)`.
- `OpenSim/Region/Framework/Scenes/Scene.Inventory.cs:2572` `BytesToString(assetData)` → `:2588 / :2621` `SceneObjectSerializer.FromOriginalXmlFormat(...)` → same KeyframeMotion (`SceneObjectSerializer.cs:145`) and script-state path as §1.1.
- `OpenSim/Region/ScriptEngine/Shared/Api/Implementation/LSL_Api.cs:17032` (`llGodLikeRezObject`) → `FromOriginalXmlFormat` (same chain).
- Trust: object assets can originate from HG inventory / foreign assets, so the asset bytes are foreign-influenceable. **Verified** to the sink; foreignness of any *given* asset is deployment-dependent (**conditional**).

### 1.3 Archive import — IAR and OAR  → same two sinks  **(CRITICAL)**

- **IAR**: `OpenSim/Region/CoreModules/Avatar/Inventory/Archiver/InventoryArchiveReadRequest.cs:554` `SceneObjectSerializer.ModifySerializedObject(...)` → `SceneObjectSerializer.cs:386` → `FromOriginalXmlFormat` → KeyframeMotion (`:145`) + script-state.
- **OAR**: `OpenSim/Region/CoreModules/World/Archiver/ArchiveReadRequest.cs:646` `DeserializeGroupFromXml2(...)` → `SceneXmlLoader.cs:228` → `SceneObjectSerializer.FromXml2Format` → KeyframeMotion (`:334`) + script-state; OAR object-assets via `ArchiveReadRequest.cs:1031` `ModifySerializedObject`.
- Inventory items / land / region-settings use **fixed-type XML** serializers (`UserInventoryItemSerializer.cs:191`, `LandDataSerializer.cs:151`, `RegionSettingsSerializer.cs:65`) — safe. Terrain uses format-specific loaders (`RAW32.cs:63` `BinaryReader.ReadSingle`) — data-only (**unverified** robustness, not RCE).
- Archives are untrusted (handed across grids / downloaded). **Verified** to the sinks.

### 1.4 HG service HTTP handlers (Gatekeeper / UserAgent / HG friends / asset / inventory)  → data-only OSD/XML  **(MEDIUM — DoS)**

| Entry point | file:line | Format | Deserializer reached | Size/depth bound? |
|---|---|---|---|---|
| Gatekeeper `foreignagent` POST | `OpenSim/Server/Handlers/Hypergrid/AgentHandlers.cs:60` (`AgentPostHandler.ProcessRequest`) | JSON/gzip | `Server/Handlers/Simulation/Utils.cs:90` `OSDParser.DeserializeJson(stream)` → `AgentCircuitData.UnpackAgentCircuitData` (`Framework/AgentCircuitData.cs`) → `AvatarAppearance.Unpack` | **No** |
| UserAgent `homeagent` POST | `OpenSim/Server/Handlers/Hypergrid/HomeAgentHandlers.cs:52` (inherits `AgentPostHandler`) | JSON/gzip | same as above | **No** |
| HG friends POST | `OpenSim/Server/Handlers/Hypergrid/HGFriendsServerPostHandler.cs:71` | form-enc | `StreamReader.ReadToEnd` (`:76`) → `ServerUtils.ParseQueryString` → `FriendInfo(dict)` | **No** |
| Gatekeeper XML-RPC `link_region`/`get_region` | `OpenSim/Server/Handlers/Hypergrid/HypergridHandlers.cs:59 / :86` | XML-RPC | Nwc.XmlRpc → `Hashtable` (string fields) | **unverified** (library) |
| UserAgent XML-RPC (≈12 handlers) | `OpenSim/Server/Handlers/Hypergrid/UserAgentServerConnector.cs:116-487` | XML-RPC | Nwc.XmlRpc → `Hashtable` | **unverified** |
| HG IM | `OpenSim/Server/Handlers/Hypergrid/InstantMessageServerConnector.cs:87` | XML-RPC + base64 | Nwc.XmlRpc; `binary_bucket` `Convert.FromBase64String` (`:203`) — **not** further deserialized | **unverified** |
| Asset POST (also HG asset) | `OpenSim/Server/Handlers/Asset/AssetServerPostHandler.cs:64-72` | XML | `XmlSerializer(typeof(AssetBase)).Deserialize(stream)` — **fixed type** | **No** (unbounded stream) |
| XInventory POST | `OpenSim/Server/Handlers/Inventory/XInventoryInConnector.cs:95-108` | form-enc | `ReadToEnd` → `ParseQueryString` | **No** |

None of these reaches an arbitrary-type formatter directly; risk is **DoS via unbounded parse** (no Content-Length / depth gate before `OSDParser`/`XmlSerializer`/`ReadToEnd`). The OSD payload *does* carry the agent circuit, but it is hand-unpacked field-by-field, not type-resolved. **Important caveat:** an HG asset fetched through these handlers becomes an `AssetBase` whose `.Data` later flows into §1.2 — so HG asset fetch is an *upstream feeder* of the CRITICAL rez path, not a sink itself.

### 1.5 Script-state restore per engine

- **YEngine** — arbitrary-type. See §1.1 (`XMRInstAbstract.cs:1987/1999`, `:1813`). **CRITICAL**.
- **Phlox** — fixed-type protobuf-net. `StateManager.cs:146` `ProtoBuf.Serializer.Deserialize<SerializedRuntimeState>(ms)` from the **local** `script_state` SQLite table (`:130-146`); compiled-script cache via `PhloxScriptLoader.cs:296` `Serializer.Deserialize<SerializedScript>`. Contracts are fixed `[ProtoContract]`/`[ProtoMember]` types (`SerializedRuntimeState.cs`, `SerializedScript.cs`, `SerializedLSLPrimitive.cs`) with **no** `DynamicType`/`AsReference`. **Not vulnerable to the RCE class** (DoS only on malformed input). **Verified.**

---

## Part 2 — Every deserialization sink (grep of top-level `OpenSim/` incl. Phlox)

### 2.1 Arbitrary-type formatters (highest risk)

| # | file:line | call | direction |
|---|---|---|---|
| S1 | `OpenSim/Region/Framework/Scenes/KeyframeMotion.cs:317-318` | `new BinaryFormatter().Deserialize(ms)` | **read** |
| S2 | `OpenSim/Region/Framework/Scenes/KeyframeMotion.cs:839` | `new BinaryFormatter()` … `.Serialize` | write (produces the blob) |
| S3 | `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1987` (`SYSERIAL`) | `BinaryFormatter.Deserialize` | **read** |
| S4 | `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1999` (`THROWNEX`) | `BinaryFormatter.Deserialize` | **read** |
| S5 | `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1713 / :1725` | `BinaryFormatter.Serialize` | write (produces the migration blob) |
| S6 | `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs:534-535` | `new BinaryFormatter().Deserialize(stream)` | **read** (self-written disk cache) |
| S7 | `OpenSim/Region/CoreModules/Asset/FlotsamAssetCache.cs:1078-1079` | `BinaryFormatter.Serialize` | write |
| S8 | `OpenSim/Framework/Util.cs:2400-2401` | `new BinaryFormatter().Deserialize(stream)` in `DeserializeFromFile` | **read** — **no callers in tree** (dead utility) |
| S9 | `OpenSim/Framework/Util.cs:2383-2387` | `BinaryFormatter.Serialize` in `SerializeToFile` | write — **no callers in tree** |
| — | `OpenSim/Region/PhysicsModules/ubOdeMeshing/Meshmerizer.cs:1289` | `// BinaryFormatter …` | **commented out** — not a live sink |

`NetDataContractSerializer` / `SoapFormatter` / `LosFormatter` / `ObjectStateFormatter`: **none in real code.** The only grep hits are WinForms `.resx` resources under `OpenSim/Tools/LaunchSLClient/` (`Resources.resx`, `Form1.resx`) — designer metadata, not runtime sinks.

### 2.2 Fixed-type / data-only serializers (lower risk)

- **protobuf-net (Phlox)** — `StateManager.cs:146`, `PhloxScriptLoader.cs:296`; contracts in `OpenSim/Addons/Phlox/InWorldz.Phlox/Serialization/*.cs`. Fixed generic type args, no dynamic typing. Safe (DoS only).
- **XmlSerializer** — all observed uses pass a **fixed `typeof(...)`**: `AssetServerPostHandler.cs:68` (`AssetBase`), `AssetsExistHandler.cs:72/83`, `InventoryServerMoveItemsHandler.cs:62`, `AuthorizationServerPostHandler.cs:60/68`, `OfflineIMService.cs:93`, `TaskInventoryDictionary.cs:52`, `XBakesModule.cs:55`, `TerrainChannel.cs:476/483/525/532`, `TreePopulatorModule.cs:583/601`, the `RestSessionService.cs`/`RestDeserialiseHandler.cs` generics, `BSPerformanceBaseline.cs:365/389`. Type is not stream-driven → not an arbitrary-type vector. Residual: XML DoS/XXE; modern .NET `XmlSerializer` prohibits DTD by default (**unverified** per-call reader settings).
- **System.Text.Json** — fixed `Deserialize<T>` only (e.g. `BotPersistenceManager.cs:441/475/1116`, `DestructionPersistence.cs:174/269`, `EntityTransferModule.cs:4109`, Phlox `LSLSystemAPI.cs:9117`). No `Newtonsoft`, **no `TypeNameHandling`, no custom `SerializationBinder` anywhere.** Safe.

### 2.3 OSDParser / LLSD (data-only; DoS on unbounded HG/cap paths — MEDIUM)

Representative unbounded parses of a remote stream (no size/depth gate): `Server/Handlers/Simulation/Utils.cs:90`, `Server/Handlers/Base/Utils.cs:107`, `Framework/Servers/HttpServer/BaseHttpServer.cs:1431/1501/1533`, `Capabilities/Handlers/FetchInventory/*Handler.cs`, `Region/ClientStack/Linden/Caps/BunchOfCaps/BunchOfCaps.cs:331/706/798`, `…/MeshCost.cs:373` (`DeserializeLLSDBinary`), `OptionalModules/Materials/MaterialsModule.cs:410/484/564/900`, `Addons/Groups/GroupsModule.cs:470/520` (IM bin-bucket), `WorldMap/WorldMapModule.cs:867` (remote grid reply). Data-only (no type instantiation) → not RCE; unbounded → DoS.

### 2.4 Type-from-input primitives

- **Dangerous (foreign-reachable):** `XMRInstAbstract.cs:1813` `Type.GetType(str,true)` and `XMRInstCtor.cs:962/969` `Type.GetType(itemType)` — `str`/`itemType` come from the YEngine migration stream / script-state XML (foreign, see §1.1). Companion to S3/S4. `MMRScriptObjWriter.cs:940` is the matching write side.
- **Safe (literal/config/plugin):** all `Activator.CreateInstance` / `Assembly.LoadFrom` hits resolve from config DLL paths or interface-filtered plugin loading — `ServerUtils.cs:273/290`, `ServiceBase.cs:73/90`, `ConfigurationMember.cs:498/512`, `PluginLoader.cs:285`, `RegionModulesControllerPlugin.cs:146/150/375/377`, `TerrainModule.cs:661/683/689`, `ApiManager.cs:76`, `AsyncHttpService.cs:192` (hard-coded type string). No foreign type/assembly names.

### 2.5 Custom binary readers on foreign data (robustness, not RCE)

- `OpenSim/Framework/Serialization/TarArchiveReader.cs:76` `new BinaryReader(s)` — OAR/IAR tar parsing of untrusted archives (malformed-input robustness; **unverified**).
- `LinksetData.FromXML`/`FromBin` (`OpenSim/Region/Framework/Scenes/LinksetData.cs:471`) — base64→custom binary, **fixed type** `LinksetData`, reached from the same foreign object XML as KeyframeMotion (`SceneObjectSerializer.cs:151/338`). Not BinaryFormatter; robustness **unverified**.
- Terrain/mesh `MemoryStream(remoteBytes)` paths (`MeshCost.cs:369`, `EstateManagementModule.cs:1349`, `TerrainData.cs:566/675/727/784`) — data-only.

---

## Part 3 — Sink table (classify & rank)

| ID | file:line | serializer | data shape | persisted where | reachable from foreign input? | risk |
|---|---|---|---|---|---|---|
| S1 | KeyframeMotion.cs:317 | BinaryFormatter (read) | `KeyframeMotion` graph (frames, vectors, quaternions, timing) | object XML (DB blob + wire base64 + OAR/IAR) | **YES** — crossing §1.1, rez §1.2, archive §1.3 | **CRITICAL** |
| S3 | XMRInstAbstract.cs:1987 | BinaryFormatter (read) | arbitrary `[Serializable]` script global/stack object (`SYSERIAL`) | YEngine `.state`/script-state XML (wire base64) | **YES** — §1.1/§1.2/§1.3 via `SetXMLState` | **CRITICAL** |
| S4 | XMRInstAbstract.cs:1999 | BinaryFormatter (read) | `ScriptThrownException` graph (`THROWNEX`) | same as S3 | **YES** | **CRITICAL** |
| T1 | XMRInstAbstract.cs:1813 (+XMRInstCtor.cs:962/969) | `Type.GetType` from stream | type name string | same as S3 | **YES** | **CRITICAL** (companion to S3/S4) |
| S6 | FlotsamAssetCache.cs:535 | BinaryFormatter (read) | `AssetBase` (we wrote it at S7) | local disk cache file | No (self-written cache); foreign only via local FS tamper | **LOW** (.NET 9 liability + defense-in-depth) |
| S8 | Util.cs:2401 | BinaryFormatter (read) | `object` (any) | local file | **No callers in tree** | **LOW** (latent liability) |
| S2/S5/S7/S9 | KeyframeMotion.cs:839; XMRInstAbstract.cs:1713/1725; FlotsamAssetCache.cs:1079; Util.cs:2387 | BinaryFormatter (write) | producers of the above | — | n/a (write) | must be converted *with* their readers (format compat) |
| X1 | AssetServerPostHandler.cs:72 | XmlSerializer `AssetBase` (read) | fixed type | wire | **YES** (HG asset POST) | **MEDIUM** (DoS/unbounded; type fixed) |
| O1 | Utils.cs:90; BaseHttpServer.cs:1431/1501/1533; BunchOfCaps.cs:798; GroupsModule.cs:470 (+ §2.3 list) | OSDParser (read) | LLSD/JSON map | wire | **YES** | **MEDIUM** (DoS, no size/depth bound) |
| P1 | StateManager.cs:146; PhloxScriptLoader.cs:296 | protobuf-net fixed type (read) | `SerializedRuntimeState`/`SerializedScript` | local SQLite + cache | local now; format is wire-capable | **LOW** (DoS only; correct design) |
| B1 | LinksetData.cs:471; TarArchiveReader.cs:76; terrain loaders | custom binary (read) | fixed types / numeric arrays | object XML / archive | **YES** | **LOW** (robustness, not RCE) |

### `EnableUnsafeBinaryFormatterSerialization`

**Set `true` in BOTH** runtime config templates:
- `OpenSim/Region/Application/runtimeconfig.template.json:8`
- `OpenSim/Server/runtimeconfig.template.json:8`

It is **required by S1/S3/S4 (and S6/S8, S2/S5/S7/S9)**. It cannot be removed until *all* live `BinaryFormatter` read paths above are converted. (The build outputs under `bin/*.runtimeconfig.json` mirror this and are regenerated from the templates.)

---

## Part 4 — BinaryFormatter replacement spec (design, not code)

General rule applied below: **untrusted sinks get a hard-cut to a new safe format with NO legacy read path; locally-owned data may keep an offline one-time migration.** Never `BinaryFormatter`-deserialize foreign bytes "for compatibility."

### S1 — KeyframeMotion (`KeyframeMotion.cs:317` read / `:839` write) — bucket: **FOREIGN (untrusted)**
- **Data shape:** a `KeyframeMotion` object — list of keyframes (`Vector3 Position`, `Quaternion Rotation`, `int TimeMS`/ticks), play mode/loop enum, current index, selected/crossing flags, serialized position. All value types — no polymorphism needed. (Read the class fields to finalize the exact list.)
- **Proposed safe format:** explicit length-prefixed binary — `magic "KFM1"` + `uint version` + `int frameCount` + per-frame fixed fields via `BinaryWriter`/`BinaryReader` of primitives, plus enums written as `int`. No type names in the stream. Bounded `frameCount` (reject absurd counts) and bounded total length.
- **Backward-compat (split by trust):**
  - The blob travels on the **wire** (object XML base64) and in the **region DB** and in **OAR/IAR**. Because the *same* blob format is used for foreign and local, the live decode path (`FromData`) is an **untrusted sink** → it must accept **only** `KFM1` and **drop** (return null) on any non-matching/legacy bytes. The existing `catch → newMotion = null` (`KeyframeMotion.cs:335-338`) already degrades gracefully, so dropping legacy keyframe data is non-fatal.
  - Locally-owned region DB rows may be upgraded by an **offline one-time migration** (read legacy blob in a controlled pass, re-write as `KFM1`). This is optional — keyframe motion is cosmetic and regenerable by re-saving the object.
- **Loss on upgrade:** in-flight legacy keyframe blobs on un-migrated objects stop animating until re-saved. **Disposable.**
- **Vanilla-peer impact:** the base64 lives **inside object XML that other grids parse**. If we change the bytes, a vanilla peer receiving our object can no longer decode our keyframe blob (it would hit its own `catch`→null). To stay wire-compatible we must either (a) keep emitting a format vanilla understands (defeats the fix on the *outbound* side) or (b) accept that cross-grid keyframe motion degrades to "no motion" with vanilla peers. **Decision point (see Part 5).** The *inbound* safety fix (refuse legacy on our side) is independent and should ship regardless.

### S3/S4/T1 — YEngine script-state migration (`XMRInstAbstract.cs:1987/1999/:1813`, write `:1713/:1725`) — bucket: **FOREIGN (untrusted)**
- **Data shape:** the migration stream is *already* a hand-rolled tagged format (`Ser.*` opcodes via `MMRScriptObjWriter`/`BinaryReader`). Only two opcodes escape to `BinaryFormatter`: `SYSERIAL` (an arbitrary `[Serializable]` value parked in a script global/stack slot) and `THROWNEX` (`ScriptThrownException`). `T1` (`Type.GetType`) resolves the runtime type of script-defined objects.
- **Proposed safe format:** replace the two `BinaryFormatter` opcodes with explicit field encodings. `THROWNEX`: serialize the known fields of `ScriptThrownException` (the thrown LSL value is *already* sent separately via `SendObjValue`/`RecvObjValue` at `:1718/:2001`), so the formatter call is nearly redundant — encode the small fixed remainder by hand. `SYSERIAL`: restrict to the finite set of LSL-representable system types the VM can actually hold and encode each explicitly; reject any other type. For `T1`, replace `Type.GetType(str,true)` with a **fixed allow-list lookup** (the method at `:1789-1812` already maps the common cases by short code — extend that table and make the fallback *throw* instead of calling `Type.GetType`).
- **Backward-compat (split by trust):** YEngine state arrives on **crossings/rez/archives = untrusted**. The live `MigrateIn` path must accept **only** the new encoding and **refuse** (abort restore for that item) legacy `SYSERIAL`/`THROWNEX`/unknown-type bytes. **No legacy `BinaryFormatter` fallback.** Running-script state is **regenerable** (script resets to default state / re-runs `state_entry`), so refusal is acceptable.
- **Loss on upgrade:** scripts crossing/rezzing with legacy serialized state that *used* `SYSERIAL`/`THROWNEX` lose that running state and restart. In practice these opcodes are rare (most state uses the explicit `Ser.*` value encodings). **Mostly disposable.**
- **Vanilla-peer impact:** YEngine `.state` XML is exchanged with other OpenSim grids on crossing. Changing the snapshot encoding means a vanilla YEngine peer cannot restore our snapshot (script restarts there) and vice-versa. **Decision point (Part 5).** Note Phlox is the primary engine here (§4), limiting real exposure.

### S6/S7 — FlotsamAssetCache (`FlotsamAssetCache.cs:535` read / `:1079` write) — bucket: **LOCALLY-OWNED**
- **Data shape:** `AssetBase` (id, full-id, name, description, type byte, flags, `byte[] Data`, metadata). Fixed type.
- **Proposed safe format:** fixed `XmlSerializer(typeof(AssetBase))` (already used at `XBakesModule.cs:55`/`AssetServerPostHandler.cs`) **or** an explicit length-prefixed binary (`AC01` magic + fields + length-prefixed `Data`). No type resolution.
- **Backward-compat:** cache is **self-written local disk** → an **offline one-time migration** is acceptable, but simplest is **hard-cut**: on version mismatch treat the file as a cache miss and re-fetch (`GetFromFileCache` already returns null on failure, `:539-549`). Cache is 100% regenerable. **Bucket: locally-owned; strategy: hard-cut/treat-as-miss.**
- **Loss:** none (cache repopulates). **Vanilla impact:** none (private on-disk format).

### S8/S9 — Util.cs `DeserializeFromFile`/`SerializeToFile` — bucket: **DEAD CODE**
- **No callers in the top-level tree** (grep: only the definitions at `Util.cs:2381/2395`). Recommend **delete** both (removes a latent generic `object` deserializer) rather than convert. If a caller is later found, treat per that caller's trust bucket.

### Phlox (reference, no change) — bucket: **LOCALLY-OWNED, already safe**
- Already fixed-type protobuf-net from local SQLite + cache (`StateManager.cs:146`, `PhloxScriptLoader.cs:296`). It is the **target design** for the conversions above. Only hardening worth noting: bound the input blob size before `Deserialize` to cap protobuf DoS, and (if Phlox state ever moves to the wire) keep the fixed-type contract — never switch to dynamic typing.

---

## Part 5 — Decision points for the two developers

1. **Wire/asset compatibility vs. clean fix (S1 KeyframeMotion, S3/S4 YEngine).** The unsafe blobs live inside object-XML and `.state`-XML that other OpenSim/HG grids parse. Choose per sink:
   (a) **Inbound-only hardening now** (refuse legacy on *our* read path) + keep emitting legacy outbound for peer compat — closes the RCE hole we care about, preserves interop, but our own outbound is still legacy-shaped; or
   (b) **Full hard-cut** to the new format both directions — cleanest, but cross-grid keyframe motion / YEngine running-state degrades to "reset" against vanilla peers.
   *Recommendation:* ship (a) immediately for both (the RCE is on the **inbound** side), schedule (b) once peers are known to not matter or are also upgraded.
2. **Retain any legacy `BinaryFormatter` read path?** *Recommendation:* **No** on every untrusted sink (S1/S3/S4). Yes-but-offline-only is acceptable solely for locally-owned data (S6 cache migration, optional KeyframeMotion DB migration) and must be a controlled/offline pass, never the live network path.
3. **Per-data-type hard-cut vs migrate.** Disposable (hard-cut): YEngine running state, Flotsam cache, in-flight keyframe blobs. Optional migrate: region-DB KeyframeMotion rows (cosmetic). Must-migrate: **none** — no must-keep data sits behind these sinks.
4. **Gate `EnableUnsafeBinaryFormatterSerialization` removal on all sinks converted?** *Recommendation:* **Yes.** Removing the flag is the final step after S1, S3, S4, S6 (and deletion of S8/S9). It is the single switch that proves no live path needs the formatter, and it is a hard requirement for the .NET 9 target (the flag is removed from the runtime there).
5. **Recommended remediation order:**
   1. **S3/S4/T1 — YEngine migration** (untrusted, arbitrary-type + arbitrary `Type.GetType`; reachable from any foreign crossing).
   2. **S1 — KeyframeMotion** (untrusted, arbitrary-type; same reachability).
   3. **Bound OSD/XML parse sizes** on HG/cap endpoints (§2.3, X1) — cheap DoS mitigation, no format break.
   4. **S6 — Flotsam cache** (local; hard-cut/treat-as-miss).
   5. **Delete S8/S9** (dead utility).
   6. **Remove `EnableUnsafeBinaryFormatterSerialization`** from both runtimeconfig templates; build & smoke-test crossing/rez/OAR/IAR.

---

## One-screen summary

**Sinks by risk tier (live code, top-level tree):**
- **CRITICAL: 3 deserialize sinks + 1 companion** — `KeyframeMotion.cs:317` (S1); `XMRInstAbstract.cs:1987` (S3) & `:1999` (S4); companion `Type.GetType` at `XMRInstAbstract.cs:1813` (+`XMRInstCtor.cs:962/969`).
- **MEDIUM:** unbounded `OSDParser`/`XmlSerializer` on HG & cap endpoints (DoS) — `AssetServerPostHandler.cs:72`, `Server/Handlers/Simulation/Utils.cs:90`, `BaseHttpServer.cs:1431/1501/1533`, `BunchOfCaps.cs:798`, `GroupsModule.cs:470`, et al.
- **LOW:** `FlotsamAssetCache.cs:535` (S6, self-written cache), `Util.cs:2401` (S8, dead), custom binary readers (`TarArchiveReader.cs:76`, `LinksetData.cs:471`, terrain loaders) — robustness/.NET-9 liabilities.
- **Write-side producers** to convert alongside their readers: `KeyframeMotion.cs:839`, `XMRInstAbstract.cs:1713/1725`, `FlotsamAssetCache.cs:1079`, `Util.cs:2387`.

**CRITICAL list (file:line):**
- `OpenSim/Region/Framework/Scenes/KeyframeMotion.cs:317`
- `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1987`
- `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1999`
- `OpenSim/Region/ScriptEngine/YEngine/XMRInstAbstract.cs:1813` (arbitrary `Type.GetType` from the same foreign stream)

**Is Phlox affected?** **No.** Phlox (the primary script engine) serializes script state with fixed-type protobuf-net (`StateManager.cs:146`, `PhloxScriptLoader.cs:296`; contracts in `InWorldz.Phlox/Serialization/*`), no `BinaryFormatter`, no `DynamicType`/`AsReference`, sourced from the local SQLite `script_state` table. It is the safe model to emulate. (Residual: cap protobuf input size for DoS.)

**`EnableUnsafeBinaryFormatterSerialization`:** still `true` in `OpenSim/Region/Application/runtimeconfig.template.json:8` **and** `OpenSim/Server/runtimeconfig.template.json:8`; required by S1/S3/S4 (and S6/S8).

**Single highest-priority fix:** Eliminate the **YEngine migration `BinaryFormatter`/`Type.GetType` path** (S3/S4/T1, `XMRInstAbstract.cs:1987/1999/1813`) — it is an arbitrary-type deserializer **and** arbitrary type-resolver fed by base64 script-state that rides in on every foreign object crossing, rez, and archive import. Hard-cut it to an explicit fixed-type encoding with a type allow-list and **no legacy read path**.
