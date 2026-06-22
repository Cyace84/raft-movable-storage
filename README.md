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

**Scope:** works in **single-player** and in **multiplayer when you are the host** (others see the
chest move, contents and all). Moving as a non-host client is not supported yet — see below.

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
| Recreate (networked) | host: `BlockCreator.CreateBlockCheat(item, pos, rot, dps, -1)` |
| Restore contents | `newStorage.GetInventoryReference().SetSlotsFromRGD(slots)` |
| Sync contents to clients | RPC `Message_Storage_Close(StorageManager_Close, player.StorageManager, newStorage)` to `Target.Other` |

`CreateBlockCheat` mints authoritative unique object indices (`SaveAndLoad.GetUniqueObjectIndex()`)
and RPCs a `Message_BlockCreator_PlaceBlock` to other clients, then creates locally — so the move
replicates. (Plain `CreateBlock(...,0,0,0)` is local-only: remote players just see the original
vanish.) The PlaceBlock RPC carries only geometry, so the replicated chest is empty until we push
contents via `Message_Storage_Close` — the same message vanilla uses on chest close (its ctor grabs
`GetRGDSlots()`; the receiver applies `SetSlotsFromRGD`).

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
**Working — user-verified**, including **multiplayer** (host moves a chest → the co-op partner sees
it relocate with its contents intact; partner needs no mod). Single-player and host MP both confirmed
live; no dupe, no resource refund, contents preserved.

### Not done yet
- [ ] **Moving as a non-host client**: `CreateBlockCheat` mints host-authoritative indices, so it is
      gated to the host (in SP you are the host). A client mover would need the `SendP2P` request
      path to the host instead. Workaround: host the session.
- [ ] Generalize beyond `Storage_Small` (works for all chest sizes that are `Storage_Small`-based;
      verify large/special content blocks if desired).
