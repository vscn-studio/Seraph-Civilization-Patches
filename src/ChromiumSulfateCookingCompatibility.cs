using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

internal sealed class ChromiumSulfateCookingCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.chromium-sulfate";
    private readonly Harmony harmony = new(HarmonyId);
    private static ChromiumSulfateCookingCompatibility? current;
    private bool disposed;

    public static ChromiumSulfateCookingCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("vintagecanvas")) return null;
        var owner = new ChromiumSulfateCookingCompatibility();
        try
        {
            current?.Dispose();
            owner.harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockCookingContainer), nameof(BlockCookingContainer.GetMeltingPoint)),
                postfix: new HarmonyMethod(typeof(ChromiumSulfateCookingCompatibility), nameof(UseCookingTemperature)));
            current = owner;
            api.Logger.Notification("[vsscpmod] Chromium sulfate cooks at 100 C; VintageCanvas pigment smelting is preserved.");
            return owner;
        }
        catch (Exception exception)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Chromium sulfate cooking fix unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static void UseCookingTemperature(BlockCookingContainer __instance, IWorldAccessor world,
        ISlotProvider cookingSlotsProvider, ref float __result)
    {
        if (__result <= 100f) return;
        var stacks = __instance.GetCookingStacks(cookingSlotsProvider, clone: false);
        var recipe = __instance.GetMatchingCookingRecipe(world, stacks, out int servings);
        // Only a valid sulfate recipe gets the original cooking temperature.
        // The mineral's own 1000 C pigment recipe must keep its combustible properties.
        if (servings > 0 && servings <= __instance.MaxServingSize
            && recipe?.Code == "sulfate-full-chromite"
            && recipe.CooksInto?.ResolvedItemstack?.Collectible.Code.Equals(new AssetLocation("game:sulfate-full-chromite")) == true)
        {
            __result = 100f;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(current, this)) current = null;
    }
}
