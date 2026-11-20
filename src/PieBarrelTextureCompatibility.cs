using System.Globalization;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

internal sealed class PieBarrelTextureCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.pie-barrel-texture";
    private readonly Harmony harmony = new(HarmonyId);
    private static PieBarrelTextureCompatibility? current;
    private bool disposed;

    public static PieBarrelTextureCompatibility? TryCreate(ICoreClientAPI api)
    {
        var owner = new PieBarrelTextureCompatibility();
        try
        {
            current?.Dispose();
            owner.harmony.Patch(AccessTools.DeclaredMethod(typeof(BlockBarrel), nameof(BlockBarrel.getContentTexture)),
                prefix: new HarmonyMethod(typeof(PieBarrelTextureCompatibility), nameof(UsePieCrust)) { priority = Priority.Last });
            current = owner;
            api.Logger.Notification("[vsscpmod] Barrel pie surfaces use the dough and baking-stage texture.");
            return owner;
        }
        catch (Exception exception)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Barrel pie texture fix unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static bool UsePieCrust(ICoreClientAPI capi, ItemStack stack,
        ref float fillHeight, ref ITexPositionSource __result)
    {
        if (stack?.Block is not BlockPie pie || stack.ItemAttributes?["inContainerTexture"].Exists == true) return true;

        // GetContents resolves creative ucontents and ingredient references. Work
        // on a clone so a mesh rebuild never writes back to the stored food.
        var contents = pie.GetContents(capi.World, stack.Clone());
        AssetLocation texture;
        if (MealMeshCache.ContentsRotten(contents))
        {
            texture = new AssetLocation("game:block/wood/barrel/rot");
        }
        else
        {
            int defaultLevel = pie.Code.EndVariant() switch { "partbaked" => 1, "perfect" => 2, "charred" => 3, _ => 0 };
            int stage = Math.Clamp(stack.Attributes.GetAsInt("bakeLevel", defaultLevel), 0, 3) + 1;
            var crust = contents.ElementAtOrDefault(0) ?? contents.ElementAtOrDefault(5);
            string? path = crust?.ItemAttributes?["inPieProperties"]["texture"].AsString();
            texture = string.IsNullOrEmpty(path)
                ? new AssetLocation("game:block/food/pie/spelt{bakelevel}")
                : AssetLocation.Create(path, crust!.Collectible.Code.Domain);
            texture.Path = texture.Path.Replace("{bakelevel}", stage.ToString(CultureInfo.InvariantCulture));
            if (!capi.Assets.Exists(texture.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png")))
                texture = new AssetLocation("game:block/food/pie/spelt" + stage.ToString(CultureInfo.InvariantCulture));
        }

        // Keep the vanilla barrel's surface geometry and stack-based fill height.
        // A pie's static invisible shape only supplies the 'unknown' texture.
        __result = new ContainerTextureSource(capi, stack, new CompositeTexture(texture));
        fillHeight = Math.Min(0.75f, 0.7f * stack.StackSize / stack.Collectible.MaxStackSize);
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(current, this)) current = null;
    }
}
