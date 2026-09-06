# Repository Guidelines

Welcome to the **Nori Desktop Pet** repository. This document serves as the comprehensive development and contribution guide for both human developers and autonomous coding agents. Follow all conventions and architectural boundaries documented here to ensure code quality and consistency.

---

## 1. Project Overview & Architecture

Nori Desktop Pet is an AI desktop companion built with a **.NET 10 + Avalonia 12** backend host and a **Vue 3 + TypeScript + UnoCSS** frontend rendered within platform-native webviews.

### Key Architectural Pillars
- **Root Entry & Slot Deployment (`Nori.AppLauncher`)**: The stable root binary `Nori` selects and launches an immutable deployment slot (`app-<version>-<revision>`) validated by `deployment.json`. The launcher does not own locks or update slots; all runtime state resides in `<PackageRoot>/data`.
- **Four Windows Architecture (`Nori.Desktop/Windows`)**:
  - Three WebViews: `first-run` (wizard), `init` (loading/splash), and `main` (control panel, settings, chat, and audio host). Windows are borderless (`WindowDecorations.None`) and transparent. Closing a window hides it; `main` is persistent for the app lifetime.
  - Native Desk Pet (`pet`): An Avalonia `PetWindow` running native OpenGL ES 2.0 via `Live2DCSharpSDK` and Cubism Core. It renders directly to the desktop (no webview airspace clipping or transparency limitations).
- **Native Live2D Desk Pet vs. Web Preview**:
  - `PetWindow` + `PetGlControl`: Native C# pipeline (`AutoBlink`, `EyeFocus`, `BeatSync`, `LipSync`, `ExpressionStore`, `ExpressionBehavior`). Alpha mask sampling (~10Hz) computes a single bounding rectangle for hit-testing (`WM_NCHITTEST` on Windows, `XShape` on Linux X11, `setIgnoresMouseEvents:` on macOS, degraded whole-window hit on Wayland).
  - Web Preview (`ModelManagement.vue`): PixiJS + `pixi-live2d-display` previewing local models through the loopback asset server.
- **IPC Bridge (`NoriBridge` & `BridgeCommands`)**:
  - Frontend invokes host methods via `src/services/runtime/` typed APIs (built on top of `src/services/host/invoke.ts`).
  - Host dispatches commands through `BridgeCommands.cs`. Envelopes are double-encoded (JSON envelope serialized as string into `InvokeScript`) to eliminate escaping vulnerabilities.
  - Commands throw exceptions with human-readable Chinese messages on failure, which become frontend promise rejections.
- **Embedded Asset Server (`AssetServer.cs`)**:
  - Loopback Kestrel HTTP server with a per-process random hex path prefix and strict `Host` header verification.
  - Serves: `/{secret}/app/*` (bundled frontend), `/{secret}/nori-assets/*` (resources), and `/{secret}/media/{token}` (one-shot token exchange for TTS bytes and audio recordings).
- **Storage Layout (`<PackageRoot>/data`)**:
  - Governed strictly by immutable `AppStoragePaths`. Subdirectories: `core/database` (`nori.db`), `core/security` (`secret.key`), `knowledge/documents` (`Memory.md`), `resources/installed`, `plugins`, `webview/cache/host`, and `diagnostics/logs`.
  - Zero fallback to OS system directories. If `<PackageRoot>/data` is unwritable, startup fails explicitly.
  - Legacy Tauri app data directories are used solely as one-time migration sources via `LegacyDataPathResolver`.
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
├── Nori.slnx                   # Solution file covering all .NET projects
├── app/desktop/                # Primary application workspace
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
│   ├── src/                    # Vue 3 SPA frontend
│   │   ├── assets/style/       # tokens.ts (single source of color truth), theme.less
│   │   ├── components/         # UI components (ui/ atomic kit, chat/, settings/, etc.)
│   │   ├── composables/        # useDebouncedSave.ts, useSnapshotField.ts, useSnapshotSave.ts
│   │   ├── services/           # runtime/, host/, i18n/, icon/, router/, window/, audio/, telemetry/
│   │   ├── views/              # FirstRunView.vue, InitView.vue, Main.vue, ChatView.vue
│   │   ├── App.vue             # Window routing coordinator
│   │   └── main.ts             # Vue bootstrap, Naive UI, UnoCSS, Sentry integration
│   ├── tests/                  # Frontend Vitest test suites (theme, i18n, runtime, components)
│   ├── uno.config.ts           # UnoCSS atomic styles, shortcuts, and token integration
│   ├── package.json            # Frontend dependencies and npm scripts
│   └── vite.config.ts          # Vite build config (base: "./", loopback proxy)
```

---

## 3. Build, Test, and Development Commands

All development commands must be run from `app/desktop/`. Use **pnpm** exclusively.

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

### Verification Gates (Must pass in CI)

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
- **Indentation**: **Tabs**, not spaces, across `.ts`, `.vue`, `.cs`, and `.less`.
- **Line Endings**: LF.
- **Quotes**: Double quotes (`"`) across TypeScript, Vue, and C#.
- **Language**: Comments, XML doc comments, logs, and user-facing messages must be in **Chinese**.

### Frontend (Vue 3, TypeScript, UnoCSS)
- **Component Setup**: Always use `<script setup lang="ts">`. Component files must use `PascalCase.vue`.
- **Casing**:
  - Local constants: `UPPER_SNAKE` (e.g., `const ROUTER = useRouter()`, `CONFIG_KEY`).
  - Local variables/props: `camelCase`.
  - Exported functions, classes, and types: `PascalCase`.
- **Styles & Layout**:
  - **UnoCSS Atomic Classes Only**: Do **not** add `<style scoped>` in components.
  - **Length Units**: All dimensions must use `rem` based on root `62.5%` (`1rem = 10px`, spacing `1 = 0.4rem`). **Never use raw `px`**.
  - **Color Tokens**: Colors must originate from `src/assets/style/tokens.ts` (e.g., `text-text-muted`, `bg-bg-card`, `border-line-subtle`). Bare hex values outside `tokens.ts` are forbidden.
  - **Contrast & Minimum Size**: Minimum font size is `text-xs` (1.15rem). High text contrast (≥4.5:1) is strictly verified by tests.
  - **Static Class Scanning**: Avoid template-string dynamic classes (e.g., `` `bg-${x}` ``). Use literal ternary expressions.
  - **Atomic UI Components**: Reuse components in `src/components/ui/` (`AppButton`, `AppCard`, `AppField`, `AppSwitchRow`, etc.) and UnoCSS shortcuts (`glass-panel`, `surface-card`, `btn-primary`, `scroll-area`).
- **Internationalization (i18n)**:
  - **Zero CJK literals in `<template>`**: Use `computed(() => useLanguages().views.xxx)`.
  - Adding text requires updating **three locations**: `locales/zh-CN.ts`, `locales/en-US.ts`, and the accessor tree in `useLanguages.ts`.
- **State & Data Management**:
  - Interact with host backend exclusively through `src/services/runtime/` typed APIs. Do not access `window.__nori` directly.
  - Debounce user input saves with `useDebouncedSave.ts` (~400ms, distinct timer per field).
  - Use `useSnapshotField.ts` to read runtime snapshots without clobbering active user edits.
  - User-facing failures must be routed to `feedback.error(可读中文, error)`.

### Backend (.NET 10, C#)
- **Casing**: Follow standard .NET conventions: `PascalCase` for types, methods, properties, public fields, and constants; `_camelCase` for private fields; `camelCase` for parameters and local variables. Do not use `UPPER_SNAKE` for C# constants.
- **Bridge Commands**:
  - Commands use `snake_case` with verb-first naming (e.g., `ui_get_snapshot`, `settings_update_ai`, `window_show`).
  - Every command must be registered in `BridgeCommands.InvokeAsync`'s switch.
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
- **Frontend**: Vitest (`app/desktop/vitest.config.ts`), running tests under `app/desktop/tests/`.
- **Backend**: xUnit, partitioned across test projects:
  - `Nori.Core.Tests`: Logic, path safety, zip extraction, configuration parsing/inference, encryption, memory/AI evaluation.
  - `Nori.Desktop.Tests`: Avalonia window metrics, hit mask calculation, bridge routing, diagnostics.
  - `Nori.PluginRuntime.Tests`: Plugin ALC loading, isolation, manifest validation, package safety.
  - `Nori.AppLauncher.Tests`: Deployment slot selection and manifest parsing.

### Critical Automated Checks
- **Design Token Synchronization (`tokens-sync.test.ts`)**: Enforces parity between `tokens.ts`, `uno.config.ts`, `theme.less`, and `naiveOverrides.ts`.
- **Contrast Ratios (`contrast.test.ts`)**: Validates text/background contrast across all token pairs (≥4.5:1 standard, ≥3:1 for large text).
- **i18n Completeness (`completeness.test.ts`)**: Enforces key-set parity between `zh-CN.ts` and `en-US.ts`, verifies accessor tree coverage, and ensures zero Chinese literals exist in Vue templates.
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
2. **Local Gate Verification**: Ensure `pnpm check:todo`, `pnpm lint`, `pnpm build`, `pnpm test`, `dotnet build`, and `dotnet test` all pass locally before opening a PR.
3. **Artifact Cleanup**: Never commit debug probes (`__probe*`), temporary scripts, logs, or scratch files. Remove debug `console.log` statements (retain `console.error` for legitimate error paths).
4. **Visual Changes**: If modifying UI or Live2D layouts, include screenshots or screen recordings at minimum resolutions (720×480 and 1080p).

---

## 7. Agent-Specific Instructions & Guardrails

When modifying this repository, autonomous agents must adhere to the following checklist:

1. **Tabs Only**: Verify your editor or tool does not substitute tabs with spaces in `.ts`, `.vue`, `.cs`, or `.less`.
2. **No Scoped Styles**: Never introduce `<style scoped>` in `.vue` files. Use UnoCSS utilities and atomic UI components.
3. **i18n Purity**: If adding UI text, update `zh-CN.ts`, `en-US.ts`, and `useLanguages.ts` in lockstep. Never hardcode strings in `<template>`.
4. **Bridge Command Registration**: When adding a C# bridge command:
   - Implement the logic in `BridgeCommands.cs`.
   - Add the case to the `InvokeAsync` switch statement.
   - Add XML doc comments showing `invoke(...)`.
   - Add corresponding typed functions in `src/services/runtime/`.
5. **No Direct `window.__nori`**: Components must strictly use `src/services/runtime/` or `src/services/host/`.
6. **Vite Base URL**: Never change `base: "./"` in `vite.config.ts`. An absolute base will break the AssetServer's secret prefix and result in blank webview windows.
7. **NativeControlHost Manifest**: Never remove `<supportedOS>` entries from `Nori.Desktop/app.manifest`; doing so breaks WebView2 native control hosting.
8. **Treat Warnings as Errors**: C# projects enforce `TreatWarningsAsErrors`. Do not leave unused imports, unhandled nullable warnings, or missing XML doc tags on public APIs.
