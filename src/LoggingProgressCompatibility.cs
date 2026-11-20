using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

// Logging's last Step leaves the bar registered until Stop/Cancel. The engine
// dispatches those callbacks to the current block, which may already be air.
internal sealed class LoggingProgressCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.logging-progress";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreClientAPI api;
    private readonly MethodInfo cancel;
    private long listener;
    private ActiveInteraction? active;
    private bool disposed;
    private static LoggingProgressCompatibility? current;

    private sealed record ActiveInteraction(object Harvest, IWorldAccessor World, IPlayer Player,
        BlockPos Position, BlockPos SelectionPosition, Block Target, BlockEntity? TargetEntity,
        ItemSlot Slot, CollectibleObject? Tool);

    private LoggingProgressCompatibility(ICoreClientAPI api, MethodInfo cancel)
    {
        this.api = api;
        this.cancel = cancel;
    }

    public static LoggingProgressCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("loggingmod")) return null;
        LoggingProgressCompatibility? owner = null;
        try
        {
            var type = AccessTools.TypeByName("LoggingMod.HarvestHoldInteraction")
                ?? throw new TypeLoadException("LoggingMod.HarvestHoldInteraction");
            MethodInfo Required(string name, params Type[] args) => AccessTools.DeclaredMethod(type, name, args)
                ?? throw new MissingMethodException(type.FullName, name);
            var step = Required("OnStep", typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection), typeof(float));
            var stop = Required("TryComplete", typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection), typeof(float));
            var cancel = Required("OnCancel", typeof(IWorldAccessor));
            owner = new LoggingProgressCompatibility(api, cancel);
            current?.Dispose();
            current = owner;
            owner.harmony.Patch(step, postfix: new HarmonyMethod(typeof(LoggingProgressCompatibility), nameof(AfterStep)));
            owner.harmony.Patch(stop, postfix: new HarmonyMethod(typeof(LoggingProgressCompatibility), nameof(AfterClear)));
            owner.harmony.Patch(cancel, postfix: new HarmonyMethod(typeof(LoggingProgressCompatibility), nameof(AfterClear)));
            owner.listener = api.Event.RegisterGameTickListener(owner.OnTick, 50);
            api.Logger.Notification("[vsscpmod] Logging harvest progress bars clear on completion and interrupted interactions.");
            return owner;
        }
        catch (Exception exception)
        {
            owner?.Dispose();
            api.Logger.Warning("[vsscpmod] Logging progress compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static void AfterStep(object __instance, IWorldAccessor world, IPlayer byPlayer,
        BlockSelection blockSel, bool __result)
    {
        var owner = current;
        // In singleplayer the same static helpers also service the server.
        if (owner == null || world.Side != EnumAppSide.Client
            || !ReferenceEquals(world, owner.api.World) || !ReferenceEquals(byPlayer, owner.api.World.Player)) return;
        if (owner.active is { } previous && !ReferenceEquals(previous.Harvest, __instance)) owner.Clear();
        if (!__result)
        {
            // Clear before the engine calls Stop, without invoking completion,
            // changing its result, or awarding/consuming any items ourselves.
            owner.cancel.Invoke(__instance, [world]);
            return;
        }
        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        owner.active = new(__instance, world, byPlayer, blockSel.Position.Copy(),
            (byPlayer.Entity.Controls.HandUsingBlockSel?.Position ?? blockSel.Position).Copy(),
            world.BlockAccessor.GetBlock(blockSel.Position), world.BlockAccessor.GetBlockEntity(blockSel.Position),
            slot, slot.Itemstack?.Collectible);
    }

    private static void AfterClear(object __instance, IWorldAccessor world)
    {
        if (world.Side == EnumAppSide.Client && current?.active is { } interaction
            && ReferenceEquals(interaction.World, world) && ReferenceEquals(interaction.Harvest, __instance))
            current.active = null;
    }

    private void OnTick(float dt)
    {
        if (active is not { } interaction) return;
        var player = api.World?.Player;
        var controls = player?.Entity?.Controls;
        // Independent of the selected block's callbacks: removal, hotbar changes
        // and mouse release can otherwise strand a bar indefinitely.
        if (!ReferenceEquals(api.World, interaction.World) || !ReferenceEquals(player, interaction.Player)
            || !api.Input.InWorldMouseButton.Right || controls?.HandUse != EnumHandInteract.BlockInteract
            || controls.HandUsingBlockSel?.Position?.Equals(interaction.SelectionPosition) != true
            || !ReferenceEquals(player!.InventoryManager.ActiveHotbarSlot, interaction.Slot)
            || !ReferenceEquals(interaction.Slot.Itemstack?.Collectible, interaction.Tool)
            || !ReferenceEquals(interaction.World.BlockAccessor.GetBlock(interaction.Position), interaction.Target)
            || !ReferenceEquals(interaction.World.BlockAccessor.GetBlockEntity(interaction.Position), interaction.TargetEntity))
            Clear();
    }

    private void Clear()
    {
        if (active is not { } interaction) return;
        active = null;
        cancel.Invoke(interaction.Harvest, [interaction.World]);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (listener != 0) api.Event.UnregisterGameTickListener(listener);
        Clear();
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
    }
}
