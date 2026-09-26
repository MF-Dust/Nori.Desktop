# Native Component Usage

Reuse existing Nori Avalonia primitives before creating new controls.

## Theme access

- Prefer `NoriThemeTokens.Brush("token-key")` for shared colors across chat, pet menus, and chrome.
- Settings pages: `SettingsBrushes.Resolve(owner, "bg-card")` or a `SettingsBrushPalette` on long-lived presenters.
- Load control templates from `SettingsTheme.axaml` (buttons, inputs, toggles, popups) instead of duplicating styles.

## Layout patterns

- **Settings / Memory / Models**: side navigation + scrollable sections; card grouping via `SettingsSectionViewModel` patterns in `Nori.Desktop/Settings`.
- **Chat / Quick Chat**: `ChatTheme.axaml`, bubble templates, and markdown colors from `ChatMarkdown` token keys.
- **Window chrome**: `NativeWindowChrome` for draggable title bar; close goes through window save/hide flow.

Do not introduce:

- a second color system parallel to `tokens.ts`
- hard-coded `#RRGGBB` in C# or AXAML
- web-style atomic CSS or Vue component libraries

## Cards and grouping

Before adding a bordered panel, ask whether spacing and alignment are enough.
Avoid nested cards unless they represent a real hierarchy boundary.

## State chips and badges

Use compact text or subtle badges for scannable state.
Do not turn ordinary metadata into decorative chips.

## Dialogs

Reuse `NativeSettingsDialogs` / shared confirmation helpers instead of one-off `Window` subclasses.
