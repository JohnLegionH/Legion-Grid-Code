# Legion Grid — SDK-style build migration plan

**Status:** DESIGN / FOR REVIEW — no conversion work has started.
**Author:** drafted for John's review, 2026-08-01.
**Branch this describes:** work would branch from `slua-tier2-tables`.
**Reference implementation:** `/d/tranquillity-develop` (OpenSim-NGC / Mike's fork) — same codebase lineage,
already migrated. We copy what worked and deliberately diverge on one thing: the dependency feed.

---

## 0. The design principle

> **`git clone && dotnet build` must just work — for anyone, with no auth, no tokens, no private feeds,
> and no environment setup beyond the .NET SDK.**

Every decision below is subordinate to that. Where "more modern" conflicts with "zero-friction setup",
zero-friction wins.

This is the one place we consciously do NOT follow Tranquillity. Their migration is technically sound but
consumes OpenMetaverse from an **authenticated private GitHub Packages feed**
(`nuget.pkg.github.com/OpenSim-NGC`, credentialed via `%GITHUB_ACTOR%` / `%GITHUB_TOKEN%` in `nuget.config`).
For anyone without those credentials the restore returns **401** — which is what broke Royale's Linux build
and what blocks fork CI. Legion will not have that failure mode, by construction.

---

## 1. The dependency model — the crux

### 1.1 The rule (total, no exceptions)

Every third-party dependency resolves to exactly one of two categories:

| # | Category | Mechanism | When |
|---|---|---|---|
| 1 | **Public package** | `<PackageReference>` from **nuget.org only** | The package genuinely exists on public nuget.org at a usable version |
| 2 | **Vendored in-repo** | `<Reference Include HintPath="$(RepoRoot)Library/X.dll">`, DLL **committed to the repo** | Everything else — no public package, a custom/forked build, or an ancient unmaintained lib |

> **There is no third category. A private or authenticated feed is never an option.**
> If a dependency cannot be resolved from public nuget.org, it gets vendored. Full stop.

The rule being *total* is what makes it safe: any dep we misclassify as "public" simply falls back to
"vendored", which always works offline. There is no failure mode where a builder needs a credential.

### 1.2 Inventory and disposition

Legion currently consumes **48 tracked DLLs under `bin/`** (plus natives), resolved by prebuild's
`<ReferencePath>../../bin/` + `<Reference name="X">` convention. Proposed disposition:

#### (A) Vendored — `Library/` (no public package, or forked build)

| DLL | Why vendored |
|---|---|
| `OpenMetaverse.dll` | ★ Legion's own libomv build (from `D:\legion-grid-webrtc\bin\`). Not public. **This is the one Tranquillity gets from the private feed.** |
| `OpenMetaverseTypes.dll` | as above |
| `OpenMetaverse.StructuredData.dll` | as above |
| `OpenMetaverse.Rendering.Meshmerizer.dll` | as above |
| `BulletXNA.dll` | OpenSim-specific fork — *Tranquillity vendors this too* |
| `C5.dll` | *Tranquillity vendors* |
| `DotNetOpenId.dll` | ancient; *Tranquillity vendors* |
| `LukeSkywalker.IPNetwork.dll` | *Tranquillity vendors* |
| `NDesk.Options.dll` | ancient; *Tranquillity vendors* |
| `Nini.dll` | ancient; *Tranquillity vendors* |
| `netcd.dll` | *Tranquillity vendors* |
| `XMLRPC.dll` | *Tranquillity vendors as `xmlrpc.dll`* |
| `Warp3D.dll` | OpenSim-specific |
| `PrimMesher.dll` | OpenSim-specific |
| `Tools.dll` | unidentified/local — vendor by default |
| `Mono.Data.Sqlite.dll`, `Mono.Data.SqliteClient.dll`, `Mono.Security.dll` | legacy Mono libs |

**Strong validation:** Tranquillity's own `Library/` folder contains exactly **8** DLLs —
`BulletXNA, C5, DotNetOpenId, LukeSkywalker.IPNetwork, NDesk.Options, Nini, netcd, xmlrpc`.
Every one is on our vendor list above. Their vendor set is effectively a proven answer to
"what has no public package"; Legion's list is theirs **plus the 4 OpenMetaverse assemblies** that they
chose to serve from the private feed and we choose to vendor.

**Legion's vendor list ≈ Tranquillity's vendor list + OpenMetaverse.** That single delta is the whole
difference between "clone and build" and "clone, get a token, configure a feed, and build".

#### (B) Public nuget.org — `PackageReference`

Expected to resolve from public nuget.org (Tranquillity already consumes several of these publicly):

`log4net`, `Newtonsoft.Json`, `MySql.Data`, `Npgsql`, `MailKit`, `MimeKit`,
`BouncyCastle.Cryptography`, `ICSharpCode.SharpZipLib` (SharpZipLib), `Mono.Cecil`,
`Mono.Addins` + `Mono.Addins.Setup` + `Mono.Addins.CecilReflector`, `RestSharp`, `ZstdNet`,
`Microsoft.Extensions.Logging.Abstractions`, `System.Configuration.ConfigurationManager`,
`System.Runtime.Caching`, `System.Security.Permissions`, `nunit.framework` (NUnit),
`Ionic.Zip` (→ DotNetZip or a maintained fork), `CSJ2K` (or `CoreJ2K` as Tranquillity uses),
`zlib.net` (or `zlib.net-mutliplatform` as Tranquillity uses).

> ⚠️ **Honesty note:** this classification is a *design-time proposal based on general knowledge of
> nuget.org, not a verified query per package.* Conversion must begin with a one-time verification pass
> (`dotnet package search` / nuget.org) to confirm each package + a usable version exists. **Any package
> that fails verification simply moves to the vendored list** — the rule absorbs the error with no
> redesign and no risk to the zero-auth guarantee.

#### (C) Native / unmanaged — copied, never referenced

`lib64/BulletSim*.dll`, `lib64/ubode.dll`, `lib64/sqlite3.dll`, `lib64/openjpeg-dotnet-x86_64.dll`,
`libzstd.dll`, and (post-Jolt) `joltc.dll`. These are **not** assembly references — see §3.2.

#### (D) Disappears with the migration

`prebuild.dll` — the prebuild tool itself. Deleted along with `prebuild.xml` and `runprebuild.*`.

### 1.3 The tradeoff, stated plainly

**Cost of vendoring:** ~17 DLLs committed to the repo (a few tens of MB), and manual version bumps —
updating a vendored lib means committing a new binary rather than editing a version string. Binaries in
git also bloat clone size permanently (git history keeps every version ever committed).

**What it buys:** the build has **zero external dependencies beyond nuget.org itself**, works offline
after one restore, works identically on Linux/macOS/Windows, works in any CI with no secrets, and — the
decisive point — **works for a grid operator you hand the repo to, on day one, with no support call.**

**Is this right for Legion?** Yes. Legion is a single-operator grid with occasional distribution to other
grid operators. Those operators are precisely the population Mike's private feed fails. Legion has no
community-package-publishing obligation that would justify a feed, and no CI matrix that would benefit.
The repo-size cost is trivially outweighed. We are *already* effectively vendoring — the 48 DLLs are
committed to `bin/` today; §1.2(A) just moves ~17 of them to a purpose-named `Library/` and lets the
other ~20 become real package references.

Note also that vendoring **eliminates an existing manual step**: the "copy OMV DLLs from
`D:\legion-grid-webrtc\bin\`" rule disappears — those four assemblies live in `Library/` and a clone
gets them for free.

### 1.4 `nuget.config`

A single tracked `nuget.config` at the repo root, with `<clear/>` so a developer's machine-level feeds
can't interfere:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

**No `<packageSourceCredentials>`. Ever.** Contrast with Tranquillity's, which has both a `github`
source and a credentials block.

> Considered and rejected: a local directory feed (e.g. `/d/local-nuget`, which already holds
> `OpenMetaverse.1.2.13.nupkg`). It is auth-free and would work, but it is a **machine-local absolute
> path** — a fresh clone on another machine fails restore. Vendoring into `Library/` is strictly better
> for the stated principle because the dependency travels *inside the repo*. (If we ever want the
> package form, we can revisit by committing `.nupkg` files into a repo-relative folder — but plain
> `HintPath` DLLs are simpler and need no feed machinery at all.)

---

## 2. Scope boundaries

### 2.1 IN scope

1. Delete `prebuild.xml`, `runprebuild.sh`, `runprebuild.bat`, and the prebuild tool.
2. Hand-author one **SDK-style `.csproj` per project**, all **tracked in git**.
3. One **tracked solution file** (`Legion.sln`).
4. **`Directory.Build.props`** at root for shared settings (`net8.0`, `LangVersion`, `RepoRoot`, common
   metadata) — modelled on Tranquillity's, which is only ~12 lines.
5. The **dependency model** of §1, including creating `Library/` and the `nuget.config`.
6. Un-gitignore `*.csproj` / `*.sln`; delete the two `!`-force-track exceptions that exist only to work
   around prebuild.
7. Add the **3 Jolt projects** to the solution (they are already SDK-style — see §4).

### 2.2 OUT of scope — explicitly deferred

| Deferred | Why |
|---|---|
| **Mono.Addins removal** | It works, and SDK csproj supports it perfectly (`<PackageReference>` + `<EmbeddedResource>` for the manifests). Tranquillity replaced it with `IPluginRegistryProvider` / `McMaster.NETCore.Plugins` as a **separate** refactor. Legion's Mono.Addins footprint is **237 `.cs` files**; removing it is a 3–6 week project with real runtime-regression risk and **zero** build-system benefit. Keep it. |
| **ANTLR / `InWorldz.Phlox.Tools`** | Keep as a **legacy (non-SDK) csproj**, exactly as Tranquillity did — theirs is the single legacy holdout out of 99 projects, still `TargetFrameworkVersion v4.5` with `Antlr3.targets`. Proven to coexist in an SDK solution. |
| **`Source/` folder reorganisation** | See §2.3 — recommended **against**. |
| **Any behavioural / code change** | Not one. If a `.cs` file changes, it's out of scope. The migration must be provably behaviour-neutral. |
| **Central package management** (`Directory.Packages.props`) | Tranquillity doesn't use it either. Adds a concept for no benefit at this size. |

### 2.3 ★ Recommendation: go SDK-style **IN PLACE** — skip the `Source/` reorg

Tranquillity restructured to `Source/ Addons/ Library/ Tests/ tools/`. **Legion should not.** Recommend
keeping the existing `OpenSim/...` tree and only changing the *project files*.

Reasons, strongest first:

1. **★ In-flight branches.** `jolt-physics-m1` is **68 commits ahead** of `slua-tier2-tables`, and there
   are several other live branches (slua tiers, experience port work). A path reorg turns **every**
   in-flight branch into a rename-conflict nightmare — git's rename detection degrades badly when a move
   is combined with content edits. In-place keeps every pending merge trivial.
2. **Review integrity.** In-place, the diff is "N new csproj files + deletions" — readable, auditable.
   With a reorg it's a 1,838-file rename blob in which a real mistake is undetectable.
3. **History ergonomics.** `git log`/`blame` on moved files needs `--follow` and still degrades. Legion
   actively cross-ports with Tranquillity and upstream OpenSim; clean history matters.
4. **Blast radius.** Every doc, script, deploy tool, `.gitignore` rule, and saved note referencing
   `OpenSim/Region/...` would need updating.
5. **It buys nothing mechanically.** SDK-style works identically at any path. The reorg is cosmetic.

**Cost of skipping it:** Legion's layout stays less tidy than Tranquillity's, and a future reorg (if ever
wanted) is a separate, purely-mechanical commit — cheap to do later, expensive to bundle now.

This decision alone removes an estimated **25–30% of the effort and most of the review risk.**

### 2.4 A bug this migration fixes for free

Today `runprebuild` **regenerates `OpenSim.sln` without the Phlox projects** — Phlox is not in
`prebuild.xml`, which is why `.gitignore` force-tracks
`InWorldz.Phlox.csproj` and `Phlox.ScriptEngine.csproj` as a workaround. So the current build system has a
standing footgun: *running the official build-setup script silently drops the script engine from the
solution.* (This is why the guardrail "never run runprebuild" exists.) With a tracked solution, that class
of bug is gone.

---

## 3. The build-and-run experience after migration

### 3.1 The three-way comparison

| | Steps to a runnable build |
|---|---|
| **Legion today** | `git clone` → **copy OMV DLLs from `D:\legion-grid-webrtc\bin\`** → `runprebuild.sh` → `dotnet build` → *(and remember not to re-run runprebuild or you lose Phlox from the sln)* |
| **Tranquillity today** | `git clone` → **create a GitHub PAT** → **set `GITHUB_ACTOR`/`GITHUB_TOKEN`** → `dotnet build` → *(401 and a failed build if you skipped the token — Royale's exact experience)* |
| **Legion after this plan** | `git clone` → `dotnet build` → **done** |

That is the entire goal, and it is achievable because vendoring removes the only dep that isn't public.

Cross-platform, offline-capable, no secrets, no generator step, no ordering rules. A grid operator handed
the repo needs the .NET 8 SDK and nothing else.

### 3.2 Output layout and native DLLs — keep the shared `bin/`

**Recommendation: preserve the existing shared-output convention** (`OutputPath` → repo `bin/` for all
projects), which is what prebuild does today via `../../bin/`.

This is the *simplest* answer and it resolves the native-DLL question almost entirely:

- The natives (`lib64/BulletSim*.dll`, `lib64/ubode.dll`, `lib64/sqlite3.dll`,
  `lib64/openjpeg-dotnet-x86_64.dll`, `libzstd.dll`, and `joltc.dll`) are **already committed in `bin/`
  at the exact path the runtime expects.** With shared output they simply stay there and the build drops
  managed assemblies alongside them. **No copy configuration needed, and no manual copy step** — a build
  yields a runnable `bin/`.
- Config content (`OpenSim.ini*`, `config-include/**`) likewise already lives in `bin/` and is untouched.
- The run workflow is **identical to today**, so no muscle memory or deploy script changes.

Implement via `Directory.Build.props`:

```xml
<OutputPath>$(RepoRoot)bin\</OutputPath>
<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
```

> ⚠️ **Known friction, plan for it.** Multiple projects writing one output directory is not the SDK's
> happy path: expect duplicate-file warnings and occasional parallel-build file locks. Tranquillity hit
> exactly this — they use **`ErrorOnDuplicatePublishOutputFiles=false` in 8 places** and moved most
> projects to default per-project output. Mitigations, in order of preference:
> 1. Set `ErrorOnDuplicatePublishOutputFiles=false` centrally in `Directory.Build.props`.
> 2. If parallel-build locking bites, build with `-m:1` or accept per-project output for the handful of
>    test projects (they don't need to be in `bin/`).
>
> **Fallback if shared output proves too painful:** switch to default per-project output and add an
> explicit copy of natives/content into the app projects via
> `<None Include="$(RepoRoot)runtime/**" CopyToOutputDirectory="PreserveNewest" />`. This is more
> "correct" but adds config and changes the run workflow — hence it is the fallback, not the default.

### 3.3 The one manual step that remains (and is unchanged)

Deploying a built `bin/` to a *running region's* bin (e.g. `/d/jolt-boot-test/bin`) stays a manual,
deliberate step — that's a deploy, not a build, and the existing `deploy-fixes.ps1` pattern
(refuse-while-running + hash-verify) is the right tool. Nothing in this migration should automate a
deploy onto a live grid.

---

## 4. Conversion order — bottom-up tiers with a build gate at each

**Target: ~94 projects** = 86 from `prebuild.xml` + ~5 Phlox + 3 Jolt.
**21 of the 86 are test projects.**

Work proceeds on one branch off `slua-tier2-tables`. A new `Legion.sln` is created empty and projects are
added tier by tier. `prebuild.xml` / `runprebuild.*` are deleted **only at the very end** (Tier 7), so the
old generator remains as a working reference throughout.

| Tier | Projects | Gate |
|---|---|---|
| **0. Scaffolding** | `Directory.Build.props`, `nuget.config`, `Library/` + vendored DLLs, empty `Legion.sln`. Verify every §1.2(B) package on nuget.org; demote failures to vendored. | Restore succeeds on an empty sln |
| **1. Leaf libs** | `SmartThreadPool`, `OpenSim.Region.PhysicsModules.ConvexDecompositionDotNet`, `OpenSim.Tools.Configger` | `dotnet build` per project |
| **2. Framework / Data / Services** | `OpenSim.Framework` + `.Console/.Monitoring/.Serialization/.Servers[.HttpServer]`, `OpenSim.Data` + MySQL/PGSQL/SQLite/Null, `OpenSim.Services.*` (~20), `OpenSim.Server.*`, `OpenSim.Capabilities*` | Tier builds clean |
| **3. Region core + modules** ⚠️ | `OpenSim.Region.Framework`, `.ClientStack.*`, `.CoreModules`, `.OptionalModules`, `.PhysicsModule*` (Bullet/ubOde/Meshing/POS/Basic/SharedBase), `.ScriptEngine.*` (YEngine) | **Riskiest tier — see below** |
| **4. Addons / Phlox** | `OpenSim.Addons.Groups`, `.OfflineIM`, `ApplicationPlugins.*`, `InWorldz.Phlox`, `Phlox.ScriptEngine`, `CompilerRunner`, `CompilerTests`; **`InWorldz.Phlox.Tools` stays legacy csproj** | ANTLR codegen still runs |
| **5. Jolt** | `OpenSim.Region.PhysicsModule.LegionJolt`, `Legion.Physics`, `Legion.Vehicles` — **already SDK-style**, so this is 3 `<ProjectReference>` additions to the sln | Builds as part of the sln, not out-of-band |
| **6. Apps + tests** | `OpenSim`, `Robust`, `pCampBot`, `OpenSim.ConsoleClient`, and the **21 test projects** | Full `dotnet build`; **`dotnet test`**; **★ boot-verify (§5)** |
| **7. Retire prebuild** | Delete `prebuild.xml`, `runprebuild.sh/.bat`, `bin/prebuild.dll`; un-gitignore csproj/sln; remove the two force-track exceptions | Clean clone → `dotnet build` → boot |

**Tier 3 is the riskiest** because: it holds the largest and most tangled projects; it is where nearly all
**21 embedded `.addin.xml` manifests** live (silent runtime failure if missed — §5.3); it contains the
physics modules with native-DLL and `.dll.config` dependencies; and it has the deepest inter-project
reference graph, so a dependency-mapping error here cascades.

**Jolt lands at Tier 5 — during, not before.** Doing Jolt first would mean wiring it into `prebuild.xml`
(work that is thrown away days later). Because the three Jolt projects are already SDK-style `net8.0`,
the migration *is* the integration: they stop being an out-of-band manual build and become ordinary
solution members. This also finally resolves the "LegionJolt is in no sln and no prebuild" gap.

---

## 5. Risks — what breaks, how we'd notice, mitigation

### 5.1 Dependency-mapping errors — **LOW risk**
*Breaks as:* unresolved-type / missing-assembly **compile errors**.
*Noticed:* immediately, by the per-tier build gate. Cannot reach production.
*Mitigation:* convert bottom-up; keep `prebuild.xml` readable as the reference for what each project
referenced. This is the highest-*volume* work but the lowest-*risk*, because the compiler checks it.

### 5.2 Missing content / natives at runtime — **HIGH risk** ⚠️
*Breaks as:* `DllNotFoundException` on joltc/BulletSim/ubode/sqlite3, or a region that starts with no
config. **A clean `dotnet build` will not catch this.**
*Noticed:* only by actually running the simulator.
*Mitigation:* the shared-`bin/` decision (§3.2) largely pre-empts it since natives already sit in `bin/`.
**Mandatory gate: a boot-verify** — headless boot per the established repro
(`dotnet OpenSim.dll -console=basic -background=true`), confirm terrain cooks, a region reaches
`INITIALIZATION COMPLETE`, and physics steps. Build-green is *not* the definition of done for this
migration.

### 5.3 Embedded addin manifests — **HIGH risk** ⚠️
*Breaks as:* a region module is **silently not discovered**. No error, no crash — a feature is just
missing. The worst failure mode in this plan.
*Noticed:* only by exercising the feature, or by diffing the loaded-module list.
*Mitigation:* `prebuild.xml` has **21** `EmbeddedResource`/addin entries — extract that list up front and
treat it as a **checklist**, verifying each `<EmbeddedResource>` appears in the corresponding SDK csproj.
At boot, capture the `[PLUGINS]: Plugin Loaded:` / `[REGIONMODULES]:` lines and **diff them against a
pre-migration boot log**. That diff is the objective proof.

### 5.4 ANTLR / Phlox codegen — **MEDIUM risk**
*Breaks as:* stale or missing generated parser; compile errors in Phlox, or subtly wrong LSL parsing.
*Noticed:* build failure (good case) or script-behaviour regression (bad case).
*Mitigation:* keep `InWorldz.Phlox.Tools` as a **legacy csproj** (Tranquillity's proven approach — don't
innovate here). Verify grammar regeneration runs, then confirm with an actual script test (a known LSL
script compiling and running in-world).

### 5.5 Shared-output friction — **MEDIUM risk, cosmetic**
*Breaks as:* duplicate-output warnings, intermittent file locks in parallel builds.
*Noticed:* build output, immediately.
*Mitigation:* §3.2 (central `ErrorOnDuplicatePublishOutputFiles=false`; `-m:1`; per-project output for
tests). Documented fallback exists.

### 5.6 Behavioural drift — **LOW risk but high impact**
*Breaks as:* something subtly different post-migration.
*Mitigation:* **zero `.cs` changes** is a hard scope rule (§2.2). Any required code change stops the
migration for an explicit decision rather than being folded in silently.

---

## 6. Honest size estimate

**Revised: ~1.5–3 weeks** of focused work for one developer, versus the earlier **2–4 weeks** estimate
that assumed the full `Source/` reorg.

The reduction comes almost entirely from §2.3 (in-place, no reorg) — that removes the 1,838-file move, the
path-rewrite sweep across docs/scripts, and the branch-conflict remediation for `jolt-physics-m1` and the
other in-flight branches.

| Phase | Estimate |
|---|---|
| Tier 0 scaffolding + package verification pass | 1–2 days |
| Tiers 1–2 (leaf, Framework/Data/Services ~35 projects) | 3–4 days |
| Tier 3 (Region/modules — the hard one) | 3–5 days |
| Tiers 4–5 (Addons/Phlox/ANTLR + Jolt) | 2–3 days |
| Tier 6–7 (apps, 21 test projects, boot-verify, prebuild removal) | 2–4 days |

**What makes it 3 weeks instead of 1.5:** the mechanical csproj conversion is genuinely fast once the
pattern is set (SDK globbing means no `<Compile Include>` lists — most files will be ~20–30 lines like
Tranquillity's `OpenSim.Framework.csproj`). The time actually disappears in the **last 15%**: runtime
issues that the compiler cannot catch (§5.2, §5.3), each requiring a boot cycle to find and confirm.
Budget generously there and do not treat "it builds" as "it's done".

**What would push it past 3 weeks:** discovering that several §1.2(B) packages aren't on nuget.org at
usable versions (more vendoring — annoying but not hard); an ANTLR toolchain that doesn't survive the
solution change; or scope creep into Mono.Addins. The first two are absorbable; **the third is the real
schedule risk and the reason §2.2 draws that line so firmly.**

---

## 7. Recommended strategy — summary

1. **Branch** off `slua-tier2-tables`. Do not touch `prebuild.xml` until Tier 7.
2. **In place** — no `Source/` reorg. Smallest diff that removes prebuild.
3. **Zero-auth dependencies** — public nuget.org or vendored in `Library/`; never a private feed;
   `nuget.config` with `<clear/>` and nuget.org only.
4. **Keep** Mono.Addins and the legacy ANTLR csproj. Defer both indefinitely.
5. **Keep** the shared `bin/` output so natives and config need no new plumbing and the run workflow is
   unchanged.
6. **Bottom-up tiers** with a build gate each, and a **boot-verify + module-list diff** as the real
   definition of done.
7. **Jolt at Tier 5** — the migration converts it from an out-of-band manual build into a normal
   solution member.

**The one-line success criterion:** on a clean machine with only the .NET 8 SDK,
`git clone && dotnet build` produces a runnable `bin/`, and the region boots with the same module list as
before. Nothing else counts.
