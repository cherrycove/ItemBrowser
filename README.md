# ItemBrowser

An in-game item browser for PEAK using PEAKLib.UI.

## Features
- Press F4 to open/close the browser.
- Search by localized name or prefab name.
- Categories: Consumable, Cookable, Weapon, Tool, Misc.
- Click an entry to spawn it in front of you.

## Usage
- Launch the game with BepInEx.
- Press F4 in a match to open the browser.

## Build
```sh
dotnet build -c Release
```

The Thunderstore package will be generated in `artifacts/thunderstore/` when running the Release build with the tcli tool.
