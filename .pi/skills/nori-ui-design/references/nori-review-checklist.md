# Nori Native UI Review Checklist

Before finishing a native UI change, verify:

- Is Nori or the current character still the visual focus?
- Is the primary user action obvious?
- Does the screen feel like a desktop companion app rather than SaaS software?
- Were unnecessary bordered panels removed?
- Are status badges limited to useful state?
- Is glow reserved for meaningful emphasis?
- Are gradients restrained?
- Are technical details visually subordinate?
- Were `tokens.ts` keys and generated `NoriThemeTokens` / `SettingsTheme` resources reused?
- Are keyboard focus states preserved on settings controls?
- Are loading, empty, disabled, and error states still clear?
- Did `pnpm theme:check` pass after token edits?
- Does the result still feel specifically like Nori?
