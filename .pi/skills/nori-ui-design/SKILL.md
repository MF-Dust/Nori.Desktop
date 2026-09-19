---
name: nori-ui-design
description: >
  Design, review, and refine the Nori Desktop Pet Vue frontend. Use when modifying
  UI layouts, visual hierarchy, spacing, typography, interaction design, settings,
  chat UI, model management, onboarding, or other frontend presentation under
  app/desktop. Preserve Nori's deep-ocean, character-centered identity and avoid
  generic SaaS/dashboard aesthetics.
---

# Nori UI Design

Inspect the target component first. Consult the relevant sections of `docs/规范.md` for affected styling conventions; inspect tokens, UnoCSS shortcuts, theme overrides, and `App*` components when the change uses them. Read only the reference needed for the current layout, interaction, or motion decision. Do not load `CLAUDE.md` or all references as a fixed prerequisite.

Repository rules override this skill.

## Product direction

Nori is a character-centered desktop companion application.

The interface should feel calm, intimate, futuristic, deep-ocean inspired,
slightly mysterious, polished, and character-first.

It must not resemble a generic SaaS admin dashboard.

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

Follow `AGENTS.md` → Local Verification. For visual changes, inspect the affected interface; do not run unrelated backend checks for a frontend-only change.
