using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

/// <summary>
/// Click Up Torches normally obtains a torch with OnPickBlock. Candela records
/// a burning torch's remaining life in GetDrops, so use that path for this one
/// interaction without changing pickup behavior for other blocks.
/// </summary>
internal sealed class CandelaClickUpTorchesCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.candela-click-up-torches";
    private readonly Harmony harmony = new(HarmonyId);
    private static readonly FieldInfo DropsPickupMode = AccessTools.Field(typeof(BlockBehaviorRightClickPickup), "dropsPickupMode")
        ?? throw new MissingFieldException(typeof(BlockBehaviorRightClickPickup).FullName, "dropsPickupMode");
    private static CandelaClickUpTorchesCompatibility? current;
    private bool disposed;

    public static CandelaClickUpTorchesCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("candela") || !api.ModLoader.IsModEnabled("clickuptorches")) return null;

        var owner = new CandelaClickUpTorchesCompatibility();
        try
        {
            current?.Dispose();
            MethodInfo interact = AccessTools.DeclaredMethod(typeof(BlockBehaviorRightClickPickup), nameof(BlockBehaviorRightClickPickup.OnBlockInteractStart))
                ?? throw new MissingMethodException(typeof(BlockBehaviorRightClickPickup).FullName, nameof(BlockBehaviorRightClickPickup.OnBlockInteractStart));
            owner.harmony.Patch(interact,
                prefix: new HarmonyMethod(typeof(CandelaClickUpTorchesCompatibility), nameof(UseDropsForTorchPickup)),
                postfix: new HarmonyMethod(typeof(CandelaClickUpTorchesCompatibility), nameof(RestorePickupMode)));
            current = owner;
            api.Logger.Notification("[vsscpmod] Click Up Torches preserves Candela torch burn time when picked up.");
            return owner;
        }
        catch (Exception exception)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Candela/Click Up Torches compatibility unavailable: {0}",
                exception.GetBaseException().Message);
            return null;
        }
    }

    private static void UseDropsForTorchPickup(BlockBehaviorRightClickPickup __instance, out bool __state)
    {
        __state = false;
        if (current == null || __instance.block is not BlockTorch) return;
        __state = (bool)DropsPickupMode.GetValue(__instance)!;
        DropsPickupMode.SetValue(__instance, true);
    }

    private static void RestorePickupMode(BlockBehaviorRightClickPickup __instance, bool __state)
    {
        if (current != null && __instance.block is BlockTorch)
            DropsPickupMode.SetValue(__instance, __state);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(current, this)) current = null;
    }
}
