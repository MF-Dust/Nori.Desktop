# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository layout

Two independent deliverables, no shared build:

- `app/desktop/` — the Nori desktop pet. **.NET 10 + Avalonia 12 host** (`Nori.Desktop/`, `Nori.Core/`, C#) + Vue 3 SPA (`src/`, TypeScript + **UnoCSS**) rendered in Avalonia's **cross-platform NativeWebView** (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux). This is where nearly all work happens.
- `docs/` — Chinese design docs. `规范.md` is the binding style contract; consult the sections relevant to the changed code. `技术.md` is the module/tech map (and records the pet-window transparency verification), `跨平台.md` the platform support matrix + degradation table, `开发任务清单.md` the roadmap, `windows.md` an Avalonia window-property reference.
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

Local verification follows `AGENTS.md` → Local Verification; consult `docs/规范.md` for affected coding conventions. `tsconfig.json` sets `noUnusedLocals`/`noUnusedParameters`; the C# projects set `TreatWarningsAsErrors`.

### Stabilization contracts

- 产品版本由构建环境控制；未显式注入时默认值是 `Dev`。Release 通过手动 codename 生成稳定版本与标签，数字版本也不得由另一个发布标签重用；通过 `NORI_PRODUCT_VERSION` 注入稳定版本、通过 `NORI_PRODUCT_INFORMATIONAL_VERSION` 注入带 `NORI_COMMIT_SHA` 短 hash 的 informational version。CLR/File/NuGet 版本保持数字格式，`ProductVersion.Current` 保留完整 `v<version>-<codename>+<shortsha>` informational version。不要新增 `0.1.0` 回退。`ProductVersion.Current` 进入 snapshot、Smoke readiness、诊断、Crash 报告和 MCP clientInfo。
- `--safe-mode` 只接受人工命令行启动，不自动恢复。它保留 UI、日志、诊断和本地手动修复，跳过 MCP 自动连接、Proactive/Reflection、知识与记忆后台维护、AI 伴侣交互及 Live2D 自动模型加载；Bridge 入口同时拒绝交互式联网、Provider/MCP/语音外部操作。
- `readiness.json` schema v2 必须包含 `product_version`、`database_schema_version`、`config_schema_version` 和 `safe_mode`；Windows CI 覆盖 first-run、initialized 和 initialized safe-mode。
- `export_diagnostics` 只生成大小受限、脱敏白名单 ZIP；不得包含数据库、聊天/记忆/提示词、工具参数/结果、请求正文、录音、资源、凭据或真实用户路径。Provider 连接测试发送固定探测且不持久化配置或内容。
- 迁移前备份使用 `VACUUM INTO`，单文件上限 64 MiB、最多保留 3 份；新增迁移必须保持这条保护。

## Architecture

### Native windows architecture

所有用户界面窗口现已原生化:
- **桌宠** (`pet`) — 原生 Avalonia `PetWindow` + `PetGlControl` (OpenGL via `Live2DCSharpSDK`)
- **对话** (`chat`, `quick-chat`) — 原生 `ChatWindow` / `QuickChatWindow` (深色主题)
- **记忆** (`memory`) — 原生 `MemoryWindow`
- **模型** (`models`) — 原生 `ModelsWindow` (包含隔离的模型预览)
- **设置** (`settings`) — 原生 `SettingsWindow`
- **音频宿主** (`main`) — 唯一保留的 `NoriWindow` WebView,托管 WebAudio/MediaRecorder

`App.cs` 读取 `first_run_completed` 并显示首次运行向导或初始化流程,然后打开主窗口。所有窗口创建时隐藏、无边框 (`WindowDecorations.None`)。

系统托盘 (`Tray/TrayMenu.cs`) 是主要入口点:左键打开对话窗口,菜单切换桌宠。`Install` 在某些 Linux 桌面上返回 false (无 StatusNotifier),此时 `platform.supportsTray` 为 false,前端显示内置入口。关闭窗口仅隐藏 (`AllowClose` 门控真正的 disposal);`ShutdownMode.OnExplicitShutdown` 保持进程存活。

`WindowManager` 跟踪每个窗口的 `IsVisible` 并触发 `VisibilityChanged`;`AppRuntime` 将其转换为 `InvalidateSnapshot("pet")`,因此 `snapshot.pet.visible` 是桌宠状态的唯一真实来源。

### The bridge (replaces Tauri IPC)

`NativeWebView` only offers JS→host `invokeCSharpAction(string)` and host→JS `InvokeScript`. On top of that:

- **Bootstrap** — an inline `<script>` in `index.html`, before the module script, defines `window.__nori` (`invoke` / `emit` / `listen` / `dispatch`, plus `label` and `assetBase`). It must stay synchronous and first.
- **Frontend API** — `src/services/host/` (`invoke.ts`, `event.ts`, `window.ts`, `shell.ts`). **Never touch `window.__nori` directly from components.**
- **Host side** — `Bridge/NoriBridge.cs` does dispatch/correlation, `Bridge/BridgeCommands.cs` holds the handlers, `Bridge/AppServices.cs` is the service container.

Envelopes are double-encoded: the host serializes the JSON envelope, then serializes *that string* into the `InvokeScript` call, and JS `JSON.parse`s it back. This is deliberate — it makes escaping bugs impossible.

**Every command must be registered in `BridgeCommands.InvokeAsync`'s switch** — an unregistered command compiles fine and fails only at runtime with `未知的命令`. Commands throw on failure; the message is user-facing Chinese text and becomes the frontend's rejection.

Privileged commands allowlist their caller by window label — `complete_first_run` rejects anything but a visible `first-run` webview. Follow that pattern for anything state-changing.

> 对话迁移阶段：`window_open_chat` 打开独立原生 `ChatWindow`（深色、缓存窗口上下文）；`main` 继续承接音频宿主与 `OperationDrawer`，`PluginWidgets` 与其他聊天入口保持在主界面/首页，Vue 其余页面先不移除。

Host→frontend events:

| Event | Emitted by | Consumed by |
|---|---|---|
| `nori:config-changed` | every `set_config` | Audio host WebView — hot-applies display config |
| `nori:play-motion` | `chat_completion` | Native chat windows |
| `nori:audio-play` / `nori:audio-stop` | `WebViewAudioPlayback` | `services/audio/` in audio host |
| `nori:audio-record-start` / `-stop` | `WebViewMicrophoneRecorder` | `services/audio/` in audio host |

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

`nori.db` lives in `<PackageRoot>/data/core/database/` with two tables: `config(key TEXT PRIMARY KEY, value TEXT)` and `chat_messages`. Everything is stored as TEXT. Package data is never redirected to system AppData; `<PackageRoot>` must remain writable and the complete package must be movable.

The trap: `ConfigValue.FromStorage` **re-infers the type on read**. `"1"`/`"true"` → Boolean, digit strings → Integer, `{…}`/`[…]` → Json, everything else → String. A config you wrote as a string comes back as a number if it happens to look like one, so `invoke<string | null>("get_config", …)` is a lie for numeric-looking values. 原生模型读取同样必须兼容这些推断类型。 `"1.25"` stays a String (i64 parse fails, not a JSON container) — `Nori.Core.Tests` pins all of this.

`set_config` broadcasts `nori:config-changed` app-wide, which is how the pet window live-updates. Schema evolution goes through `config_schema_version` + `MigrateSchema()`; a DB newer than the binary is rejected outright.

Secrets (`*_api_key`, `*_secret`, `*_token`, `*_password`) are encrypted with **AES-256-GCM** (`nsec2:` prefix) — no longer raw DPAPI. The master key lives in the platform keystore (`SecretKeyStore`): DPAPI-wrapped file on Windows, Keychain on macOS, libsecret on Linux, 0600 file as fallback. Old `enc:dpapi:` values still decrypt **on Windows only**; elsewhere `ConfigStore.IsUnreadableSecret` flags them so the UI asks the user to re-enter that one key — never silently wiping other config.

Live2D display settings are stored **per model** as `<base>_<modelId>` (e.g. `l2d_scale_arg-nori`) with fallback to the legacy global key — see `PetRuntime` and `BridgeCommands.model_get_meta`. Keep both lookups when adding keys.

### Resource management (local only)

Models are local resources under `<PackageRoot>/data/resources/installed/live2d/<name>/`; there is no remote download or gateway. Runtime data is strictly under `<PackageRoot>/data`, which must be writable and movable as a whole package. The frontend calls `check_resource` to test install state and `import_local_resource` to add models from a local ZIP or folder. `ResourceManager` in `Nori.Core/Resources/` covers check/list/delete/import, and Live2D resources count as installed only when they contain a `.model3.json`.

`ZipExtractor` is hardened — rejects absolute paths, UNC, drive letters, `..`, control chars and symlink entries, and re-canonicalizes each parent against the target. It also strips a single common top-level directory. Don't loosen it.

### Live2D: 原生 OpenGL 桌宠与独立模型预览

Desktop Pet rendering is implemented natively in Avalonia (`PetWindow.cs` + `PetGlControl.cs`) using `Live2DCSharpSDK` and Cubism Native Core:
- Direct OpenGL ES 2.0 rendering in a transparent Avalonia window (`OpenGlControlBase`).
- High-quality 2048x2048 clipping mask buffer, 16x anisotropic filtering, and high precision mask enabled.
- Alpha mask sampling (~10Hz) derives one contiguous bounding rectangle around the visible model, preventing fragmented head/body hit regions. **Per platform**: Windows queries that rectangle from a `WM_NCHITTEST` hook; Linux/X11 sends it to `XShapeCombineRectangles(ShapeInput)`; macOS toggles `setIgnoresMouseEvents:` based on whether the cursor is inside it. Wayland has neither — `supportsHitThrough` goes false and the pet degrades to a fully clickable window.
- 1:1 C# behavioral pipeline (`AutoBlink`, `EyeFocus`, `IdleDisable`, `BeatSync`, `LipSync`, `ExpressionStore`, `ExpressionBehavior`). Lip-sync amplitude now comes from the frontend's `audio_level` reports, not NAudio.
- Native mouse drag (4px threshold + position persistence), tap actions/expressions, global cursor tracking, and deep sea glow themed context menu.
- 模型预览位于仅深色的独立 Avalonia `ModelsWindow`，使用独立 `ModelPreviewControl` / `PetRuntime` 与 `PetGlControl`。预览不改写当前桌宠、不接入 AI 或音频；SDK 全局状态串行访问并按渲染实例计数，模型纹理独立持有。

### Local model management

模型管理位于 `Nori.Desktop/Windows/ModelsWindow*.cs`：导入本地 Live2D ZIP/文件夹、启用模型、调整每模型显示、全局行为和互动区域，并进行隔离的原生预览。入口为 `RUNTIME.openModels()` → `window_open_models`，独立 `models` 来源只允许固定模型领域命令。关闭先保存再隐藏，失败保留草稿。`src/services/live2d/models.ts` 与缩略图仍供首次运行和主页复用；浏览器 Pixi/Cubism 预览已移除。功能与验证清单见 `docs/native-models-parity.md`。

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
- Debounce config writes (~400 ms) with **each field using its own timer** (a shared timer silently drops the earlier field's value) and flush pending writes on unmount. Synchronize snapshot-backed fields without clobbering what the user is typing.
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
