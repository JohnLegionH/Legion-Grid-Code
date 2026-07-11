# GeneratedVegetationModule — build, deploy & plan contract

`OpenSim/Region/CoreModules/World/Vegetation/GeneratedVegetationModule.cs` is the
in-grid **vegetation rezzer**: it reads a vegetation *plan JSON* and rezzes the
trees into the console's current region, stamping each with a dedicated GroupID so
they can be cleared without touching hand-placed content.

The plan JSON is produced by the **separate `legion-tools` project** (formerly
`tools/terrain-gen/`, split out of this repo 2026-07; lives at `D:\legion-tools`
locally / its own GitHub repo). This module — the C# consumer — stays in the grid
repo **by design**; the plan JSON is the contract between the two.

## Console usage
Operate on the `change region`-selected region:
```
vegetation plant <planfile.json>   # rez the plan's trees (paced, progress every 500)
vegetation clear-generated         # remove ONLY GENERATED_VEG_GROUP trees
```
`plant` is **not idempotent** (each rez gets fresh UUIDs → duplicates). Iterate with
`clear-generated` → `plant`.

## Plan JSON contract (producer: legion-tools `vegetation_plan.py`)
```json
{
  "meta":  { "group_uuid": "<must equal the module's GENERATED_VEG_GROUP>" },
  "trees": [
    { "code": 255,           // OpenMetaverse PCode (255 = Tree/renderable)
      "x": 128.0, "y": 96.0, "z": 21.5,     // region-local metres
      "sx": 1.0, "sy": 1.0, "sz": 1.0,      // per-axis scale
      "rot": 0.0 }                          // Z rotation, radians
  ]
}
```
`group_uuid` mismatch is rejected (safety: prevents `clear-generated` from ever
deleting the wrong content).

## Build / deploy  (READ THIS on a fresh clone)
- This `.cs` compiles into `OpenSim.Region.CoreModules.dll`.
- **`runprebuild` is FORBIDDEN on this tree** — it clobbers the hand-maintained,
  gitignored `.csproj` files. Do **not** regenerate them.
- A fresh clone must add the compile include **by hand**. In
  `OpenSim/Region/CoreModules/OpenSim.Region.CoreModules.csproj`, next to the
  existing `VegetationModule.cs` include:
  ```xml
  <Compile Include="World\Vegetation\GeneratedVegetationModule.cs">
    <SubType>Code</SubType>
  </Compile>
  ```
  Then build the solution normally (`dotnet build --configuration Release OpenSim.sln`).
- **Deploy the COMPLETE `OpenSim*.dll` set — never cherry-pick one DLL.**
  `IVegetationModule` is implemented by both `OpenSim.Region.CoreModules.dll` (the
  preferred core `VegetationModule`) and `OpenSim.Region.OptionalModules.dll`
  (`TreePopulatorModule`). A partial deploy left a stale sibling resolved and trees
  rezzed as unrenderable `PCode.NewTree` (111). Recommended live config:
  `[Trees] enabled = false` to remove the `TreePopulatorModule` ambiguity at source.

The plant-done log line reports the resolved module and the first tree's PCode
(255 = renderable, 111 = invisible) so any regression of this class self-announces.
