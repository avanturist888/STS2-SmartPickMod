# SmartPick — Developer Guide

## What is this?

A Slay the Spire 2 mod that shows tier badges (S/A/B/C/D) on cards and relics. Built with C# on Godot 4.5, uses HarmonyLib for runtime patching.

## Tech Stack

- **Godot 4.5** (game engine) — NOT Unity
- **.NET 9.0**, C#, x64
- **HarmonyLib** — runtime method patching of game DLLs
- **GodotSharp.dll** — C# bindings to Godot API
- **sts2.dll** — main game assembly

## Build & Run

```bash
dotnet build SmartPick.csproj
```

This copies DLL/PCK/JSON to `Slay the Spire 2/mods/`. Enable mod in Settings > Modding > Restart.

If build fails with "file is being used" — close the game first.

### Local config

Create `local.props` if game/Godot paths differ:
```xml
<Project>
  <PropertyGroup>
    <STS2GamePath>D:\Games\Slay the Spire 2</STS2GamePath>
    <GodotExePath>C:\Path\To\Godot_v4.5-stable_mono_win64.exe</GodotExePath>
  </PropertyGroup>
</Project>
```

## Decompiling the Game

We decompile `sts2.dll` to discover game APIs. This is the primary way to find class names, methods, and properties.

```bash
# Install ilspycmd if not installed
dotnet tool install -g ilspycmd

# Decompile (run from bash)
"$HOME/.dotnet/tools/ilspycmd.exe" "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64/sts2.dll" -p -o /tmp/sts2_decompiled
```

Output goes to `/c/Users/<user>/AppData/Local/Temp/sts2_decompiled/`. Key namespaces:
- `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection` — card selection screens
- `MegaCrit.Sts2.Core.Nodes.Screens.Shops` — merchant
- `MegaCrit.Sts2.Core.Nodes.Cards` — NCard, NCardGrid
- `MegaCrit.Sts2.Core.Nodes.Cards.Holders` — NCardHolder, NGridCardHolder
- `MegaCrit.Sts2.Core.Nodes.Relics` — NRelic, NRelicBasicHolder
- `MegaCrit.Sts2.Core.Nodes.Screens.Overlays` — NOverlayStack, IOverlayScreen
- `MegaCrit.Sts2.Core.Models` — CardModel, RelicModel, ModelDb

## Key Game Architecture

### Screen Types

The game has several screen hierarchies — they do NOT share a common base:

| Screen | Base Class | Used For |
|--------|-----------|----------|
| `NCardRewardSelectionScreen` | `Control, IOverlayScreen` | Post-combat card rewards |
| `NCardGridSelectionScreen` | `Control, IOverlayScreen` | Forge, transform, enchant, remove |
| `NChooseACardSelectionScreen` | `Control, IOverlayScreen` | Choose 1 of 3 cards |
| `NChooseABundleSelectionScreen` | `Control, IOverlayScreen` | Bundle/pack selection |
| `NCardsViewScreen` | `Control` (capstone) | Deck view |
| `NMerchantInventory` | `Control` | Merchant shop |
| `NCardLibrary` | `NSubmenu` | Card compendium |
| `NChooseARelicSelection` | `Control, IOverlayScreen` | Relic reward |
| `NRelicCollection` | `NSubmenu` | Relic compendium |

### Card Node Hierarchy

- `NCardHolder` — base class for card containers
  - `NGridCardHolder` — card in a grid (reward, deck, library)
  - `NPreviewCardHolder` — card in preview (bundle preview)
- `NCard` — the visual card node. `NCard.Create(model)` from pool
- `NMerchantCard` — merchant shop card (separate hierarchy, uses reflection)

### Relic Node Hierarchy

- `NRelic` — visual relic icon. `NRelic.Create(model, IconSize)`
- `NRelicBasicHolder` — relic in reward screen (has `.Relic.Model`)
- `NMerchantRelic` — relic in merchant (private `_relic` field, needs reflection)
- `NRelicCollectionEntry` — relic in compendium (public `relic` field)
- `NTreasureRoomRelicHolder` — relic in treasure chest (has `.Relic.Model`)

### Getting Card/Relic Identity

```csharp
// Cards
NCardHolder holder → holder.CardModel.Id.Entry  // e.g. "OFFERING"
CardModel → .Pool.Title                          // e.g. "ironclad" (character)
CardModel → .Title                               // localized display name

// Relics
NRelicBasicHolder → .Relic.Model.Id.Entry        // e.g. "ANCHOR"
RelicModel → .Title.GetFormattedText()            // localized name
RelicModel → .Description.GetFormattedText()      // localized description
```

### ModelDb — All Cards/Relics

```csharp
ModelDb.AllCards          // IEnumerable<CardModel> — all cards in game
ModelDb.AllRelics         // IEnumerable<RelicModel> — all relics
ModelDb.AllCharacters     // IEnumerable<CharacterModel> — 5 characters
ModelDb.AllCardPools      // card pools per character
```

**Important**: `ModelDb.AllCards` returns **canonical (immutable)** instances. To use with `NCard.Create`, you may need `card.MutableClone()`.

## Godot Quirks for Mods

### Virtual methods don't work without source generators

Godot doesn't call `_Ready()`, `_Input()`, `_GetDragData()`, `_CanDropData()` etc. on C# classes created via `new()` unless Godot source generators are active. We include `Godot.SourceGenerators.dll` as an analyzer:

```xml
<Analyzer Include="build\analyzers\Godot.SourceGenerators.dll" />
<CompilerVisibleProperty Include="GodotProjectDir" />
```

The DLL is extracted from `Godot_v4.5-stable_mono_win64/GodotSharp/Tools/nupkgs/Godot.SourceGenerators.4.5.0.nupkg`.

### _Ready() not called on dynamically created nodes

Even with source generators, `_Ready()` may not fire reliably on nodes created via `new()` and added to the tree. Solution: call `BuildUI()` manually after `AddChild()`.

### Positioning issues with layout containers

NButton, PanelContainer etc. may center children, overriding `Position`. Solutions:
- Use `TopLevel = true` for absolute positioning (but loses z-order)
- Add to a parent that doesn't manage layout
- Use `FindRelicIconParent()` pattern — find the actual visual node to attach to

### NGridCardHolder origin is at card CENTER

Size is (0,0), visual extends +-150x211 from origin. Account for this when positioning nearby UI.

### Game hover tips

Use `NHoverTipSet.CreateAndShow(ownerControl, model.HoverTips)` for native tooltips. They render in `NGame.HoverTipsContainer` — ensure your UI is added BEFORE it in the tree so tips show on top:

```csharp
NGame.Instance.AddChild(screen);
NGame.Instance.MoveChild(screen, hoverContainer.GetIndex());
```

## Project Structure

```
src/
  ModEntry.cs              — Entry point, Harmony.PatchAll()
  TierData.cs              — Card tier loading, lookup, custom tiers
  RelicTierData.cs         — Relic tier loading, lookup, custom tiers
  CardBadgeOverlay.cs      — Attaches badge UI to card nodes
  RelicBadgeOverlay.cs     — Attaches badge UI to relic nodes
  CombatExporter.cs        — Character resolution, legacy export
  RewardExporter.cs        — Legacy reward export
  Patches/
    CardScreenPatches.cs   — Harmony patches for all card screens
    RelicScreenPatches.cs  — Harmony patches for relic screens
    TierEditorPatches.cs   — F7 hotkey listener
    *ExportPatches.cs      — Legacy JSON export patches
  UI/
    TierEditorScreen.cs    — In-game tier editor (F7)
    TierRow.cs             — Tier row with drop target
    TierItemIcon.cs        — Draggable card/relic icon
data/tier_lists/
  mobalytics_cards.json    — Card tiers per character (embedded)
  mobalytics_relics.json   — Relic tiers (embedded)
build/analyzers/
  Godot.SourceGenerators.dll — Enables Godot virtual methods
pack/
  export_presets.cfg       — Godot PCK export config
workshop/
  workshop_item.vdf        — Steam Workshop descriptor
  prepare.sh               — Build script for Workshop
  preview.png              — Workshop preview image
```

## Harmony Patching Patterns

### Basic postfix patch
```csharp
[HarmonyPatch(typeof(SomeClass), nameof(SomeClass.SomeMethod))]
public static class MyPatch
{
    [HarmonyPostfix]
    public static void AfterMethod(SomeClass __instance) { ... }
}
```

### Gotchas

- **PatchAll() fails silently** if ANY patch target doesn't exist. Wrap in try/catch and log.
- **Ambiguous match** — if method has overloads, specify parameter types:
  ```csharp
  [HarmonyPatch(typeof(Foo), "Bar", new[] { typeof(Func<CardModel, bool>) })]
  ```
- **Two patches on same method** works fine (both postfixes fire).
- **Private methods** can be patched by string name: `[HarmonyPatch(typeof(Foo), "PrivateMethod")]`

## Tier Data Flow

1. **Built-in tiers** loaded from embedded JSON at mod init
2. **Custom tiers** loaded from `%AppData%/SlayTheSpire2/smartpick_custom_tiers.json`
3. **Lookup priority**: custom ordered > Mobalytics (character list, then "Colorless" list for multiplayer cards)
4. **Custom tiers use position-based scoring**: first in S=100, last in S=85, linearly interpolated
5. **Badge display**: `CardBadgeOverlay.AttachBadgesDeferred(screenNode)` scans tree, creates badge UI

## Logs

Game logs: `%AppData%/Roaming/SlayTheSpire2/logs/godot.log`

Filter mod logs: `grep "SmartPick" godot.log`

## Common Tasks

### Add badges to a new screen

1. Decompile and find the screen class
2. Find its lifecycle method (AfterOverlayOpened, OnSubmenuOpened, etc.)
3. Add Harmony postfix patch calling `CardBadgeOverlay.AttachBadgesDeferred(__instance)`
4. Add cleanup patch on close calling `CardBadgeOverlay.ClearBadges()`

### Add a new tier source

1. Create JSON in `data/tier_lists/`
2. Add `<EmbeddedResource>` in `.csproj`
3. Load in `TierData.Initialize()` or `RelicTierData.Initialize()`

### Release new version

```bash
bash workshop/prepare.sh
cd workshop/content
powershell -Command "Compress-Archive -Path SmartPick.dll,SmartPick.pck,SmartPick.json -DestinationPath ../SmartPick-vX.Y.Z.zip -Force"
```

Upload zip to GitHub Releases and Nexus Mods.
