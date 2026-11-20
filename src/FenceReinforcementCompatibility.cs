using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

internal sealed class FenceReinforcementCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.fence-reinforcement";
    private readonly Harmony harmony = new(HarmonyId);
    private static FenceReinforcementCompatibility? current;
    private bool disposed;

    public static FenceReinforcementCompatibility? TryCreate(ICoreAPI api)
    {
        var owner = new FenceReinforcementCompatibility();
        try
        {
            current?.Dispose();
            owner.harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockFence), nameof(BlockFence.OnNeighbourBlockChange)),
                transpiler: new HarmonyMethod(typeof(FenceReinforcementCompatibility), nameof(PreserveReinforcement)));
            current = owner;
            api.Logger.Notification("[vsscpmod] Fence connection changes preserve reinforcement and ownership.");
            return owner;
        }
        catch (Exception exception)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Fence reinforcement fix unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static IEnumerable<CodeInstruction> PreserveReinforcement(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var setBlock = AccessTools.Method(typeof(IBlockAccessor), nameof(IBlockAccessor.SetBlock),
            [typeof(int), typeof(BlockPos)]);
        var matches = code.Where(instruction => instruction.Calls(setBlock)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException("Expected one SetBlock call in BlockFence.OnNeighbourBlockChange.");

        // Only shape changes use ExchangeBlock. Real placement still calls
        // OnBlockPlaced and clears any stale reinforcement at that position.
        // Keep the original argument stack, labels and exception boundaries.
        matches[0].operand = AccessTools.Method(typeof(IBlockAccessor), nameof(IBlockAccessor.ExchangeBlock),
            [typeof(int), typeof(BlockPos)]);
        return code;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(current, this)) current = null;
    }
}
