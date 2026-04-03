# Changelog

## 0.2.4 - 2026-04-03
- Added support for redirecting spawned items to the currently observed teammate while dead or in ghost/spectate mode.

## 0.2.3 - 2026-03-29
- Unified offline and multiplayer item spawning through PEAK's built-in `GameUtils.InstantiateAndGrab` flow.
- Added the `ItemBrowser.Spawn <Item>` in-game console command for use with mods such as `Console Unlocker`.
- Documented that PEAK's current console command matching is case-sensitive, so command names must be entered exactly.
- Simplified internal fallback logic:
  - Removed the old room-position spawn fallback.
  - Reduced icon/category fallback complexity to keep the browser codebase easier to maintain.

## 0.2.2 - 2026-03-20
- Fixed multiplayer item spawning for non-host players.
- Switched online spawning to PEAK's built-in GameUtils.InstantiateAndGrab network flow.
- Removed the requirement for the host to install ItemBrowser for client spawn requests to succeed.
- Added extra spawn diagnostics to verify multiplayer runtime state and native spawn path selection.

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
