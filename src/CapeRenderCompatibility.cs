using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

internal sealed class CapeRenderCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.cape-render";
    private static readonly HashSet<CapeRenderCompatibility> owners = [];
    private static ConditionalWeakTable<ItemStack, RetryState> retries = new();
    private static FieldInfo? atlasTexturesField;
    private static ILogger? logger;
    private static int staleAtlasWarningReported;

    private sealed class RetryState
    {
        public long NextAttempt;
        public bool Reported;
    }

    private sealed record RenderState(MultiTextureMeshRef? Original, RetryState Retry, bool Skipped);

    public static CapeRenderCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("capes") || !api.ModLoader.IsModEnabled("attributerenderinglibrary")) return null;
        if (owners.Count == 0)
        {
            var harmony = new Harmony(HarmonyId);
            try
            {
                var type = AccessTools.TypeByName("AttributeRenderingLibrary.CollectibleBehaviorWearableAttachment")
                    ?? throw new TypeLoadException("Attribute Rendering Library wearable renderer");
                var method = AccessTools.DeclaredMethod(type, "OnBeforeRender", [typeof(ICoreClientAPI), typeof(ItemStack), typeof(EnumItemRenderTarget), typeof(ItemRenderInfo).MakeByRefType()])
                    ?? throw new MissingMethodException(type.FullName, "OnBeforeRender");
                var atlasType = AccessTools.TypeByName("Vintagestory.Client.NoObf.TextureAtlasManager")
                    ?? throw new TypeLoadException("Texture atlas manager");
                atlasTexturesField = AccessTools.Field(atlasType, "AtlasTextures")
                    ?? throw new MissingFieldException(atlasType.FullName, "AtlasTextures");
                var regenMipMaps = AccessTools.DeclaredMethod(atlasType, "RegenMipMaps", [typeof(int)])
                    ?? throw new MissingMethodException(atlasType.FullName, "RegenMipMaps");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(CapeRenderCompatibility), nameof(BeforeRender)),
                    finalizer: new HarmonyMethod(typeof(CapeRenderCompatibility), nameof(AfterRender)));
                harmony.Patch(regenMipMaps, prefix: new HarmonyMethod(typeof(CapeRenderCompatibility), nameof(BeforeRegenMipMaps)));
                logger = api.Logger;
                Volatile.Write(ref staleAtlasWarningReported, 0);
                api.Logger.Notification("[vsscpmod] Cape render recovery and stale atlas mipmap protection enabled.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(HarmonyId);
                api.Logger.Warning("[vsscpmod] Cape render recovery unavailable: {0}", error.GetBaseException().Message);
                return null;
            }
        }
        var owner = new CapeRenderCompatibility();
        owners.Add(owner);
        return owner;
    }

    private static bool BeforeRegenMipMaps(object __instance, int atlasNumber)
    {
        var textures = atlasTexturesField?.GetValue(__instance) as IList<LoadedTexture>;
        if (textures != null && atlasNumber >= 0 && atlasNumber < textures.Count)
        {
            var texture = textures[atlasNumber];
            if (texture != null && !texture.Disposed && texture.TextureId > 0) return true;
        }

        if (Interlocked.Exchange(ref staleAtlasWarningReported, 1) == 0)
            logger?.Warning("[vsscpmod] Ignored a stale texture atlas mipmap request at index {0}; loaded atlas count is {1}.",
                atlasNumber, textures?.Count ?? 0);
        return false;
    }

    private static bool BeforeRender(ItemStack itemstack, ref ItemRenderInfo renderinfo, out RenderState? __state)
    {
        __state = null;
        if (itemstack?.Collectible?.Code?.ToString() != "capes:cape" || renderinfo == null) return true;
        var retry = retries.GetOrCreateValue(itemstack);
        bool skipped = Environment.TickCount64 < retry.NextAttempt && ClientMeshRecovery.Usable(renderinfo.ModelRef);
        __state = new RenderState(renderinfo.ModelRef, retry, skipped);
        return !skipped;
    }

    private static Exception? AfterRender(Exception? __exception, ICoreClientAPI capi, ref ItemRenderInfo renderinfo, RenderState? __state)
    {
        if (__state == null || __state.Skipped) return __exception;
        if (__exception == null && renderinfo != null && ClientMeshRecovery.Usable(renderinfo.ModelRef))
        {
            __state.Retry.NextAttempt = 0;
            return null;
        }
        if (!ClientMeshRecovery.Usable(__state.Original)) return __exception;
        if (renderinfo == null) return __exception;
        renderinfo.ModelRef = __state.Original;
        __state.Retry.NextAttempt = Environment.TickCount64 + 5000;
        if (!__state.Retry.Reported)
        {
            __state.Retry.Reported = true;
            capi.Logger.Warning("[vsscpmod] Cape model generation failed; using the original model and retrying. {0}",
                __exception?.ToString() ?? "The renderer returned an invalid mesh.");
        }
        return null;
    }

    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        new Harmony(HarmonyId).UnpatchAll(HarmonyId);
        retries = new();
        atlasTexturesField = null;
        logger = null;
        Volatile.Write(ref staleAtlasWarningReported, 0);
    }
}
