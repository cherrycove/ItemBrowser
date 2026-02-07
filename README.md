# ItemBrowser

An in-game item browser and spawner for PEAK, built with PEAKLib.UI.

![ItemBrowser UI Preview (ZH)](https://raw.githubusercontent.com/cherrycove/ItemBrowser/main/readme-preview-zh.png)

![ItemBrowser UI Preview (EN)](https://raw.githubusercontent.com/cherrycove/ItemBrowser/main/readme-preview.png)

## Features
- Press `F5` to open/close the browser.
- Search by localized item name or prefab name.
- Two-level filtering: `All / Food / Weapon`, with sub-categories for Food and Weapon.
- Click an entry to spawn it in front of your character.
- Multilingual UI support:
  - Item names are resolved from game localization keys via `ItemNameKeyMap.json`.
  - Mod-specific texts are loaded from `Localized_Text.json` and injected into `LocalizedText.MAIN_TABLE`.
  - UI labels refresh to the current game language when opening the browser.

## Usage
- Launch PEAK with BepInEx.
- Enter a match, then press `F5` to open ItemBrowser.

## Build
```sh
dotnet build -c Release
```

Thunderstore package output is generated under `artifacts/thunderstore/` when building Release with tcli.
