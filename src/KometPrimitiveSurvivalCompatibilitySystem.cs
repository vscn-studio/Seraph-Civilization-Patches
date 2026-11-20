using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

/// <summary>Keep a gameplay mod out of Komet's optional shader-pack lifecycle.</summary>
public sealed class KometPrimitiveSurvivalCompatibilitySystem : ModSystem
{
    private const string HarmonyId = "vsscpmod.komet-primitive-survival";
    private Harmony? harmony;

    // Komet 3.0.0-pre.3 calls HoldBack in StartPre at the default order (0.1).
    // Installing in the main patch system (1.4), or in Start, is already too late.
    public override double ExecuteOrder() => -0.1;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api)
    {
        if (harmony != null || api.Side != EnumAppSide.Client
            || !api.ModLoader.IsModEnabled("komet")
            || !api.ModLoader.IsModEnabled("primitivesurvival")
            || api.ModLoader.GetMod("komet")?.Info.Version != "3.0.0-pre.3") return;

        try
        {
            Type shaders = AccessTools.TypeByName("Komet.Rendering.ShaderMods")
                ?? throw new TypeLoadException("Komet.Rendering.ShaderMods");
            MethodInfo all = AccessTools.DeclaredMethod(shaders, "All", [typeof(ICoreAPI)])
                ?? throw new MissingMethodException(shaders.FullName, "All");
            if (!all.IsStatic || !typeof(IList).IsAssignableFrom(all.ReturnType)
                || !all.ReturnType.IsGenericType
                || AccessTools.Property(all.ReturnType.GetGenericArguments()[0], "Id")?.PropertyType != typeof(string))
                throw new MissingMemberException("Unexpected Komet shader list signature");

            harmony = new Harmony(HarmonyId);
            harmony.Patch(all, postfix: new HarmonyMethod(typeof(KometPrimitiveSurvivalCompatibilitySystem), nameof(KeepGameplayMod)));
            api.Logger.Notification("[vsscpmod] Komet 3.0.0-pre.3 / Primitive Survival startup compatibility enabled.");
        }
        catch (Exception exception)
        {
            Dispose();
            api.Logger.Warning("[vsscpmod] Komet / Primitive Survival compatibility unavailable: {0}", exception.GetBaseException().Message);
        }
    }

    private static void KeepGameplayMod(object __result)
    {
        // All returns a fresh list backed by cached scan results. Filter only the
        // returned list: never alter Seen, saved shader preferences or other mods.
        // This also keeps PS out of the UI's whole-mod pause/resume controls.
        var mods = (IList)__result;
        for (int i = mods.Count - 1; i >= 0; i--)
        {
            object? mod = mods[i];
            if (mod != null && AccessTools.Property(mod.GetType(), "Id").GetValue(mod) is string id
                && id == "primitivesurvival") mods.RemoveAt(i);
        }
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        base.Dispose();
    }
}
