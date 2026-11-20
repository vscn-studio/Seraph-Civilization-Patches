using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;

namespace SeraphCivilizationPatches;

internal sealed class VintageCanvasTextureCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.vintagecanvas-texture";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<VintageCanvasTextureCompatibility> owners = [];

    public static VintageCanvasTextureCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("vintagecanvas")) return null;
        if (owners.Count == 0)
        {
            try
            {
                var texture = AccessTools.TypeByName("VintageCanvas.src.Utility.TextureUtil")
                    ?? throw new TypeLoadException("VintageCanvas texture utility");
                foreach (string operation in new[] { "SwapPaintingTexture", "SwapPaintingTextureMesh" })
                {
                    // Guard the queued action itself: the atlas may be disposed
                    // after scheduling, before ExecuteMainThreadTasks runs it.
                    var callbacks = texture.GetNestedTypes(BindingFlags.NonPublic)
                        .SelectMany(AccessTools.GetDeclaredMethods)
                        .Where(m => m.Name.StartsWith("<" + operation + ">b__", StringComparison.Ordinal)
                            && m.ReturnType == typeof(void) && m.GetParameters().Length == 0
                            && AccessTools.Field(m.DeclaringType, "capi")?.FieldType == typeof(ICoreClientAPI)
                            && AccessTools.Field(m.DeclaringType, "TexPos")?.FieldType == typeof(TextureAtlasPosition))
                        .ToArray();
                    if (callbacks.Length != 1) throw new MissingMethodException(texture.FullName, operation + " atlas update callback");
                    harmony.Patch(callbacks[0], prefix: new HarmonyMethod(typeof(VintageCanvasTextureCompatibility), nameof(CanUpdateAtlas)));
                }
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(HarmonyId);
                api.Logger.Warning("[vsscpmod] VintageCanvas texture compatibility unavailable: {0}", e.GetBaseException().Message);
                return null;
            }
        }
        var owner = new VintageCanvasTextureCompatibility();
        owners.Add(owner);
        api.Logger.Notification("[vsscpmod] VintageCanvas queued painting texture updates validate the current atlas before uploading.");
        return owner;
    }

    private static bool CanUpdateAtlas(ICoreClientAPI ___capi, TextureAtlasPosition? ___TexPos)
    {
        var textures = ___capi.BlockTextureAtlas?.AtlasTextures;
        if (___TexPos == null || textures == null || ___TexPos.atlasNumber >= textures.Count) return false;
        var texture = textures[___TexPos.atlasNumber];
        // An atlas at the same index can belong to a newly loaded world/reload.
        return texture != null && !texture.Disposed && texture.TextureId > 0
            && texture.TextureId == ___TexPos.atlasTextureId;
    }

    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        harmony.UnpatchAll(HarmonyId);
    }
}
