# Changelog

## 0.2.1 - 2026-02-09
- Optimized first open (`F5`) stutter with staged first-frame rendering and earlier menu warmup.
- Reworked list virtualization to preserve the existing two-column UI skeleton and core interactions.
- Fixed scrolling correctness issues:
  - Correct content height and bounds clamping.
  - Proper bottom/top scroll response and visible item window updates.
- Reduced scroll-time hitches by throttling window updates and avoiding redundant UI property writes.
- Paused heavy background warmups while the browser is open to prioritize interaction smoothness.

## 0.2.0 - 2026-02-07
- Reworked browser UI layout, spacing, scrolling, and close button behavior.
- Added two-level category filtering (`All / Food / Weapon` + sub-categories).
- Improved icon handling and hidden/invalid item filtering.
- Added dynamic language refresh when opening UI.
- Unified localization flow:
  - Item names from `ItemNameKeyMap.json` + game localization keys.
  - Mod-specific UI texts from `Localized_Text.json`.

## 0.1.0 - 2026-02-06
- Initial release with search + categorized item browser UI.
