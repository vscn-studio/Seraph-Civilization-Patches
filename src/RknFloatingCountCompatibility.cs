using HarmonyLib;
using Vintagestory.API.Client;

namespace SeraphCivilizationPatches;

internal sealed class RknFloatingCountCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.rkn-floating-counts";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly CraftingCountRenderer replacement;
    private static RknFloatingCountCompatibility? current;

    private RknFloatingCountCompatibility(CraftingCountRenderer replacement) => this.replacement = replacement;

    public static RknFloatingCountCompatibility? TryCreate(ICoreClientAPI api, CraftingCountRenderer replacement)
    {
        var compatibility = new RknFloatingCountCompatibility(replacement);
        try
        {
            Type? surface = AccessTools.TypeByName("RKN.Crafting.Entities.BlockEntityCraftingSurface");
            if (surface == null || CraftingSurfaceAccess.TryCreate(surface) == null) return null;
            // Older RKN versions do not have their own floating count renderer.
            Type? builtin = surface.Assembly.GetType("RknCrafting.Entities.GridCountRenderer");
            if (builtin == null) return null;
            var render = AccessTools.Method(builtin, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])
                ?? throw new MissingMethodException(builtin.FullName, "OnRenderFrame");
            current?.Dispose();
            compatibility.harmony.Patch(render,
                prefix: new HarmonyMethod(typeof(RknFloatingCountCompatibility), nameof(RenderBuiltin)));
            current = compatibility;
            api.Logger.Notification("[vsscpmod] RKN built-in floating counts replaced by Seraph counts.");
            return compatibility;
        }
        catch (Exception exception)
        {
            compatibility.harmony.UnpatchAll(HarmonyId);
            api.Logger.Warning("[vsscpmod] RKN floating-count replacement unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    // Keep RKN's renderer registered so it can resume if our renderer fails.
    // This also handles RKN registering its renderer after server config arrives.
    private static bool RenderBuiltin() => current?.replacement.IsOperational != true;

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
    }
}
