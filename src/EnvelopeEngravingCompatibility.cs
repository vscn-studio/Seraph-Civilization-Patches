using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

internal sealed class EnvelopeEngravingCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.envelope-engraving";
    private readonly Harmony harmony = new(HarmonyId);
    private static EnvelopeEngravingCompatibility? current;
    private bool disposed;

    public static EnvelopeEngravingCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("envelopes")) return null;
        var owner = new EnvelopeEngravingCompatibility();
        try
        {
            current?.Dispose();
            owner.harmony.Patch(AccessTools.Constructor(typeof(ShapeTextureSource),
                [typeof(ICoreClientAPI), typeof(Shape), typeof(string),
                    typeof(IDictionary<string, CompositeTexture>), typeof(TexturePathUpdater)]),
                postfix: new HarmonyMethod(typeof(EnvelopeEngravingCompatibility), nameof(UseEngravingMetal)));
            current = owner;
            api.Logger.Notification("[vsscpmod] Envelopes uses the selected engraving metal over the static ring texture fallback.");
            return owner;
        }
        catch (Exception exception)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Envelopes engraving compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static void UseEngravingMetal(ShapeTextureSource __instance, Shape shape, string filenameForLogging)
    {
        // Envelopes sets the per-stack engraving texture on its generated shape.
        // The constructor otherwise gives the shared item defaults precedence.
        if (filenameForLogging == "stampssealsource" && shape.Textures?.ContainsKey("engraving") == true)
            __instance.textures.Remove("engraving");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(current, this)) current = null;
    }
}
