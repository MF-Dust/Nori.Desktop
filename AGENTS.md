# Repository Guidelines

Welcome to the **Nori Desktop Pet** repository. This document serves as the comprehensive development and contribution guide for both human developers and autonomous coding agents. Follow all conventions and architectural boundaries documented here to ensure code quality and consistency.

---

## 1. Project Overview & Architecture

Nori Desktop Pet is an AI desktop companion built with a **.NET 10 + Avalonia 12** native host. The bundled page is only the compatibility audio host; plugin widgets may open their own WebView.

### Key Architectural Pillars
- **Root Entry & Slot Deployment (`Nori.AppLauncher`)**: The stable root binary `Nori` selects and launches an immutable deployment slot (`app-<version>-<revision>`) validated by `deployment.json`. The launcher does not own locks or update slots; all runtime state resides in `<PackageRoot>/data`.
- **Window Architecture (`Nori.Desktop/Windows`)**:
  - User windows are native Avalonia: `first-run`, `init`, `main`, and `pet`, plus on-demand settings, memory, models, chat, and quick chat. Windows are borderless (`WindowDecorations.None`) and transparent. Closing a window hides it; `main` is persistent for the app lifetime.
  - The only app WebView is the hidden `audio-host`, created when the platform audio backend is not native.
  - Native Desk Pet (`pet`): An Avalonia `PetWindow` running native OpenGL ES 2.0 via `Live2DCSharpSDK` and Cubism Core. It renders directly to the desktop (no webview airspace clipping or transparency limitations).
  - Native Settings: `SettingsWindow` hosts Avalonia settings pages; inspect this native path for settings UI work.
- **Native Live2D Desk Pet**:
  - `PetWindow` + `PetGlControl`: Native C# pipeline (`AutoBlink`, `EyeFocus`, `BeatSync`, `LipSync`, `ExpressionStore`, `ExpressionBehavior`). Alpha mask sampling (~10Hz) computes a single bounding rectangle for hit-testing (`WM_NCHITTEST` on Windows, `XShape` on Linux X11, `setIgnoresMouseEvents:` on macOS, degraded whole-window hit on Wayland). Model preview is the native `ModelPreviewControl`, not a web view.
- **IPC Bridge (`NoriBridge` & `BridgeCommands`)**:
  - Production bridge entry is `BridgeCommandRouter`: the hidden `audio-host` uses the 7-command map in `src/services/host/commands.ts`; native windows call `BridgeCommands` through `SettingsService`, `NativeChatService`, `ModelService`, and `MemoryService` frozen allowlists.
  - Structured audio errors use `src/services/runtime/logging.ts`.
  - Host dispatches commands through `BridgeCommands.cs`. Envelopes are double-encoded (JSON envelope serialized as string into `InvokeScript`) to eliminate escaping vulnerabilities.
  - Commands throw exceptions with human-readable Chinese messages on failure, which become frontend promise rejections.
- **Embedded Asset Server (`AssetServer.cs`)**:
  - Loopback Kestrel HTTP server with a per-process random hex path prefix and strict `Host` header verification.
  - Serves: `/{secret}/app/*` (audio-host bundle), `/{secret}/media/{token}` (one-shot TTS/recording exchange), and `/plugins` routes for plugin WebViews.
- **Storage Layout (`<PackageRoot>/data`)**:
  - Governed strictly by immutable `AppStoragePaths`. Subdirectories: `core/database` (`nori.db`), `core/security` (`secret.key`), `knowledge/documents` (`Memory.md`), `resources/installed`, `plugins`, `webview/cache/host`, and `diagnostics/logs`.
  - Zero fallback to OS system directories. If `<PackageRoot>/data` is unwritable, startup fails explicitly.
  - Runtime data resides only in `<PackageRoot>/data`; the package does not read legacy Tauri application-data directories.
- **Plugin System (`Nori.PluginRuntime` & `Nori.PluginSDK`)**:
  - Plugins are in-process .NET 10 extensions loaded into collectible `AssemblyLoadContext` instances for isolation.
  - Third-party plugins reference the ref-only NuGet package `Nori.PluginSDK` (exposing `INoriPlugin`, `IPluginContext`, `ui.webview` capability).
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
│   ├── Live2D/                 # Cubism Native Core
│   ├── Live2DCSharpSDK.*/      # Ported Live2D OpenGL engine
│   ├── Nori.AppLauncher/       # Dependency-free root launcher
│   ├── Nori.AppLauncher.Tests/
│   ├── Nori.Core/              # Avalonia-independent logic
│   ├── Nori.Core.Tests/
│   ├── Nori.Desktop/           # Avalonia host (windows, tray, pet, bridge, audio)
│   ├── Nori.Desktop.Tests/
│   ├── Nori.PluginRuntime/
│   ├── Nori.PluginRuntime.Tests/
│   ├── src/                    # Hidden audio-host page + typed bridge client
│   │   ├── assets/style/       # tokens.ts — native theme token source
│   │   ├── services/audio/
│   │   ├── services/host/      # Typed audio commands and host events
│   │   ├── services/runtime/   # Structured audio-host logging
│   │   └── bootstrap.ts
│   ├── tests/                  # Vitest: audio, bridge, native tokens, contrast
│   ├── scripts/                # CI, coverage, todo-check, packaging, sync-design-tokens.mjs
│   ├── package.json
│   └── vite.config.ts          # base: "./"; proxies /media and /plugins
```

---

## 3. Build, Test, and Development Commands

Run application build, test, and development commands from `app/desktop/`. Use **pnpm** for frontend package management and scripts; use `dotnet` for .NET commands. Follow the global RTK command wrapper rule.

### Prerequisites
- .NET 10 SDK (ASP.NET Core Runtime 10)
- Node.js 24+
- pnpm 11+
- Platform WebViews: WebView2 Evergreen (Windows), WebKitGTK 4.1 (Linux), WKWebView (macOS)

### Common Workflows

```bash
# Navigate to the workspace
cd app/desktop

# Install frontend dependencies
pnpm install

# Run frontend dev server (Vite on localhost:1420)
pnpm dev

# Run desktop app in development mode (attaches webviews to Vite dev server)
NORI_DEV=1 dotnet run --project Nori.Desktop

# Run desktop app in production mode (serves compiled dist/ from wwwroot)
pnpm build
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

# 3. Frontend type checking and production compilation
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

### Frontend (TypeScript audio host)
- `src/bootstrap.ts` installs audio handlers only when the injected window label is `audio-host`; do not add user-facing UI to this page.
- Playback, recording, media exchange, and RMS reporting live in `src/services/audio/` and use `src/services/host/` typed commands/events.
- Keep the audio-host command map limited to commands that the hidden host consumes. The native Bridge validates every command and source label.
- Structured errors go through `src/services/runtime/logging.ts`. Send only fixed event IDs and known error types; never pass exception text, recordings, or request content.
- Preserve `base: "./"` and the Vite proxies for `/media` and `/plugins`; plugin pages continue to use their independent WebView and RPC bridge.
- Native Avalonia colors, spacing, radius, and font measures come from `src/assets/style/tokens.ts`. Run `pnpm theme:check` after changing tokens and keep the native contrast tests passing.
- Do not reintroduce Vue, web UI, i18n, UnoCSS, Less, or Web Sentry dependencies into the compatibility host.
- Use `UPPER_SNAKE` for local constants, `camelCase` for local values, and `PascalCase` for exported functions, classes, and types.

### Backend (.NET 10, C#)
- **Casing**: Follow standard .NET conventions: `PascalCase` for types, methods, properties, public fields, and constants; `_camelCase` for private fields; `camelCase` for parameters and local variables. Do not use `UPPER_SNAKE` for C# constants.
- **Bridge Commands**:
  - Commands use `snake_case` with verb-first naming (e.g., `settings_update_workspace`, `model_select`, `memory_list_page`, `audio_level`).
  - Every command must be registered in `BridgeCommands.InvokeAsync`'s switch.
  - Implement host commands in `BridgeCommands.cs`; keep the compatibility host's typed contract in `src/services/host/commands.ts` limited to its audio and structured-log calls. Native windows use their authorized services directly.
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
  - Window operations and `InvokeScript` must run through `Dispatcher.UIThread`.
- **Platform Abstractions**:
  - Platform-specific code must reside behind `IPlatformServices` (`Nori.Core/Platform/`).
  - Code must be capability-driven: inspect `IPlatformServices.Capabilities` (`supportsGlobalCursor`, `supportsHitThrough`, `supportsTray`) before calling OS features instead of relying on `PlatformNotSupportedException`.

---

## 5. Testing Guidelines

### Test Frameworks & Projects
- **Frontend**: Vitest (`app/desktop/vitest.config.ts`), running audio, bridge, native-token, and contrast tests under `app/desktop/tests/`.
- **Backend**: xUnit, partitioned across test projects:
  - `Nori.Core.Tests`: Logic, path safety, zip extraction, configuration parsing/inference, encryption, memory/AI evaluation.
  - `Nori.Desktop.Tests`: Avalonia window metrics, hit mask calculation, bridge routing, diagnostics.
  - `Nori.PluginRuntime.Tests`: Plugin ALC loading, isolation, manifest validation, package safety.
  - `Nori.AppLauncher.Tests`: Deployment slot selection and manifest parsing.

### Critical Automated Checks
- **Native Design Token Synchronization (`native-tokens-sync.test.ts`)**: Verifies generated Avalonia colors, brushes, and measures match `tokens.ts`.
- **Native Contrast Ratios (`contrast.test.ts`)**: Validates text/background contrast for the native palette (≥4.5:1 for body text).
- **Path Security (`ResourcePathSafetyTests.cs`, `AssetPathTests.cs`)**: Prevents path traversal, UNC, symlink breakouts, and directory escaping.
- **Config Inference (`ConfigValueTests.cs`)**: Pins type inference for SQLite key/value reads.

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
- `feat: 支持插件自定义 WebView 窗口`
- `fix: 修复原生 Live2D 模型与行为`

### Pull Request Rules
1. **Scope**: Keep pull requests atomic and focused on a single task. Avoid mixing unrelated refactors.
2. **Local Gate Verification**: Follow Local Verification above and report the checks actually run. CI owns the full gate; do not run unrelated local suites solely to open a PR.
3. **Artifact Cleanup**: Never commit debug probes (`__probe*`), temporary scripts, logs, or scratch files. Remove debug `console.log` statements (retain `console.error` for legitimate error paths).
4. **Visual Changes**: Verify the affected interface through screenshots or interaction. Layout, scaling, and responsive changes require checks at 720×480 and 1080p; other visual changes need evidence for the affected view.

---

## 7. Additional Guardrails

The coding rules above are the source for formatting, bridge registration, and the audio-host command contract.

- **Vite Base URL**: Preserve `base: "./"` in `vite.config.ts`; an absolute base breaks the AssetServer secret prefix.
- **NativeControlHost Manifest**: Preserve `<supportedOS>` entries in `Nori.Desktop/app.manifest` for WebView2 hosting.
- **Treat Warnings as Errors**: Respect the C# projects' `TreatWarningsAsErrors` setting.
- **Avalonia 12 window chrome**: `SystemDecorations` was renamed to `WindowDecorations`; the old enum survives only as `[Obsolete]`.
- **Kestrel dynamic ports**: `ListenLocalhost(0)` is rejected; use `Listen(IPAddress.Loopback, 0)` (see `AssetServer.cs`).
- **`LibraryImport` vs `DllImport`**: `LibraryImport` requires `AllowUnsafeBlocks`; `Nori.Core` uses classic `DllImport` for P/Invokes project-wide.
- **`objc_msgSend`**: Declare a separate `DllImport` per return type (`nint` / `void` / `CGPoint` / `CGRect`); arm64 reads the wrong registers otherwise. All AppKit calls must be on the UI thread.
- **CA1416 and type patterns**: `PlatformServices.Current is MacPlatformServices mac` still needs an `OperatingSystem.IsMacOS() &&` guard or the analyzer errors (warnings are errors here).
- **`Directory.Build.props`**: Owns `TargetFramework` / `Nullable` / `LangVersion` only; `TreatWarningsAsErrors` stays per-project (`Live2DCSharpSDK.*` is ported external code).
- **`tsconfig.json` target**: ES2022 (for `Array.prototype.at`, etc.), matching Vite `build.target: esnext`.
- **Audio host label**: Only the hidden `audio-host` WebView loads `services/audio`; playback state lives in one place for the process lifetime.
- **Microphone consent**: `Avalonia.Controls.WebView 12.1.0` exposes no `PermissionRequested` on `NativeWebView`; macOS needs `NSMicrophoneUsageDescription` in the bundled `Info.plist` (from `publish.sh`) or `getUserMedia` is rejected outright.
- **Completion**: Complete the requested scope and its relevant verification. Report unrelated findings without automatically expanding the change. Ask only when necessary information or authorization is missing; reuse authorization already provided.
