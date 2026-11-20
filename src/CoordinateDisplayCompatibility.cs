using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationPatches;

internal sealed class CoordinateDisplayCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.coordinate-display";
    private readonly Harmony harmony = new(HarmonyId);
    private static readonly ConditionalWeakTable<GuiElementTextInput, object> HiddenMapInputs = new();
    private static CoordinateDisplayCompatibility? active;
    private bool disposed;

    private CoordinateDisplayCompatibility() { }

    public static CoordinateDisplayCompatibility? TryCreate(ICoreClientAPI api)
    {
        // Civilization owns map descriptions, marker editing and coordinate presentation.
        if (!SeraphCivilizationPatchesSystem.UseIndependentMaps(api.ModLoader)) return null;

        active?.Dispose();
        var compatibility = new CoordinateDisplayCompatibility();
        bool installed = false;
        {
            try
            {
                Type type = FindType("ImmersiveMaps.Utils.MapCoordinates")
                    ?? throw new TypeLoadException("Immersive Maps map coordinates type was not found.");
                foreach ((string methodName, string patchName) in new[]
                {
                    ("Describe", nameof(HideMapDescription)),
                    ("FieldLabels", nameof(HideMapFields)),
                    ("FieldHints", nameof(HideMapFields))
                })
                {
                    MethodInfo method = AccessTools.Method(type, methodName)
                        ?? throw new MissingMethodException(type.FullName, methodName);
                    compatibility.harmony.Patch(method, prefix: new HarmonyMethod(typeof(CoordinateDisplayCompatibility), patchName));
                }

                Type itemMap = FindType("ImmersiveMaps.Items.ItemMap")
                    ?? throw new TypeLoadException("Immersive Maps item type was not found.");
                MethodInfo itemInfo = AccessTools.Method(itemMap, "GetHeldItemInfo")
                    ?? throw new MissingMethodException(itemMap.FullName, "GetHeldItemInfo");
                compatibility.harmony.Patch(itemInfo, prefix: new HarmonyMethod(typeof(CoordinateDisplayCompatibility), nameof(HideMapDebugInfo)));

                MethodInfo addTextInput = AccessTools.Method(typeof(GuiComposerHelpers), "AddTextInput",
                    new[] { typeof(GuiComposer), typeof(ElementBounds), typeof(Action<string>), typeof(CairoFont), typeof(string) })
                    ?? throw new MissingMethodException(typeof(GuiComposerHelpers).FullName, "AddTextInput");
                compatibility.harmony.Patch(addTextInput, postfix: new HarmonyMethod(typeof(CoordinateDisplayCompatibility), nameof(MarkMapCoordinateInput)));
                MethodInfo renderTextInput = AccessTools.Method(typeof(GuiElementTextInput), "RenderInteractiveElements")
                    ?? throw new MissingMethodException(typeof(GuiElementTextInput).FullName, "RenderInteractiveElements");
                compatibility.harmony.Patch(renderTextInput, prefix: new HarmonyMethod(typeof(CoordinateDisplayCompatibility), nameof(HideMapCoordinateInput)));
                MethodInfo keyDownTextInput = AccessTools.Method(typeof(GuiElementTextInput), "OnKeyDown")
                    ?? throw new MissingMethodException(typeof(GuiElementTextInput).FullName, "OnKeyDown");
                compatibility.harmony.Patch(keyDownTextInput, prefix: new HarmonyMethod(typeof(CoordinateDisplayCompatibility), nameof(BlockHiddenMapInput)));
                installed = true;
            }
            catch (Exception exception)
            {
                api.Logger.Warning("[vsscpmod] Immersive Maps coordinate patch failed: {0}", exception.GetBaseException().Message);
            }
        }

        if (!installed)
        {
            compatibility.Dispose();
            return null;
        }
        active = compatibility;
        api.Logger.Notification("[vsscpmod] Coordinate display compatibility enabled.");
        return compatibility;
    }

    private static bool HideMapDescription(ref string __result)
    {
        if (!InformationRestrictions.ClientImmersiveMapsRestricted) return true;
        __result = Lang.Get("vsscpmod:map-location-hidden");
        return false;
    }

    private static bool HideMapFields(ref (string First, string Second) __result)
    {
        if (!InformationRestrictions.ClientImmersiveMapsRestricted) return true;
        __result = (string.Empty, string.Empty);
        return false;
    }

    private static void HideMapDebugInfo(ref bool withDebugInfo)
    {
        if (InformationRestrictions.ClientImmersiveMapsRestricted) withDebugInfo = false;
    }

    private static void MarkMapCoordinateInput(GuiComposer composer, Action<string> onTextChanged, string key)
    {
        if (key is not ("firstCoordinateInput" or "secondCoordinateInput")
            || onTextChanged.Target?.GetType().FullName != "ImmersiveMaps.Gui.GuiDialogEditWaypoint") return;
        GuiElementTextInput input = GuiComposerHelpers.GetTextInput(composer, key);
        HiddenMapInputs.GetValue(input, _ => new object());
    }

    private static bool HideMapCoordinateInput(GuiElementTextInput __instance)
        => !InformationRestrictions.ClientImmersiveMapsRestricted || !HiddenMapInputs.TryGetValue(__instance, out _);

    private static bool BlockHiddenMapInput(GuiElementTextInput __instance, KeyEvent args)
    {
        if (!InformationRestrictions.ClientImmersiveMapsRestricted || !HiddenMapInputs.TryGetValue(__instance, out _)) return true;
        args.Handled = true;
        return false;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false);
            if (type != null) return type;
        }
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ReferenceEquals(active, this)) active = null;
        harmony.UnpatchAll(HarmonyId);
    }
}
