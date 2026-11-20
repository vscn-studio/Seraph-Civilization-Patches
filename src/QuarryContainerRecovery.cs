using System.Runtime.CompilerServices;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

// Migrate loaded world containers, never player or vehicle inventories. Defer until
// the entire derived block entity has finished restoring its inventory.
internal static class QuarryContainerRecovery
{
    private sealed class State
    {
        public int Queued;
        public bool Running;
    }
    private static readonly ConditionalWeakTable<BlockEntity, State> states = new();

    internal static void Queue(BlockEntity __instance)
    {
        if (__instance is not IBlockEntityContainer || __instance.Api?.Side != EnumAppSide.Server) return;
        var state = states.GetOrCreateValue(__instance);
        if (Interlocked.Exchange(ref state.Queued, 1) != 0) return;
        __instance.Api.Event.EnqueueMainThreadTask(() =>
        {
            Interlocked.Exchange(ref state.Queued, 0);
            Recover(__instance);
        }, "vsscpmod-quarry-container-recovery");
    }

    internal static void BeforeOpen(InventoryBase __instance)
    {
        if (__instance.Api?.Side != EnumAppSide.Server || __instance.Pos == null) return;
        var entity = __instance.Api.World.BlockAccessor.GetBlockEntity(__instance.Pos);
        if (entity is IBlockEntityContainer container && ReferenceEquals(container.Inventory, __instance)) Recover(entity);
    }

    private static void Recover(BlockEntity entity)
    {
        if (!QuarryCarryCompatibility.Active || entity.Api?.Side != EnumAppSide.Server
            || entity is not IBlockEntityContainer container || entity.Pos == null) return;
        var world = entity.Api.World;
        if (!world.Config.GetBool("vsscpmodQuarryCarryEnabled", true)
            || !ReferenceEquals(world.BlockAccessor.GetBlockEntity(entity.Pos), entity)
            || container.Inventory is not { } inventory) return;
        var state = states.GetOrCreateValue(entity);
        if (state.Running) return;
        state.Running = true;
        try
        {
            int threshold = world.Config.GetInt("vsscpmodQuarryCarryBackpack", 32);
            foreach (ItemSlot slot in inventory)
            {
                var stack = slot.Itemstack;
                if (stack == null || stack.StackSize <= 0
                    || QuarryCarryCompatibility.CountBlocks(stack) / stack.StackSize < threshold
                    || (slot.StorageType & EnumItemStorageFlags.Backpack) != 0) continue;
                // Keep the original untouched if spawning fails. One whole stack,
                // including nested Quarry inventory and custom data, becomes one drop.
                try
                {
                    if (world.SpawnItemEntity(stack.Clone(), entity.Pos.UpCopy()) == null) continue;
                }
                catch (Exception exception)
                {
                    entity.Api.Logger.Warning("[vsscpmod] Could not eject legacy Quarry slab at {0}: {1}",
                        entity.Pos, exception.Message);
                    continue;
                }
                slot.Itemstack = null;
                slot.MarkDirty();
                entity.MarkDirty(true);
            }
        }
        finally { state.Running = false; }
    }
}
