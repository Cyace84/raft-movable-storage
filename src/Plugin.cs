using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RaftMovableStorage
{
    // Move a placed storage chest (with its contents) to a new spot, using the vanilla build
    // placement ghost as the preview. Press the hotkey while aiming at a storage to pick it up
    // into placement mode (ghost follows the cursor); left-click drops it at the new location with
    // its inventory intact; press the hotkey again (or right-click) to cancel.
    //
    // ARCHITECTURE NOTE (learned from runtime diagnostics in this BepInEx 5 + Wine/CrossOver setup):
    //   * BaseUnityPlugin.Update() is NOT pumped here, and the plugin component gets Destroyed a few
    //     seconds after Awake (which would also rip any Harmony patches via UnpatchSelf).
    //   * Therefore ALL per-frame logic lives on our own DontDestroyOnLoad `Ticker` GameObject, and we
    //     use NO Harmony at all. Placement is handled by reading the vanilla ghost (BlockCreator.selectedBlock)
    //     directly on left-click. Vanilla cannot double-place the chest because the storage item is not in
    //     the player's inventory while it is being carried.
    //
    // Recon basis (verified by decompiling Assembly-CSharp.dll):
    //   Storage_Small : Block          GetInventoryReference() : Inventory
    //   Inventory.GetRGDSlots() : RGD_Slot[]   /   Inventory.SetSlotsFromRGD(RGD_Slot[])
    //   BlockCreator.SetBlockTypeToBuild(Item_Base)  -> shows ghost (selectedBlock follows cursor)
    //   BlockCreator.CreateBlock(item, localPos, localRot, dps, -1, false, 0,0,0)  (self-fills indices,
    //       calls OnFinishedPlacement() synchronously so the new storage's inventory exists on return)
    //   BlockCreator.RemoveBlockNetwork(block, player, updateRaftBounds)  (static, networked)
    //
    // SCOPE: host / single-player. Multiplayer needs a networked place (Message_BlockCreator_PlaceBlock)
    // plus storage-content sync; CreateBlock(replicating:false) + SetSlotsFromRGD are local-only. TODO.

    [BepInPlugin(Guid, "Movable Storages", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.cyace.raftmovablestorage";

        public static ConfigEntry<KeyboardShortcut> MoveKey;
        public static ManualLogSource Log;

        // carry-mode state
        internal static Storage_Small moving;
        internal static Item_Base movingItem;
        internal static DPS movingDps;
        internal static RGD_Slot[] movingSlots;
        // renderers/colliders we disabled to hide the original while carrying (restored on cancel)
        private static readonly List<Collider> _hiddenColliders = new List<Collider>();
        private static readonly List<Renderer> _hiddenRenderers = new List<Renderer>();

        private void Awake()
        {
            Log = Logger;
            MoveKey = Config.Bind(
                "General",
                "MoveStorageKey",
                new KeyboardShortcut(KeyCode.M),
                "Aim at a storage and press this to pick it up (with its contents) into placement mode. " +
                "Left-click to drop it at the new spot. Press the key again or right-click to cancel.");

            // Own DontDestroyOnLoad ticker: BaseUnityPlugin.Update is not pumped in this env and the
            // plugin component gets destroyed shortly after Awake.
            var go = new GameObject("RMS_Ticker");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Ticker>();

            Note($"Movable Storages 1.0.0 loaded. Move key = {MoveKey.Value}.");
        }

        // Info = user-facing milestones; Trace = diagnostic non-events (missed raycast, bad spot).
        internal static void Note(string msg) => Log?.LogInfo(msg);
        internal static void Trace(string msg) => Log?.LogDebug(msg);

        // Per-frame logic, driven by Ticker.
        internal static void Tick()
        {
            if (MoveKey.Value.IsDown() || Input.GetKeyDown(KeyCode.M))
            {
                if (moving != null) { Trace("hotkey: cancel carry."); CancelMove(); }
                else TryBeginMove();
                return;
            }

            if (moving == null) return;

            // carrying: right-click cancels, left-click confirms placement at the ghost
            if (Input.GetMouseButtonDown(1)) { Trace("rmb: cancel carry."); CancelMove(); return; }
            if (Input.GetMouseButtonDown(0))
            {
                var bc = ComponentManager<Network_Player>.Value?.BlockCreator;
                if (bc != null) ConfirmMove(bc);
            }
        }

        private static void TryBeginMove()
        {
            var player = ComponentManager<Network_Player>.Value;
            if (player == null) { Trace("begin: no local Network_Player."); return; }

            var cam = Camera.main;
            if (cam == null) { Trace("begin: Camera.main is null."); return; }

            if (!Physics.Raycast(cam.transform.position, cam.transform.forward,
                    out var hit, Player.UseDistance * 2f, LayerMasks.MASK_Block))
            { Trace("begin: raycast hit nothing on MASK_Block."); return; }

            var storage = hit.collider.GetComponentInParent<Storage_Small>();
            if (storage == null)
            { Trace($"begin: hit '{hit.collider.name}' but no Storage_Small in parents."); return; }
            if (storage.buildableItem == null)
            { Trace("begin: storage has null buildableItem."); return; }

            var inv = storage.GetInventoryReference();
            movingSlots = inv != null ? inv.GetRGDSlots() : null;
            movingItem = storage.buildableItem;
            movingDps = storage.dpsType;
            moving = storage;

            // BUG1: hide the original while carrying so its mesh+collider don't block placing the
            // ghost on/near its own spot. NOTE: do NOT SetActive(false) the GameObject — that
            // de-registers the block and makes RemoveBlockNetwork silently fail (proven via eval:
            // it caused the duplicate). Disabling only Renderers+Colliders keeps registration intact.
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            foreach (var c in storage.GetComponentsInChildren<Collider>())
                if (c.enabled) { c.enabled = false; _hiddenColliders.Add(c); }
            foreach (var r in storage.GetComponentsInChildren<Renderer>())
                if (r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            // make sure the build creator is active so the ghost shows, then engage it
            var bc = player.BlockCreator;
            if (bc != null && !bc.gameObject.activeSelf) bc.gameObject.SetActive(true);
            bc?.SetBlockTypeToBuild(movingItem);

            Note($"Carrying '{movingItem.UniqueName}' ({(movingSlots?.Length ?? 0)} slots). LMB to place.");
        }

        internal static void CancelMove()
        {
            if (moving == null) return;
            // restore the hidden original (BUG1)
            RestoreHidden();
            ExitBuildMode();
            moving = null;
            movingItem = null;
            movingSlots = null;
        }

        private static void RestoreHidden()
        {
            foreach (var c in _hiddenColliders) if (c != null) c.enabled = true;
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
        }

        // Fully leave build mode so the player can't keep placing extra copies (BUG3).
        private static void ExitBuildMode()
        {
            var bc = ComponentManager<Network_Player>.Value?.BlockCreator;
            if (bc == null) return;
            bc.SetGhostBlockVisibility(false);
            bc.gameObject.SetActive(false);
        }

        // Read the vanilla ghost, remove the original, recreate at the new spot, restore inventory.
        internal static void ConfirmMove(BlockCreator bc)
        {
            var ghost = bc.selectedBlock;
            if (ghost == null) { Trace("place: no ghost (selectedBlock null) - aim at a buildable surface."); return; }

            var err = Traverse.Create(bc).Method("CanBuildBlock", new object[] { ghost }).GetValue<BuildError>();
            if (err != BuildError.None) { Trace($"place: invalid spot ({err})."); return; }

            var pos = ghost.transform.localPosition;
            var rot = ghost.transform.localEulerAngles;
            var item = movingItem;
            var dps = movingDps;
            var slots = movingSlots;
            var original = moving;

            // The original is about to be destroyed; drop the hide-bookkeeping (nothing to restore).
            _hiddenColliders.Clear();
            _hiddenRenderers.Clear();
            // DUPE/REFUND FIX: RemoveBlockCoroutine only refunds (chest item, contents, OR — when
            // itemToReturnOnDestroy is null — 50% of the recipe materials) inside a branch gated on
            // `playerRemovingBlock != null`. Passing NULL skips that branch entirely: no item, no
            // recipe, no contents. DestroyBlock/RemoveUnstableBlockNetworked are null-safe and
            // DestroyImmediate runs unconditionally, so the block is still removed. (Decompile-
            // verified; contents already captured into `slots`.)
            BlockCreator.RemoveBlockNetwork(original, null, true);

            var nb = bc.CreateBlock(item, pos, rot, dps, -1, false, 0u, 0u, 0u);
            if (nb is Storage_Small ns && slots != null)
            {
                var inv = ns.GetInventoryReference();
                if (inv != null) inv.SetSlotsFromRGD(slots);
                Note($"placed '{item.UniqueName}' at {pos}; restored {slots.Length} slots.");
            }
            else
            {
                Log?.LogWarning($"placed '{item?.UniqueName}' but new block is not Storage_Small (nb={(nb == null ? "null" : nb.GetType().Name)}).");
            }

            // clear state WITHOUT re-activating the (now removed) original, and exit build mode (BUG3)
            moving = null;
            movingItem = null;
            movingSlots = null;
            ExitBuildMode();
        }
    }

    // Our own guaranteed per-frame ticker, independent of BaseUnityPlugin.Update, surviving scene loads.
    public class Ticker : MonoBehaviour
    {
        private void Update() => Plugin.Tick();
    }
}
