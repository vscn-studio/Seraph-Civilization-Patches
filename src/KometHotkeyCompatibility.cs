using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using HotkeyDictionary = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;

namespace SeraphCivilizationPatches;

internal sealed class KometHotkeyCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.komet-hotkeys";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreClientAPI api;
    private static KometHotkeyCompatibility? current;
    private FieldInfo binding = null!, searching = null!, canvas = null!, dirty = null!;
    private PropertyInfo canvasX = null!, canvasY = null!, option = null!, kind = null!, code = null!, enabled = null!, hotkeys = null!;
    private MethodInfo at = null!, bind = null!;

    private KometHotkeyCompatibility(ICoreClientAPI api) => this.api = api;

    public static KometHotkeyCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("komet") || !api.ModLoader.IsModEnabled("unbindhotkeys")) return null;
        var compatibility = new KometHotkeyCompatibility(api);
        try
        {
            Type screen = AccessTools.TypeByName("Komet.Options.OptionsScreen")
                ?? throw new TypeLoadException("Komet.Options.OptionsScreen");
            Type engine = screen.Assembly.GetType("Komet.Options.EngineOptions", true)!;
            Type row = screen.Assembly.GetType("Komet.Api.OptionRow", true)!;
            FieldInfo Field(string name) => AccessTools.Field(screen, name) ?? throw new MissingFieldException(screen.FullName, name);
            PropertyInfo Property(Type type, string name) => AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name);
            MethodInfo Method(Type type, string name, Type[] parameters) => AccessTools.Method(type, name, parameters)
                ?? throw new MissingMethodException(type.FullName, name);
            compatibility.binding = Field("_binding");
            compatibility.searching = Field("_searching");
            compatibility.canvas = Field("_canvas");
            compatibility.dirty = Field("_dirty");
            compatibility.canvasX = Property(compatibility.canvas.FieldType, "X");
            compatibility.canvasY = Property(compatibility.canvas.FieldType, "Y");
            compatibility.at = Method(screen, "At", [typeof(double), typeof(double), typeof(bool)]);
            compatibility.option = Property(compatibility.at.ReturnType, "Option");
            compatibility.kind = Property(row, "Kind");
            compatibility.code = Property(row, "Code");
            compatibility.enabled = Property(row, "IsEnabled");
            compatibility.bind = Method(engine, "Bind", [typeof(string), typeof(KeyCombination)]);
            compatibility.hotkeys = Property(engine, "HotKeys");
            MethodInfo keyDown = Method(screen, "OnKeyDown", [typeof(KeyEvent)]);
            MethodInfo capture = Method(screen, "CaptureAllInputs", []);
            MethodInfo clash = Method(engine, "Clash", [typeof(string)]);
            MethodInfo mapping = Method(engine, "Mapping", [typeof(string)]);
            current?.Dispose();
            current = compatibility;
            compatibility.harmony.Patch(keyDown, prefix: new HarmonyMethod(typeof(KometHotkeyCompatibility), nameof(OnKeyDown)));
            compatibility.harmony.Patch(capture, postfix: new HarmonyMethod(typeof(KometHotkeyCompatibility), nameof(CaptureHoveredKey)));
            compatibility.harmony.Patch(clash, prefix: new HarmonyMethod(typeof(KometHotkeyCompatibility), nameof(HideClearedMapping)));
            compatibility.harmony.Patch(mapping, prefix: new HarmonyMethod(typeof(KometHotkeyCompatibility), nameof(HideClearedMapping)));
            api.Logger.Notification("[vsscpmod] Komet / Unbind Hot Keys compatibility enabled.");
            return compatibility;
        }
        catch (Exception exception)
        {
            compatibility.harmony.UnpatchAll(HarmonyId);
            if (ReferenceEquals(current, compatibility)) current = null;
            api.Logger.Warning("[vsscpmod] Komet hotkey compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static void CaptureHoveredKey(GuiDialog __instance, ref bool __result)
    {
        // The embedded ESC menu captures all keys in GuiManager's first pass.
        // Focus alone cannot deliver hover shortcuts to Komet in the second pass.
        // Keep Komet's search/binding capture and let unrelated keys pass onward.
        if (!__result && current?.HoveredKey(__instance) != null) __result = true;
    }

    private string? HoveredKey(GuiDialog dialog)
    {
        if (!dialog.IsOpened() || binding.GetValue(dialog) != null || (bool)searching.GetValue(dialog)!) return null;
        object surface = canvas.GetValue(dialog)!;
        double x = api.Input.MouseX - (double)canvasX.GetValue(surface)!;
        double y = api.Input.MouseY - (double)canvasY.GetValue(surface)!;
        object? hit = at.Invoke(dialog, [x, y, false]);
        object? row = hit == null ? null : option.GetValue(hit);
        if (row == null || kind.GetValue(row)?.ToString() != "Key" || !(bool)enabled.GetValue(row)!) return null;
        string key = (string)code.GetValue(row)!;
        return hotkeys.GetValue(null) is HotkeyDictionary keys && keys.ContainsKey(key) ? key : null;
    }

    private static bool OnKeyDown(GuiDialog __instance, KeyEvent args)
    {
        var self = current;
        // Unbind Hot Keys 1.0.1 uses key codes 55 (Delete) and 86 (D).
        if (self == null || args == null || args.Handled
            || (args.KeyCode != (int)GlKeys.Delete && args.KeyCode != (int)GlKeys.D)) return true;

        // Use Komet's hit testing, including its scrolled viewport and canvas offset.
        // Capturing a new binding keeps Komet's normal Delete-key assignment behavior.
        string? key = self.HoveredKey(__instance);
        if (key == null) return true;

        // Komet handles cloning, persistent settings, linked Ctrl/Shift and notifications.
        self.bind.Invoke(null, [key, new KeyCombination { KeyCode = 0 }]);
        self.dirty.SetValue(__instance, true);
        args.Handled = true;
        return false;
    }

    private static bool HideClearedMapping(string code, ref string __result)
    {
        // Show cleared bindings as blank, without Komet's key name or conflict text.
        if (current?.hotkeys.GetValue(null) is HotkeyDictionary keys
            && keys.TryGetValue(code, out HotKey key) && key.CurrentMapping?.KeyCode == 0)
        {
            __result = "";
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
    }
}
