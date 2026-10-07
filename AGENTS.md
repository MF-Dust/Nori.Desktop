# Repository Guidelines

Welcome to the **Nori Desktop Pet** repository. This document serves as the comprehensive development and contribution guide for both human developers and autonomous coding agents. Follow all conventions and architectural boundaries documented here to ensure code quality and consistency.

---

## 1. Project Overview & Architecture

Nori Desktop Pet is an AI desktop companion built with a **.NET 10 + Avalonia 12** native host. Playback and capture go straight to the platform audio device.

### Key Architectural Pillars
- **Root Entry & Slot Deployment (`Nori.AppLauncher`)**: The stable root binary `Nori` selects and launches an immutable deployment slot (`app-<version>-<revision>`) validated by `deployment.json`. The launcher does not own locks or update slots; all runtime state resides in `<PackageRoot>/data`.
- **Window Architecture (`Nori.Desktop/Windows`)**:
  - User windows are native Avalonia: `first-run`, `init`, `main`, and `pet`, plus on-demand settings, memory, models, chat, and quick chat. Windows are borderless (`WindowDecorations.None`) and transparent. Closing a window hides it; `main` is persistent for the app lifetime.
  - Native Desk Pet (`pet`): An Avalonia `PetWindow` running native OpenGL ES 2.0 via `Nori.Desktop/Live2D/Gl` and native host/resource owners, `Nori.Live2D` resource definitions, model assembly, motion, physics, pose, breath, layout, pointer smoothing and mask planning layers, and PurismCore (MIT, Cubism v6 ABI). It renders directly to the desktop.
  - Native Settings: `SettingsWindow` hosts Avalonia settings pages; inspect this native path for settings UI work.
  - Audio: `NativeAudioFactory` opens WASAPI on Windows, AudioQueue on macOS, and ALSA device `default` on Linux. Legacy `audio_backend=webview` does not switch implementations.
- **Native Live2D Desk Pet**:
  - `PetWindow` + `PetGlControl`: Native C# pipeline (`AutoBlink`, `EyeFocus`, `BeatSync`, `LipSync`, `ExpressionStore`, `ExpressionBehavior`). Alpha mask sampling (~10Hz) computes a single bounding rectangle for hit-testing (`WM_NCHITTEST` on Windows, `XShape` on Linux X11, `setIgnoresMouseEvents:` on macOS, degraded whole-window hit on Wayland). Model preview is the native `ModelPreviewControl`, not a web view.
- **IPC Bridge (`BridgeCommandRouter` & `BridgeCommands`)**:
  - Native windows call `BridgeCommands` through `SettingsService`, `NativeChatService`, `ModelService`, and `MemoryService` frozen allowlists.
  - Host dispatches commands through `BridgeCommands.cs`.
  - Commands throw exceptions with human-readable Chinese messages on failure.
- **Storage Layout (`<PackageRoot>/data`)**:
  - Governed strictly by immutable `AppStoragePaths`. Subdirectories: `core/database` (`nori.db`), `core/security` (`secret.key`), `knowledge/documents` (`Memory.md`), `resources/installed`, `plugins`, and `diagnostics/logs`.
  - Zero fallback to OS system directories. If `<PackageRoot>/data` is unwritable, startup fails explicitly.
  - Runtime data resides only in `<PackageRoot>/data`; the package does not read legacy application-data directories.
- **Plugin System (`Nori.PluginRuntime` & `Nori.PluginSDK`)**:
  - Plugins are in-process .NET 10 extensions loaded into collectible `AssemblyLoadContext` instances for isolation.
  - Third-party plugins reference the ref-only NuGet package `Nori.PluginSDK` (exposing `INoriPlugin`, `IPluginContext`, and contribution contracts). The desktop host exposes `ui.avalonia` for disposable native page contributions (`IPluginPageContribution` / `IPluginPage`), opened from plugin management in host-owned windows. Page creation and disposal run on the UI thread, and revocation must release pages before ALC unload. `ui.webview` remains unsupported.
  - Packaged as `.noripack` archives and managed safely without host process restarts.
- **Security & Secret Storage**:
  - Sensitive config values (`*_api_key`, `*_secret`, `*_token`, `*_password`) are stored as `nsec2:<base64(nonce | ciphertext | tag)>` encrypted with AES-256-GCM.
  - Master keys are stored via platform keystores (Windows DPAPI, macOS Keychain, Linux libsecret, or 0600 file fallback).
  - Legacy formats (`enc:dpapi:`, `nsec1:`, plaintext) still read for migration; `ConfigStore.GetSecretIssue` / `SecretIssueCategory` surfaces unreadable items (`LegacyUnsupported`, `KeyStoreUnavailable`, `CorruptCiphertext`) so the UI can ask the user to re-enter that key without wiping other config.
- **Safe Mode (`--safe-mode`)**:
  - Manual CLI flag that skips MCP auto-connection, Proactive/Reflection schedulers, background memory maintenance, AI pet interactions, and Live2D model auto-load, while disabling external network provider calls.

### Stabilization Contracts

- **Product version**: Controlled by the build environment; defaults to `Dev` when not injected. Release uses a manual codename; numeric versions must not be reused across release tags. Inject stable version via `NORI_PRODUCT_VERSION` and informational version via `NORI_PRODUCT_INFORMATIONAL_VERSION` (with `NORI_COMMIT_SHA` short hash). CLR/File/NuGet versions stay numeric; `ProductVersion.Current` keeps the full `v<version>-<codename>+<shortsha>` informational string and feeds snapshot, smoke readiness, diagnostics, crash reports, and MCP `clientInfo`. Do not add a `0.1.0` fallback.
- **`--safe-mode`**: Manual CLI only; no auto-recovery. Keeps UI, logs, diagnostics, and local manual repair; skips MCP auto-connect, Proactive/Reflection, knowledge/memory background maintenance, AI pet interaction, and Live2D auto model load; bridge entry also rejects interactive networking and external Provider/MCP/voice operations.
- **`readiness.json` schema v2**: Must include `product_version`, `database_schema_version`, `config_schema_version`, and `safe_mode`. Windows CI covers first-run, initialized, and initialized safe-mode profiles.
- **`export_diagnostics`**: Size-limited, redacted whitelist ZIP only; must not contain database, chat/memory/prompts, tool args/results, request bodies, recordings, resources, credentials, or real user paths. Provider connection tests send fixed probes and do not persist config or content.
- **Pre-migration backup**: Uses `VACUUM INTO`, 64 MiB per-file cap, keep at most 3 copies; new migrations must preserve this protection.

### Configuration & Resources

- **`nori.db`**: SQLite key/value under `<PackageRoot>/data/core/database/`; values stored as TEXT. `ConfigValue.FromStorage` re-infers type on read (`"1"`/`"true"` → Boolean, digit strings → Integer, `{…}`/`[…]` → Json, else String). `"1.25"` stays String. Native readers must tolerate inferred types; see `Nori.Core.Tests` / `ConfigValueTests`.
- **Live2D display keys**: Per-model `<base>_<modelId>` (e.g. `l2d_scale_arg-nori`) with fallback to legacy global keys — see `PetRuntime` and `BridgeCommands.model_get_meta`.
- **Local models only**: Under `<PackageRoot>/data/resources/installed/live2d/`; native `ModelsWindow` (`ModelsWindow.Library.cs`) imports via `ModelService` allowlist command `model_import_local` (local ZIP or folder); `ZipExtractor` rejects absolute paths, UNC, drive letters, `..`, control chars, and symlink entries, re-canonicalizes parents, and strips a single common top-level directory. Do not loosen it.

---

## 2. Project Structure & Module Organization

```text
Nori-Desktop-Pet/
├── .github/workflows/          # CI (build.yml) and release (release.yml)
├── docs/                       # Chinese specs — see docs/ for full list
├── app/desktop/                # Primary application workspace
│   ├── Nori.slnx
│   ├── Live2D/                 # PurismCore native runtime (MIT)
│   ├── Nori.Live2D/            # Model/resource definitions, Purism ABI and animation (no old SDK dependency)
│   ├── Nori.AppLauncher/       # Dependency-free root launcher
│   ├── Nori.AppLauncher.Tests/
│   ├── Nori.Core/              # Avalonia-independent logic
│   ├── Nori.Core.Tests/
│   ├── Nori.Desktop/           # Avalonia host (windows, tray, pet, bridge, audio; Live2D/Gl owns GL)
│   ├── Nori.Desktop.Tests/
│   ├── Nori.PluginRuntime/
│   ├── Nori.PluginRuntime.Tests/
│   ├── src/                    # Native theme tokens only
│   │   ├── assets/style/       # tokens.ts — native theme token source
│   │   └── services/theme/     # Contrast helpers
│   ├── tests/                  # Vitest: native tokens and contrast
│   ├── scripts/                # CI, coverage, todo-check, packaging, sync-design-tokens.mjs
│   └── package.json
```

---

## 3. Build, Test, and Development Commands

Run application build, test, and development commands from `app/desktop/`. Use **pnpm** for frontend package management and scripts; use `dotnet` for .NET commands. Follow the global RTK command wrapper rule.

### Prerequisites
- .NET 10 SDK; published apps need the .NET Runtime 10
- Node.js 24+
- pnpm 11+
- Linux desktop libraries: GTK 3 and ALSA (`libasound2t64` on Ubuntu 24.04+, otherwise `libasound2`)

### Common Workflows

```bash
# Navigate to the workspace
cd app/desktop

# Install theme-check dependencies
pnpm install

# Run the desktop app
dotnet run --project Nori.Desktop
```

### Local Verification

本地按改动范围运行必要检查：纯文档修改检查内容与格式；单端修改验证该端；跨端契约或共享构建变更验证两端。全量门禁保留在 CI。需要覆盖率时，以覆盖率测试替代同范围普通测试。检查通过后，仅因新增修改、失败或未解决风险重跑。

### Verification Gates (CI command reference)

```bash
# 1. Source code annotation gate (fails on forbidden TODO/FIXME comments in first-party code)
pnpm check:todo

# 2. Frontend lint & static analysis
pnpm lint

# 3. TypeScript theme token check
pnpm build

# 4. Frontend unit tests & coverage
pnpm test
pnpm coverage

# 5. Backend compilation (all warnings treated as errors)
dotnet build Nori.slnx --configuration Release

# 6. Backend unit tests (strictly serialized MSBuild concurrency)
dotnet test Nori.slnx --configuration Release --no-build --no-restore -m:1

# 7. Backend coverage collection
pnpm coverage:dotnet --no-build --no-restore
```

### Packaging & Release

```bash
# Framework-dependent build for win-x64
publish.bat

# Framework-dependent build for linux-x64 / linux-arm64 / osx-arm64 / osx-x64
./publish.sh <rid>
```

---

## 4. Coding Style & Naming Conventions

### General Formatting
- **Indentation**: **Tabs**, not spaces, across `.ts` and `.cs`.
- **Line Endings**: LF.
- **Quotes**: Double quotes (`"`) across TypeScript and C#.
- **Language**: Comments, XML doc comments, and logs use **Chinese**. Host error messages remain readable Chinese.

### TypeScript theme tokens
- `src/` only holds native theme tokens and contrast helpers. Do not add a user-facing page, audio host, or plugin WebView here.
- Native Avalonia colors, spacing, radius, and font measures come from `src/assets/style/tokens.ts`. Run `pnpm theme:check` after changing tokens and keep the native contrast tests passing.
- Do not reintroduce Vue, Vite app hosting, web UI, i18n, UnoCSS, Less, or Web Sentry dependencies.
- Use `UPPER_SNAKE` for local constants, `camelCase` for local values, and `PascalCase` for exported functions, classes, and types.

### Backend (.NET 10, C#)
- **Casing**: Follow standard .NET conventions: `PascalCase` for types, methods, properties, public fields, and constants; `_camelCase` for private fields; `camelCase` for parameters and local variables. Do not use `UPPER_SNAKE` for C# constants.
- **Bridge Commands**:
  - Commands use `snake_case` with verb-first naming (e.g., `settings_update_workspace`, `model_select`, `memory_list_page`).
  - Every command must be registered in `BridgeCommands.InvokeAsync`'s switch.
  - Implement host commands in `BridgeCommands.cs`. Native windows use their authorized services directly.
  - Every command requires an XML `///` doc comment displaying an invocation example:
    ```csharp
    /// <summary>
    /// 更新工作区设置。
    /// 前端调用：invoke("settings_update_workspace", { root: "..." })
    /// </summary>
    ```
  - Commands report failure by throwing exceptions with user-facing Chinese text. Never swallow errors or return silent nulls.
- **Threading Rules**:
  - Never execute blocking I/O (HTTP, SQLite, file extraction) on the Avalonia UI thread.
  - Window operations must run through `Dispatcher.UIThread`.
- **Platform Abstractions**:
  - Platform-specific code must reside behind `IPlatformServices` (`Nori.Core/Platform/`).
  - Code must be capability-driven: inspect `IPlatformServices.Capabilities` (`supportsGlobalCursor`, `supportsHitThrough`, `supportsTray`) before calling OS features instead of relying on `PlatformNotSupportedException`.

---

## 5. Testing Guidelines

### Test Frameworks & Projects
- **Theme tokens**: Vitest (`app/desktop/vitest.config.ts`), running native-token and contrast tests under `app/desktop/tests/`.
- **Backend**: xUnit, partitioned across test projects:
  - `Nori.Core.Tests`: Logic, path safety, zip extraction, configuration parsing/inference, encryption, memory/AI evaluation.
  - `Nori.Desktop.Tests`: Avalonia window metrics, hit mask calculation, bridge routing, diagnostics.
  - `Nori.PluginRuntime.Tests`: Plugin ALC loading, isolation, manifest validation, package safety.
  - `Nori.AppLauncher.Tests`: Deployment slot selection and manifest parsing.

### Critical Automated Checks
- **Native Design Token Synchronization (`native-tokens-sync.test.ts`)**: Verifies generated Avalonia colors, brushes, and measures match `tokens.ts`.
- **Native Contrast Ratios (`contrast.test.ts`)**: Validates text/background contrast for the native palette (≥4.5:1 for body text).
- **Path Security (`ResourcePathSafetyTests.cs`)**: Prevents path traversal, UNC, symlink breakouts, and directory escaping.
- **Config Inference (`ConfigValueTests.cs`)**: Pins type inference for SQLite key/value reads.

### Gated Live2D / GL Tests
Real-model and real-GL tests in `Nori.Desktop.Tests` are skipped unless enabled explicitly. CI sets none of these variables and the models are not in git, so these tests never run in CI; run them locally after changing `Nori.Live2D` or `Nori.Desktop/Live2D`.
- `[Live2DAssetsFact]`: `NORI_TEST_LIVE2D_ASSETS=1`. Models load from `NORI_LIVE2D_FIXTURES/<modelId>/` when set, otherwise from the nearest `data/resources/installed/live2d/<modelId>/` above the test output directory (`arg-nori`, `nori`).
- `[NativeGlAssetsTheory]`: Windows x64 with `NORI_TEST_NATIVE_GL=1` and `NORI_TEST_LIVE2D_ASSETS=1`.
- `[TextureQuadGlTheory]`: Windows x64 with `NORI_TEST_NATIVE_GL=1`.
- `NORI_TEST_NATIVE_GL=1` also switches the shared headless fixture (`BridgeCommandsTests.BuildAvaloniaApp`) to real Skia drawing.

```bash
NORI_TEST_LIVE2D_ASSETS=1 NORI_TEST_NATIVE_GL=1 dotnet test Nori.Desktop.Tests/Nori.Desktop.Tests.csproj --configuration Release -m:1
```

---

## 6. Commit & Pull Request Guidelines

### Commit Format
Follow the Conventional Commits specification:
```text
<type>: <short description in Chinese or English>
```
Common types:
- `feat`: New feature or user-facing capability
- `fix`: Bug fix
- `test`: Adding or updating test suites
- `refactor`: Code reorganization without functional changes
- `chore`: Build, packaging, or dependency updates
- `docs`: Documentation updates

*Examples*:
- `fix: preserve non-emoji supplementary characters`
- `feat: 三平台原生播放与录音`
- `fix: 修复原生 Live2D 模型与行为`

### Pull Request Rules
1. **Scope**: Keep pull requests atomic and focused on a single task. Avoid mixing unrelated refactors.
2. **Local Gate Verification**: Follow Local Verification above and report the checks actually run. CI owns the full gate; do not run unrelated local suites solely to open a PR.
3. **Artifact Cleanup**: Never commit debug probes (`__probe*`), temporary scripts, logs, or scratch files. Remove debug `console.log` statements (retain `console.error` for legitimate error paths).
4. **Visual Changes**: Verify the affected interface through screenshots or interaction. Layout, scaling, and responsive changes require checks at 720×480 and 1080p; other visual changes need evidence for the affected view.

---

## 7. Additional Guardrails

The coding rules above are the source for formatting and bridge registration.

- **Windows manifest**: Preserve `<supportedOS>` entries in `Nori.Desktop/app.manifest`; the OpenGL control needs the supported-OS declaration.
- **Treat Warnings as Errors**: Respect the C# projects' `TreatWarningsAsErrors` setting.
- **Avalonia 12 window chrome**: `SystemDecorations` was renamed to `WindowDecorations`; the old enum survives only as `[Obsolete]`.
- **`LibraryImport` vs `DllImport`**: `LibraryImport` requires `AllowUnsafeBlocks`; `Nori.Core` uses classic `DllImport` for P/Invokes project-wide.
- **`objc_msgSend`**: Declare a separate `DllImport` per return type (`nint` / `void` / `CGPoint` / `CGRect`); arm64 reads the wrong registers otherwise. All AppKit calls must be on the UI thread.
- **CA1416 and type patterns**: `PlatformServices.Current is MacPlatformServices mac` still needs an `OperatingSystem.IsMacOS() &&` guard or the analyzer errors (warnings are errors here).
- **`Directory.Build.props`**: Owns `TargetFramework` / `Nullable` / `LangVersion` only; `TreatWarningsAsErrors` stays per-project.
- **`tsconfig.json` target**: ES2022.
- **Playback state**: One native playback instance and one recorder live for the process lifetime inside `AppRuntime`.
- **Microphone consent**: macOS needs `NSMicrophoneUsageDescription` in the bundled `Info.plist` (from `publish.sh`) or AudioQueue capture is rejected by the system. Linux playback and capture need `libasound.so.2`.
- **Completion**: Complete the requested scope and its relevant verification. Report unrelated findings without automatically expanding the change. Ask only when necessary information or authorization is missing; reuse authorization already provided.
