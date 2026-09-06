# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

Two independent deliverables, no shared build:

- `app/desktop/` — the Nori desktop pet. **.NET 10 + Avalonia 12 host** (`Nori.Desktop/`, `Nori.Core/`, C#) + Vue 3 SPA (`src/`, TypeScript + **UnoCSS**) rendered in Avalonia's **cross-platform NativeWebView** (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux). This is where nearly all work happens.
- `docs/` — Chinese design docs. `规范.md` is a binding style contract, not advice — read it before touching frontend or C# code. `技术.md` is the module/tech map (and records the pet-window transparency verification), `跨平台.md` the platform support matrix + degradation table, `开发任务清单.md` the roadmap, `windows.md` an Avalonia window-property reference.
- `Nori.AppLauncher/` is the dependency-free stable root entry (`Nori`). It selects only a validated `app-<numeric-version>-<revision>` slot; it never updates/deletes slots or owns the single-instance lock. Published data is created only at `<PackageRoot>/data` and is never included in a package.

`README.md` contains the project overview, current stabilization boundary and development gates.

## Commands

Desktop app — run from `app/desktop/`. **Use pnpm**: the project is managed with pnpm, and `node_modules` layout assumptions in scripts assume it.

```bash
pnpm install          # 安装前端依赖
pnpm build            # vue-tsc --noEmit && vite build  ← the frontend gate
pnpm test             # vitest run  ← 前端纯函数/服务回归
dotnet build          # builds Nori.Core + Nori.Desktop + tests  ← the backend gate
dotnet test           # xUnit; pure-function coverage
./publish.bat         # framework-dependent publish (no bundled runtime) for win-x64
./publish.sh          # same, for linux-x64 / linux-arm64 / osx-arm64 / osx-x64
```

CI (`.github/workflows/build.yml`) runs all four gates on `windows-latest` / `ubuntu-latest` / `macos-14` plus a publish smoke test.

Running the app:

```bash
dotnet run --project Nori.Desktop            # production: serves the built dist/ from wwwroot
NORI_DEV=1 dotnet run --project Nori.Desktop # dev: points the WebView at vite on :1420
pnpm dev                                      # vite only; must be running for NORI_DEV=1
```

`规范.md` requires `pnpm build`, `dotnet build` and `dotnet test` to pass before a change is considered done. `tsconfig.json` sets `noUnusedLocals`/`noUnusedParameters`; the C# projects set `TreatWarningsAsErrors`.

### Stabilization contracts

- 产品版本由构建环境控制；未显式注入时唯一默认值是 `Dev`。Release 必须手动输入全历史唯一 codename，数字版本也不得由另一个发布标签重用；通过 `NORI_PRODUCT_VERSION` 注入稳定版本、通过 `NORI_PRODUCT_INFORMATIONAL_VERSION` 注入带 `NORI_COMMIT_SHA` 短 hash 的 informational version。CLR/File/NuGet 版本保持数字格式，`ProductVersion.Current` 保留完整 `v<version>-<codename>+<shortsha>` informational version。不要新增 `0.1.0` 回退。`ProductVersion.Current` 进入 snapshot、Smoke readiness、诊断、Crash 报告和 MCP clientInfo。
- `--safe-mode` 只接受人工命令行启动，不自动恢复。它保留 UI、日志、诊断和本地手动修复，跳过 MCP 自动连接、Proactive/Reflection、知识与记忆后台维护、AI 伴侣交互及 Live2D 自动模型加载；Bridge 入口同时拒绝交互式联网、Provider/MCP/语音外部操作。
- `readiness.json` schema v2 必须包含 `product_version`、`database_schema_version`、`config_schema_version` 和 `safe_mode`；Windows CI 覆盖 first-run、initialized 和 initialized safe-mode。
- `export_diagnostics` 只生成大小受限、脱敏白名单 ZIP；不得包含数据库、聊天/记忆/提示词、工具参数/结果、请求正文、录音、资源、凭据或真实用户路径。Provider 连接测试发送固定探测且不持久化配置或内容。
- 迁移前备份使用 `VACUUM INTO`，单文件上限 64 MiB、最多保留 3 份；新增迁移必须保持这条保护。

## Architecture

### Four windows, three NativeWebView + one native OpenGL

`Nori.Desktop/Windows/WindowDefinition.cs` declares four windows — `first-run`, `init`, `main`, `pet` — all created hidden, borderless (`WindowDecorations.None`) and transparent.
- Three windows (`first-run`, `init`, `main`) are `NoriWindow` hosting `NativeWebView` and loading the Vue bundle. `main` doubles as the **audio host** (see below) — it only hides on close, so it is always alive.
- The desktop pet (`pet`) is a native Avalonia `PetWindow` hosting `PetGlControl` (OpenGL via `Live2DCSharpSDK`), bypassing webview airspace and window-region clipping issues completely. Same code on all three desktops.

1. `App.cs` reads `first_run_completed` from SQLite and shows `first-run` or `init`.
2. The host navigates each webview to `…/app/index.html?window=<label>`.
3. Each webview mounts `App.vue`, which calls `navigateToOwnWindow()` — it reads its own label from the query string and `router.replace()`s to the mapped route.
4. The label→route table is `WINDOW_ROUTES` in `src/services/window/index.ts`.

**Adding a webview window means touching four places**: `WindowDefinition.All`, the `WindowLabel` union, `WINDOW_ROUTES`, and `src/services/router/index.ts`.

First-run flow: wizard in `first-run` → `complete_first_run` (C#) sets an `initStartPending` flag, closes `first-run`, shows `init`, broadcasts `nori:init-start` → `InitView` opens `main`, then closes `init`.

**The broadcast can outrun the subscription.** `init` starts hidden on the first-run path, and its webview may still be loading when `nori:init-start` fires — the event is then lost forever and the page spins on the loading ring. So `InitView` **subscribes first, then calls `init_ready`**, which returns (and clears) `initStartPending`; a true value means "run the flow now". `startInitFlow` has a reentrancy gate, and a 10s watchdog offers a manual "open main window" escape hatch.

The tray (`Tray/TrayMenu.cs`) is the primary always-available entry point: left-click opens `main`, menu toggles `pet`. `Install` returns `false` when the tray fails (some Linux desktops have no StatusNotifier), which flips `platform.supportsTray` so the frontend shows a built-in entry instead. Closing a window only hides it (`NoriWindow.AllowClose` / `PetWindow.AllowClose` gates real disposal); `ShutdownMode.OnExplicitShutdown` keeps the process alive.

`WindowManager` tracks each window's `IsVisible` and raises `VisibilityChanged`; `AppRuntime` turns that into `InvalidateSnapshot("pet")`, so `snapshot.pet.visible` is the single source of truth for pet state — toggling from the tray updates the main window immediately.

### The bridge (replaces Tauri IPC)

`NativeWebView` only offers JS→host `invokeCSharpAction(string)` and host→JS `InvokeScript`. On top of that:

- **Bootstrap** — an inline `<script>` in `index.html`, before the module script, defines `window.__nori` (`invoke` / `emit` / `listen` / `dispatch`, plus `label` and `assetBase`). It must stay synchronous and first.
- **Frontend API** — `src/services/host/` (`invoke.ts`, `event.ts`, `window.ts`, `shell.ts`). **Never touch `window.__nori` directly from components.**
- **Host side** — `Bridge/NoriBridge.cs` does dispatch/correlation, `Bridge/BridgeCommands.cs` holds the handlers, `Bridge/AppServices.cs` is the service container.

Envelopes are double-encoded: the host serializes the JSON envelope, then serializes *that string* into the `InvokeScript` call, and JS `JSON.parse`s it back. This is deliberate — it makes escaping bugs impossible.

**Every command must be registered in `BridgeCommands.InvokeAsync`'s switch** — an unregistered command compiles fine and fails only at runtime with `未知的命令`. Commands throw on failure; the message is user-facing Chinese text and becomes the frontend's rejection.

Privileged commands allowlist their caller by window label — `complete_first_run` rejects anything but a visible `first-run` webview. Follow that pattern for anything state-changing.

Host→frontend events:

| Event | Emitted by | Consumed by |
|---|---|---|
| `nori:init-start` | `complete_first_run` | `InitView.vue` |
| `nori:config-changed` | every `set_config` | WebViews — hot-applies display config |
| `nori:play-motion` | `chat_completion` | WebViews |
| `nori:window-metrics` | `NoriWindow.PostMetrics` | `services/host/window.ts` cache |
| `nori:audio-play` / `nori:audio-stop` | `WebViewAudioPlayback` | `services/audio/` in `main` |
| `nori:audio-record-start` / `-stop` | `WebViewMicrophoneRecorder` | `services/audio/` in `main` |

Blocking work (HTTP, zip extraction, SQLite) must stay off the UI thread; anything touching windows or `InvokeScript` must go through `Dispatcher.UIThread`.

### Serving the frontend and assets

There is no custom URI scheme any more — Avalonia's `WebResourceRequested` is read-only and cannot return a response. Instead `Nori.Core/Assets/AssetServer.cs` runs a **Kestrel server bound to `IPAddress.Loopback`** with a per-process random hex path prefix and a `Host`-header check. It mounts:

- `/{secret}/app/*` → the built Vue bundle (`wwwroot`, copied from `dist/`)
- `/{secret}/nori-assets/*` → `<PackageRoot>/data/resources`
- `/{secret}/media/{token}` → one-shot audio exchange (`MediaExchange`): `GET` takes the TTS bytes (removed on read, 2min TTL), `POST` delivers a microphone recording back. Same `Host`-header and prefix checks apply.

App and assets are **same-origin**, so `assetUrl()` is a relative path and there is no CORS to configure. `vite.config.ts` sets `base: "./"` — with an absolute base the built `/assets/…` URLs skip the secret prefix and 404. In dev, `AssetServer` uses fixed port 14201 with no prefix and vite proxies `/nori-assets` to it, so frontend code is identical in both modes.

`AssetPath.cs` is a faithful port of the old `asset.rs`: percent-decoding, absolute/UNC/drive-letter/`..` rejection, canonicalized containment checks, symlink-escape checks, the MIME table, and `PathCandidates`.

**`PathCandidates` only removes path segments, never adds them.** It fixes requests that are one level too deep (a `model3.json` referencing `subdir/tex.png`), *not* zips with an extra nested top-level folder — the old CLAUDE.md claimed the latter and was wrong. The nested-zip case is handled at extraction time instead, by `ZipExtractor.FindCommonTopDirectory`.

### Config: SQLite key/value with inferred types

`nori.db` lives in `<PackageRoot>/data/core/database/` with two tables: `config(key TEXT PRIMARY KEY, value TEXT)` and `chat_messages`. Everything is stored as TEXT. The old Tauri `app_data_dir()/data` is used only as a one-time migration source; package data is never redirected to system AppData. `<PackageRoot>` must remain writable and the complete package must be movable.

The trap: `ConfigValue.FromStorage` **re-infers the type on read**. `"1"`/`"true"` → Boolean, digit strings → Integer, `{…}`/`[…]` → Json, everything else → String. A config you wrote as a string comes back as a number if it happens to look like one, so `invoke<string | null>("get_config", …)` is a lie for numeric-looking values — `parseNumber()` in `services/live2d/config.ts` exists for exactly this. `"1.25"` stays a String (i64 parse fails, not a JSON container) — `Nori.Core.Tests` pins all of this.

`set_config` broadcasts `nori:config-changed` app-wide, which is how the pet window live-updates. Schema evolution goes through `config_schema_version` + `MigrateSchema()`; a DB newer than the binary is rejected outright.

Secrets (`*_api_key`, `*_secret`, `*_token`, `*_password`) are encrypted with **AES-256-GCM** (`nsec2:` prefix) — no longer raw DPAPI. The master key lives in the platform keystore (`SecretKeyStore`): DPAPI-wrapped file on Windows, Keychain on macOS, libsecret on Linux, 0600 file as fallback. Old `enc:dpapi:` values still decrypt **on Windows only**; elsewhere `ConfigStore.IsUnreadableSecret` flags them so the UI asks the user to re-enter that one key — never silently wiping other config.

Live2D display settings are stored **per model** as `<base>_<modelId>` (e.g. `l2d_scale_arg-nori`) with fallback to the legacy global key — see `l2dModelKey()` / `readModelConfig()`. Keep both lookups when adding keys.

### Resource management (local only)

Models are local resources under `<PackageRoot>/data/resources/installed/live2d/<name>/`; there is no remote download or gateway. Runtime data is strictly under `<PackageRoot>/data`, which must be writable and movable as a whole package. The frontend calls `check_resource` to test install state and `import_local_resource` to add models from a local ZIP or folder. `ResourceManager` in `Nori.Core/Resources/` covers check/list/delete/import, and Live2D resources count as installed only when they contain a `.model3.json`.

`ZipExtractor` is hardened — rejects absolute paths, UNC, drive letters, `..`, control chars and symlink entries, and re-canonicalizes each parent against the target. It also strips a single common top-level directory. Don't loosen it.

### Live2D: Native OpenGL Desk Pet + PixiJS Setting Preview

Desktop Pet rendering is implemented natively in Avalonia (`PetWindow.cs` + `PetGlControl.cs`) using `Live2DCSharpSDK` and Cubism Native Core:
- Direct OpenGL ES 2.0 rendering in a transparent Avalonia window (`OpenGlControlBase`).
- High-quality 2048x2048 clipping mask buffer, 16x anisotropic filtering, and high precision mask enabled.
- Alpha mask sampling (~10Hz) derives one contiguous bounding rectangle around the visible model, preventing fragmented head/body hit regions. **Per platform**: Windows queries that rectangle from a `WM_NCHITTEST` hook; Linux/X11 sends it to `XShapeCombineRectangles(ShapeInput)`; macOS toggles `setIgnoresMouseEvents:` based on whether the cursor is inside it. Wayland has neither — `supportsHitThrough` goes false and the pet degrades to a fully clickable window.
- 1:1 C# behavioral pipeline (`AutoBlink`, `EyeFocus`, `IdleDisable`, `BeatSync`, `LipSync`, `ExpressionStore`, `ExpressionBehavior`). Lip-sync amplitude now comes from the frontend's `audio_level` reports, not NAudio.
- Native mouse drag (4px threshold + position persistence), tap actions/expressions, global cursor tracking, and deep sea glow themed context menu.
- Settings page preview retains PixiJS + `pixi-live2d-display` with texture mipmapping (`baseTexture.mipmap = 1`), 2048 mask buffer, and `devicePixelRatio` DPI super-sampling.

### Local model management

Model management lives in `src/components/settings/ModelManagement.vue`: import local Live2D ZIP/folder, enable an installed model, adjust per-model display settings, and preview. First-run `ModelSelect.vue` only records the chosen model id; the app never downloads it.

## Conventions (from `docs/规范.md` — follow these, they're enforced by review)

- **Tabs**, not spaces, in `.ts` / `.vue` / `.cs` / `.less`. Double quotes. LF.
- Comments, doc comments, log messages and user-facing Chinese strings stay **in Chinese** — match the surrounding files.
- Frontend naming is unusual: **local constants are `UPPER_SNAKE`** (including `const ROUTER = useRouter()`), local variables `camelCase`, exported functions/types `PascalCase`. **C# does not follow this** — it uses normal .NET conventions (`PascalCase` members, `_camelCase` private fields). Config keys and bridge commands stay `snake_case`, verb-first.
- Vue: `<script setup lang="ts">` only, PascalCase filenames, pages in `views/`, pieces in `components/`. Prefer `ref`/`computed`; avoid `reactive` for whole objects.
- Styles: **UnoCSS atomic classes only — no `<style scoped>` in components.** `uno.config.ts` uses `presetWind3` with **no reset** (the reset lives in `theme.less`, which would otherwise fight naive-ui).
  - **All lengths in `rem`**; root font size is `62.5%`, so `1rem = 10px` and the Uno spacing scale is a 4px grid (`1` = `0.4rem`). Non-scale values use `w-[6.8rem]`. **No `px` anywhere.**
  - **Single colour source**: `src/assets/style/tokens.ts`. The Uno theme, `naiveOverrides.ts` and the `:root` block in `theme.less` all derive from it; `tests/theme/tokens-sync.test.ts` fails if they drift. Never write a bare hex outside `tokens.ts`.
  - Minimum font size is `text-xs` (1.15rem). Text colour uses `--danger-text`; `--danger` is fills/borders only. Contrast ≥4.5:1 is enforced by `tests/theme/contrast.test.ts`.
  - **UnoCSS scans statically** — class names must appear literally. Template-string class names (`` `bg-${x}` ``) silently produce nothing; use full literal class names in ternaries.
  - Reuse `uno.config.ts` shortcuts (`glass-panel`, `surface-card`, `scroll-area`, `btn-primary`, `chip`, `input-base`, `focus-ring`, `nav-item`, …) and the `App*` components in `src/components/ui/` instead of re-deriving them.
  - **naive-ui appearance is tuned only in `naiveOverrides.ts`** — its styles are runtime CSS-in-JS with injection order we don't control, so atomic classes must not target naive's internal DOM.
- i18n text needs **three** edits: `locales/zh-CN.ts`, `locales/en-US.ts`, and the typed accessor tree in `useLanguages.ts`. In components always wrap it as `computed(() => useLanguages().views.xxx)` so it re-renders on locale change. **No CJK literals in `<template>`** — `tests/i18n/completeness.test.ts` checks key-set parity, accessor coverage, and template purity.
- Icons go in the `icon` object in `services/icon/index.ts` (24×24 viewBox) and render via `<Icon name="…"/>`.
- Debounce config writes (~400 ms) via `src/composables/useDebouncedSave.ts` — it already gives **each field its own timer** (a shared timer silently drops the earlier field's value) and flushes on unmount. Read snapshot-backed fields through `useSnapshotField.ts` so incoming snapshots don't clobber what the user is typing.
- **Failures must be visible**: route them through `services/feedback` (`feedback.error(中文, error)`). `console.error` is diagnostics only, never the sole output.
- **Never hard-code platform assumptions**: read `RUNTIME.platform()` (`supportsGlobalCursor` / `supportsWindowDrag` / `supportsHitThrough` / `supportsTray`) and disable the affected UI with an explanation instead of swallowing `PlatformNotSupportedException`.
- `App.cs` is assembly only — new logic gets a new module. Every bridge command needs a `///` doc comment showing the frontend `invoke(...)` call.
- Be conservative about new dependencies. Remove debug `console.log` before committing; keep `console.error` on meaningful failure paths. Don't leave scratch files in the repo.

## Known traps

- **`NativeControlHost` needs a manifest.** `Nori.Desktop/app.manifest` must keep its `<compatibility>` / `<supportedOS>` list, or the WebView throws `Unable to create child window for native control host` at startup. It's now a Windows-conditional csproj item (`NoriIsWindowsBuild`) — still required there.
- **Avalonia 12 renamed `SystemDecorations` to `WindowDecorations`** and removed the old enum; the property survives only as `[Obsolete]`.
- **Kestrel rejects `ListenLocalhost(0)`.** Dynamic ports must use `Listen(IPAddress.Loopback, 0)`.
- **`vite.config.ts` must keep `base: "./"`.** An absolute base emits `/assets/…`, which bypasses the AssetServer's secret prefix and 404s — the app loads a blank window with no error.
- **`LibraryImport` requires `AllowUnsafeBlocks`.** `Nori.Core` deliberately uses classic `DllImport` for its P/Invokes to avoid enabling unsafe code project-wide.
- Window dragging cannot use CSS. `data-tauri-drag-region` is gone; `TitleBar.vue` calls `window_start_drag`.
- **`IPlatformServices` is capability-flag driven, not throw-driven.** Every platform ships an implementation (`Windows` / `Mac` / `Linux` / `Unsupported`); read `Capabilities` before calling. `SetClickThrough` is deliberately a silent no-op when unsupported — the pet calls it ~10Hz and must never break its render loop.
- **`objc_msgSend` is variadic**: declare a separate `DllImport` per return type (`nint` / `void` / `CGPoint` / `CGRect`), or arm64 reads the wrong registers. All AppKit calls must be on the UI thread.
- **CA1416 doesn't see through type patterns.** `PlatformServices.Current is MacPlatformServices mac` still needs an `OperatingSystem.IsMacOS() &&` guard or the analyzer errors (warnings are errors here).
- **`Directory.Build.props` owns `TargetFramework`/`Nullable`/`LangVersion` only.** `TreatWarningsAsErrors` stays per-project — `Live2DCSharpSDK.*` is ported external code and can't meet our strict bar.
- **`tsconfig.json` targets ES2022** (for `Array.prototype.at` etc.), matching vite's `build.target: esnext`. Don't drop it back to ES2020.
- **The audio host is `main` only.** `pet`/`init` must not install `services/audio` — playback state lives in one place, and `main` is the only webview guaranteed to exist for the whole process lifetime.
- **`Avalonia.Controls.WebView 12.1.0` exposes no `PermissionRequested` event** on `NativeWebView`, so microphone consent is entirely the OS's native prompt. macOS needs `NSMicrophoneUsageDescription` in the bundled `Info.plist` (generated by `publish.sh`) or `getUserMedia` is rejected outright.
