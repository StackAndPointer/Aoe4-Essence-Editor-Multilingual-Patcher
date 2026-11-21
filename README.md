<div align="center">

# Age of Empires IV — Essence Editor Multilingual Patcher

**A version-independent localization patcher for the Age of Empires IV Essence Editor (mod editor).**
Ship a translation once and keep it working across game updates.

[![CI](https://github.com/StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher/actions/workflows/ci.yml/badge.svg)](https://github.com/StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher/actions/workflows/ci.yml)
[![Release](https://github.com/StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher/actions/workflows/release.yml/badge.svg)](https://github.com/StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher/actions/workflows/release.yml)

</div>

---

## What it does

The Essence Editor stores its UI strings inside seven .NET assemblies, in the
`<Assembly>.Resources.Text.resources` embedded resources. Instead of maintaining a
hand-patched binary per game build, this tool ships a **version-independent catalog** of
translations and rewrites the resource strings in place with
[dnlib](https://github.com/0xd4d/dnlib). Anything the catalog does not cover falls back to
the original English text.

The result: a game update no longer invalidates the patch. Only genuinely new or reworded
strings need attention.

## Install

Grab the latest release:

| Asset | Use |
|---|---|
| `EssenceL10n-win-Setup.exe` | Installer (Velopack); ships desktop & Start-menu shortcuts and self-updates from GitHub Releases |
| `EssenceL10n-<version>-win-x64.zip` | Portable. Extract anywhere and run |
| `EssenceL10n-win-Portable.zip` | Velopack portable build |

No .NET runtime or SDK is required — everything is published self-contained.

## Use

1. Launch **`EssenceL10n.exe`** (light-themed GUI).
2. Pick the game folder — the one containing `EssenceEditor.exe`. Steam libraries are
   auto-detected; you can also browse manually. The tool does **not** need to live inside
   the game folder.
3. Choose a patch language → **Scan** to inspect coverage → **Apply patch** to write.

Originals are backed up to `<game folder>\EssenceL10n.backup\<timestamp>\` before writing
(toggleable).

### Command line

The GUI is only a front end. Every write goes through `EssenceL10n.Cli.exe`, which can be
scripted or run in CI:

```powershell
EssenceL10n.Cli.exe report "<game folder>" --catalog catalogs\zh-Hans\zh-Hans.jsonl --json
EssenceL10n.Cli.exe apply  "<game folder>" --catalog catalogs\zh-Hans\zh-Hans.jsonl
EssenceL10n.Cli.exe verify "<game folder>" --catalog catalogs\zh-Hans\zh-Hans.jsonl --json
EssenceL10n.Cli.exe apply  "<game folder>" --catalog catalogs\ja\ja.jsonl --dry-run --json
```

Other commands: `scan`, `bootstrap`, `diff`, `todo`, `merge`, `prune`, `export`,
`import`, `compare`, `translate`. Run `EssenceL10n.Cli.exe help` for the full list.

## Languages

| Language | Catalog | Units | Translated | Kept as-is | Technical | Coverage |
|---|---|---:|---:|---:|---:|---:|
| Simplified Chinese | `catalogs/zh-Hans` | 3574 | 3540 | 0 | 34 | **99.0%** |
| Traditional Chinese | `catalogs/zh-Hant` | 3574 | 3540 | 0 | 34 | **99.0%** |
| Japanese | `catalogs/ja` | 3574 | 3503 | 37 | 34 | **98.0%** |
| Russian | `catalogs/ru` | 3574 | 3509 | 31 | 34 | **98.2%** |
| Spanish | `catalogs/es` | 3574 | 3483 | 57 | 34 | **97.5%** |
| German | `catalogs/de` | 3574 | 3369 | 205 | 34 | **94.3%** |
| English (source) | `catalogs/en` | 3574 | — | — | — | baseline |

All six translations are **human-made**. "Kept as-is" counts entries whose translation is
identical to the English source on purpose (`OK`, `FX`, `FPS`, `ID:`, channel letters such
as `R:`/`G:`/`B:`). "Technical" counts deliberately untranslated identifiers
(`State = Ignored`). Untranslated entries across every catalog: **0**.

Exports for translation platforms (Weblate, Crowdin, …) are one command away:

```powershell
EssenceL10n.Cli.exe export --catalog catalogs\ja\ja.jsonl --format po   --out ja.po
EssenceL10n.Cli.exe export --catalog catalogs\ja\ja.jsonl --format xliff --out ja.xlf
EssenceL10n.Cli.exe import --catalog catalogs\ja\ja.jsonl --from ja.po
```

## How the fallback chain works

```
Exact → KeyInAssembly → (KeyGlobal, off by default) → SourceHash → (Fuzzy, off by default) → keep original
```

Renamed keys, reworded strings and reshuffled resource tables do not break the patch. That
is what makes it a *hot* patch rather than a one-shot binary replacement.

## Architecture

```
src/EssenceL10n.Core      scanning, catalogs, fallback chain, patch engine, PO/XLIFF
src/EssenceL10n.Cli       command line; emits --json for the GUI
src/EssenceL10n.App       Avalonia GUI; spawns the CLI, never touches dnlib itself
tests/                    xunit regression tests
catalogs/<lang>/          one JSONL catalog per language
artifacts/translate/      local translation work orders (gitignored; catalogs/ is the source of truth)
build/pack.ps1            self-contained packaging + Velopack
build/check-catalogs.ps1  catalog integrity check (shared by CI and local runs)
```

**Front end / back end split.** The GUI never references dnlib. It starts
`EssenceL10n.Cli.exe` as a child process and parses its `--json` output, so there is exactly
one code path that touches game binaries, and a GUI crash cannot leave a half-written
assembly behind.

The GUI has its own i18n: `src/EssenceL10n.App/i18n/*.json` (en, zh-Hans, zh-Hant, ja, de,
ru, es), compiled as embedded resources and selected from `CultureInfo.CurrentUICulture`,
with `--lang <culture>` available as an override. Unlike the patch language, this only
affects the tool's own interface.

## Technical decisions

| Concern | Choice | Why |
|---|---|---|
| Language | **C# / .NET 8** (SDK 10 builds it) | The targets are .NET assemblies; resource I/O, PE rewriting and i18n all live in one runtime |
| Core library | **dnlib 4.5.0** | Replacing an `EmbeddedResource` in place leaves every other resource and metadata untouched |
| Resource I/O | **`System.Resources`** (built in) | Supersedes `ResGen.exe`, which is only a CLI shell over these APIs |
| GUI | **Avalonia 11.3.6**, Fluent **Light** | Cross-platform, single csproj, low ceremony |
| Distribution | **Velopack** + GitHub Releases | Double-click install, incremental self-update |
| Tests | **xunit** | Guards the fallback chain |

There are **no external tool dependencies** — no `ResGen.exe`, no registry access.

## Verified facts about the target

- Text lives in exactly **7 assemblies**, all at `<Assembly>.Resources.Text.resources`, **3574 entries** total.
- None of the assemblies are strong-named (`PublicKeyToken=null`), so no re-signing is needed.
- The **318 BAML resources** inside `*.g.resources` showed **zero text differences** — no BAML rewriter required.
- The assembly name of `EssenceEditor.exe` is **`EssenceEditor`**, not `Essence.Editor`. Indexing by file name breaks the lookup.

## Build from source

```powershell
dotnet build EssenceL10n.slnx -c Release
dotnet test  tests/EssenceL10n.Core.Tests/EssenceL10n.Core.Tests.csproj

# self-contained zip only
pwsh -File build/pack.ps1 -Version 1.0.0

# zip + Velopack installer, wired to this repo's release feed
pwsh -File build/pack.ps1 -Version 1.0.0 -Velopack -GithubRepo StackAndPointer/Aoe4-Essence-Editor-Multilingual-Patcher
```

Output lands in `publish/`:

```
publish/app/                     GUI + CLI + catalogs + update.json — run from here
publish/EssenceL10n-<v>-win-x64.zip
publish/velopack/EssenceL10n-win-Setup.exe
publish/velopack/EssenceL10n-win-Portable.zip
publish/velopack/EssenceL10n-<v>-full.nupkg
```

## Releasing

CI runs build + tests + catalog integrity + GUI asset checks on every push. Tagging
`v1.2.3` triggers the release workflow, which packs, attaches the assets to the GitHub
Release and publishes the Velopack update feed:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

Clients read `update.json` (`{ "githubRepo": "…", "channel": "win" }`) to find the release feed.
How "Check updates" behaves depends on **how the app was deployed**:

| Deployment | Self-update |
|---|---|
| `EssenceL10n-win-Setup.exe` (Velopack installer) | Full. Downloads deltas and restarts in place |
| `EssenceL10n-win-Portable.zip` (Velopack portable) | Full. Updates in place |
| `EssenceL10n-<version>-win-x64.zip` or a raw `publish/app` folder | None. Queries the GitHub Releases API, shows the new version, opens the download page |

The third case is why `Velopack.UpdateManager` would otherwise throw
`Cannot perform this operation in an application which is not installed`: a plain folder has no
Velopack installation context (`sq.version`, `packages.win.json`, `Update.exe`). The updater
checks `IsInstalled` / `IsPortable` first and falls back to the GitHub API instead of failing.

## Catalog format

One JSON object per line:

```json
{"Assembly":"Essence.Editor.Core","Resource":"Essence.Editor.Core.Resources.Text.resources","Key":"DialogResult.OK.Content","Source":"OK","Value":"OK","State":2,"Hash":"…"}
```

`State`: `0` missing · `1` translated · `2` reviewed · `3` ignored (never translated).
The catalog is keyed by `Assembly + Resource + Key`, so it survives rebuilds that reshuffle
resource tables.

## License

MIT — see [LICENSE](LICENSE).

## Disclaimer

Fan-made tooling. Not affiliated with Relic Entertainment or World's Edge. Back up your game
files; the tool does that for you by default, but a backup you control is worth more than a
promise.
