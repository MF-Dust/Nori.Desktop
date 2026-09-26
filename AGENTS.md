# Repository Guidelines

Welcome to the **Nori Desktop Pet** repository. This document serves as the comprehensive development and contribution guide for both human developers and autonomous coding agents. Follow all conventions and architectural boundaries documented here to ensure code quality and consistency.

---

## 1. Project Overview & Architecture

Nori Desktop Pet is an AI desktop companion built with a **.NET 10 + Avalonia 12** native host. The bundled page is only the compatibility audio host; plugin widgets may open their own WebView.

### Key Architectural Pillars
- **Root Entry & Slot Deployment (`Nori.AppLauncher`)**: The stable root binary `Nori` selects and launches an immutable deployment slot (`app-<version>-<revision>`) validated by `deployment.json`. The launcher does not own locks or update slots; all runtime state resides in `<PackageRoot>/data`.
- **Window Architecture (`Nori.Desktop/Windows`)**:
  - User windows are native Avalonia: `first-run`, `init`, `main`, and `pet`, plus on-demand settings, memory, models, chat, and quick chat. Windows are borderless (`WindowDecorations.None`) and transparent. Closing a window hides it; `main` is persistent for the app lifetime.
  - The only app WebView is the hidden audio host, created when the platform audio backend is not native.
  - Native Desk Pet (`pet`): An Avalonia `PetWindow` running native OpenGL ES 2.0 via `Live2DCSharpSDK` and Cubism Core. It renders directly to the desktop (no webview airspace clipping or transparency limitations).
  - Native Settings: `SettingsWindow` hosts Avalonia settings pages; inspect this native path for settings UI work.
- **Native Live2D Desk Pet**:
  - `PetWindow` + `PetGlControl`: Native C# pipeline (`AutoBlink`, `EyeFocus`, `BeatSync`, `LipSync`, `ExpressionStore`, `ExpressionBehavior`). Alpha mask sampling (~10Hz) computes a single bounding rectangle for hit-testing (`WM_NCHITTEST` on Windows, `XShape` on Linux X11, `setIgnoresMouseEvents:` on macOS, degraded whole-window hit on Wayland). Model preview is the native `ModelPreviewControl`, not a web view.
- **IPC Bridge (`NoriBridge` & `BridgeCommands`)**:
  - The hidden audio host uses the narrow command map in `src/services/host/commands.ts`; structured audio errors use `src/services/runtime/logging.ts`.
  - Host dispatches commands through `BridgeCommands.cs`. Envelopes are double-encoded (JSON envelope serialized as string into `InvokeScript`) to eliminate escaping vulnerabilities.
  - Commands throw exceptions with human-readable Chinese messages on failure, which become frontend promise rejections.
- **Embedded Asset Server (`AssetServer.cs`)**:
  - Loopback Kestrel HTTP server with a per-process random hex path prefix and strict `Host` header verification.
  - Serves: `/{secret}/app/*` (bundled frontend), `/{secret}/nori-assets/*` (resources), and `/{secret}/media/{token}` (one-shot token exchange for TTS bytes and audio recordings).
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
- **Safe Mode (`--safe-mode`)**:
  - Manual CLI flag that skips MCP auto-connection, Proactive/Reflection schedulers, background memory maintenance, AI pet interactions, and Live2D model auto-load, while disabling external network provider calls.

---

## 2. Project Structure & Module Organization

```text
Nori-Desktop-Pet/
├── .github/workflows/          # CI (build.yml) and release (release.yml) definitions
├── docs/                       # Chinese technical specifications and standards
│   ├── 规范.md                 # Binding code style, design token, and naming contracts
│   ├── 技术.md                 # Architecture, IPC bridge, window models, and contracts
│   ├── 跨平台.md               # OS support matrix, platform degradation, and requirements
│   └── plugin-system.md        # Plugin runtime architecture and SDK specifications
├── app/desktop/                # Primary application workspace
│   ├── Nori.slnx               # Solution file covering all .NET projects
│   ├── Live2D/                 # Cubism Native Core and model definitions
│   ├── Live2DCSharpSDK.*/      # Ported Live2D C# OpenGL rendering engine
│   ├── Nori.AppLauncher/       # Dependency-free root launcher and deployment slot selector
│   ├── Nori.AppLauncher.Tests/ # Slot selection and launcher unit tests
│   ├── Nori.Core/              # Avalonia-independent logic (Data, Config, Mcp, Memory, Voice, etc.)
│   ├── Nori.Core.Tests/        # Core business and security unit tests
│   ├── Nori.Desktop/           # Avalonia host (Windows, Tray, PetGlControl, Bridge, Audio)
│   ├── Nori.Desktop.Tests/     # Host, window management, and smoke tests
│   ├── Nori.PluginRuntime/     # Plugin host, ALC loader, and capability router
│   ├── Nori.PluginRuntime.Tests/ # Plugin runtime isolation and lifecycle tests
│   ├── public/                 # Static assets for the frontend
│   ├── scripts/                # CI, coverage, todo-check, and packaging scripts
│   ├── src/                    # Audio-host page and typed bridge client
│   │   ├── assets/style/       # tokens.ts is the native theme token source
│   │   ├── services/audio/     # WebAudio playback, recording, and RMS analysis
│   │   ├── services/host/      # Typed audio commands and host events
│   │   ├── services/runtime/   # Structured audio-host logging
│   │   └── bootstrap.ts        # Audio-host page entry
│   ├── tests/                  # Audio, bridge, native token, and contrast tests
│   ├── scripts/sync-design-tokens.mjs # Native theme token generator
│   ├── package.json            # Frontend dependencies and npm scripts
│   └── vite.config.ts          # Vite build config (base: "./", loopback proxy)
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
- Preserve `base: "./"` and the Vite proxies for `/nori-assets`, `/media`, and `/plugins`; plugin pages continue to use their independent WebView and RPC bridge.
- Native Avalonia colors, spacing, radius, and font measures come from `src/assets/style/tokens.ts`. Run `pnpm theme:check` after changing tokens and keep the native contrast tests passing.
- Do not reintroduce Vue, web UI, i18n, UnoCSS, Less, or Web Sentry dependencies into the compatibility host.
- Use `UPPER_SNAKE` for local constants, `camelCase` for local values, and `PascalCase` for exported functions, classes, and types.

### Backend (.NET 10, C#)
- **Casing**: Follow standard .NET conventions: `PascalCase` for types, methods, properties, public fields, and constants; `_camelCase` for private fields; `camelCase` for parameters and local variables. Do not use `UPPER_SNAKE` for C# constants.
- **Bridge Commands**:
  - Commands use `snake_case` with verb-first naming (e.g., `ui_get_snapshot`, `settings_update_ai`, `window_show`).
  - Every command must be registered in `BridgeCommands.InvokeAsync`'s switch.
  - Implement host commands in `BridgeCommands.cs`; keep the compatibility host's typed contract in `src/services/host/commands.ts` limited to its audio and structured-log calls. Native windows use their authorized services directly.
  - Every command requires an XML `///` doc comment displaying an invocation example:
    ```csharp
    /// <summary>
    /// 获取当前应用运行时快照。
    /// 前端调用：invoke("ui_get_snapshot")
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
- **Completion**: Complete the requested scope and its relevant verification. Report unrelated findings without automatically expanding the change. Ask only when necessary information or authorization is missing; reuse authorization already provided.
