# Movable Storages

A BepInEx 5 (Mono) plugin for **Raft** that lets you pick up a placed storage chest — **with its
contents** — and drop it somewhere else, using the vanilla build-placement ghost as the preview.

## Usage
1. Aim at a storage chest.
2. Press the hotkey (default **M**) → the chest enters "carry" mode; the vanilla build ghost
   follows your cursor showing where it will land.
3. **Left-click** to drop it at the new spot. The full inventory comes with it.
4. Press the hotkey again, or **right-click**, to cancel.

Hotkey is configurable in `BepInEx/config/com.cyace.raftmovablestorage.cfg` (generated on first run).

**Scope:** works on **host / single-player**. Multiplayer (placing for remote clients) is not yet
supported — see below.

## Build
```bash
DOTNET_ROOT=$HOME/.dotnet dotnet build -c Release
# override the game path if the bottle moves:
#   dotnet build -c Release -p:GameDir="/path/to/Raft"
```
Output: `bin/Release/RaftMovableStorage.dll` → copy into `<Raft>/BepInEx/plugins/`.

## How it works (recon basis)
All API verified by decompiling the game's `Assembly-CSharp.dll` (`DOTNET_ROOT=$HOME/.dotnet ilspycmd <dll> -t <Type>`):

| Step | Call |
|------|------|
| Capture contents | `Storage_Small.GetInventoryReference().GetRGDSlots()` → `RGD_Slot[]` |
| Hide original | disable its `Renderer.enabled` + `Collider.enabled` (NOT `SetActive`) |
| Show ghost | `BlockCreator.SetBlockTypeToBuild(Item_Base)` (positions `selectedBlock`) |
| Remove original | `BlockCreator.RemoveBlockNetwork(block, **null**, true)` (static, networked) |
| Recreate | `BlockCreator.CreateBlock(item, pos, rot, dps, -1, false, 0,0,0)` |
| Restore contents | `newStorage.GetInventoryReference().SetSlotsFromRGD(slots)` |

`CreateBlock` self-generates the `0` indices and calls `OnFinishedPlacement()` synchronously, so the
new storage's inventory exists immediately after the call returns.

### Three hard-won gotchas (all cost a dupe/refund bug)
1. **Don't hide the original with `SetActive(false)`** — that de-registers the block, so the later
   `RemoveBlockNetwork` silently no-ops and you get a duplicate. Disable `Renderer`/`Collider` only.
2. **Remove with a `null` player.** `RemoveBlockCoroutine` refunds to the player only inside a branch
   gated on `playerRemovingBlock != null` (gives the chest item back, or — if `itemToReturnOnDestroy`
   is null — 50 % of the recipe materials). Passing `null` skips the whole branch; the block is still
   destroyed (`DestroyImmediate` is unconditional and the path is null-safe). No item, no recipe, no
   contents leak.
3. **No Harmony, own ticker.** In this BepInEx 5 + Wine/CrossOver setup `BaseUnityPlugin.Update()` is
   not pumped and the plugin component is `Destroy`ed seconds after `Awake`. All per-frame logic runs
   on a `DontDestroyOnLoad` `Ticker` GameObject; nothing depends on the plugin instance surviving.

Prior art (`Aidanamite/Building-Utilities`) had a move feature disabled because it copied
`GetBlockCreationData()` (`RGD_Block`, geometry only — no items). The fix is the inventory round-trip
(`GetRGDSlots` / `SetSlotsFromRGD`), which `RGD_Storage` itself uses for save/load.

## Status
**Working — user-verified** (multiple chests, contents intact, no dupe, no resource refund).

### Not done yet
- [ ] **Multiplayer**: `CreateBlock(replicating:false)` + `SetSlotsFromRGD` are local-only, so remote
      clients won't see the moved chest (or would see it empty). Needs a networked place
      (`Message_BlockCreator_PlaceBlock`) + storage-content sync. Host/SP is unaffected.
- [ ] Generalize beyond `Storage_Small` (works for all chest sizes that are `Storage_Small`-based;
      verify large/special content blocks if desired).
