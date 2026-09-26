---
name: nori-ui-design
description: >
  Design, review, and refine Nori Desktop native Avalonia UI. Use when modifying
  settings, chat, memory, models, onboarding, pet chrome, or other native window
  presentation under Nori.Desktop. Preserve Nori's deep-ocean, character-centered
  identity and avoid generic SaaS/dashboard aesthetics.
---

# Nori Native UI Design

Inspect the target window or page first. Consult `docs/规范.md` for affected conventions. Read only the reference needed for the current layout, interaction, or motion decision.

Repository rules override this skill. Authoritative dev guide: `AGENTS.md`.

## Product direction

Nori is a character-centered desktop companion application.

The interface should feel calm, intimate, futuristic, deep-ocean inspired,
slightly mysterious, polished, and character-first.

It must not resemble a generic SaaS admin dashboard.

## Design token pipeline

- **Source of truth**: `app/desktop/src/assets/style/tokens.ts` (`COLORS`, `SPACING`, `RADIUS`, `FONT_SIZES`).
- **Sync**: `pnpm theme:sync` / `pnpm theme:check` via `app/desktop/scripts/sync-design-tokens.mjs`.
- **Generated native palette**: `Nori.Desktop/Appearance/NoriThemeTokens.g.cs` — use `NoriThemeTokens.Brush("…")` / `Color("…")`.
- **Settings-specific resources**: `Nori.Desktop/Settings/SettingsTheme.axaml` loaded through `SettingsBrushes` / `SettingsBrushPalette`.
- **Shared chrome**: `NativeWindowChrome` uses `NoriThemeTokens` for title bar, borders, and traffic-light buttons.
- **Contrast gate**: `app/desktop/tests/theme/contrast.test.ts` and `native-tokens-sync.test.ts`.

Do not introduce bare hex outside `tokens.ts`. Rem measures in tokens use the 62.5% root scale (1 rem = 10 logical px in generated measures).

## Priorities

1. Nori / current character
2. user's immediate action
3. conversation and interaction
4. important application state
5. configuration and technical details

## References

- `references/nori-visual-language.md`
- `references/component-usage.md`
- `references/desktop-app-patterns.md`
- `references/motion-and-glow.md`
- `references/nori-review-checklist.md`

## Verification

Follow `AGENTS.md` → Local Verification. For visual changes, inspect the affected native window at 720×480 and 1080p; cover Chinese and English resource paths for settings-style pages.
