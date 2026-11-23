using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

internal sealed class LevelingSkillPanel : IDisposable
{
    private const string Id = "vsscpmod.leveling-panel";
    internal const string WindowKey = "vsscpmod-character-skills";
    private const string BodyKey = "vsslssmod-character-body";
    private static readonly Harmony harmony = new(Id);
    private static readonly HashSet<LevelingSkillPanel> owners = [];
    private static ConditionalWeakTable<GuiComposer, ScrollState> states = new();
    private static ConditionalWeakTable<object, WindowState> windows = new();
    private static readonly List<WeakReference<object>> views = [];
    private static MethodInfo metrics = null!, rowHeight = null!, font = null!, fit = null!, hover = null!, addHover = null!;
    private static FieldInfo sizes = null!, apiField = null!, dialogField = null!;
    private static object functionsPanel = null!;
    private static ILogger logger = null!;
    private static bool warned;
    internal static readonly (string Title, string[] Stats)[] Groups = [
        ("combat", ["miningSpeedMul", "meleeWeaponsDamage", "meleeTiming", "rangedWeaponsDamage", "rangedWeaponsAcc", "bowDrawingStrength", "bowTiming", "mechanicalsDamage"]),
        ("survival", ["walkspeed", "hungerrate", "healingeffectivness", "maxhealthExtraPoints", "armorDurabilityLoss", "armorWalkSpeedAffectedness", "animalSeekingRange"]),
        ("harvest", ["animalHarvestingTime", "animalLootDropRate", "forageDropRate", "wildCropDropRate", "oreDropRate", "rustyGearDropRate", "vesselContentsDropRate", "wholeVesselLootChance"]),
        ("craft", ["temporalGearTLRepairCost", "unlocks"])
    ];
    private sealed class ScrollState(ElementBounds content, double visible, double total)
    {
        internal readonly ElementBounds Content = content;
        internal readonly double Visible = visible, Total = total;
        internal bool Initialized;
    }
    private sealed class SkillScrollbar(ICoreClientAPI api, Action<float> changed, ElementBounds bounds, ElementBounds viewport)
        : GuiElementScrollbar(api, changed, bounds)
    {
        public override void OnMouseWheel(ICoreClientAPI client, MouseWheelEventArgs args)
        {
            // GuiComposer also broadcasts wheel events to elements outside the pointer.
            // Hit-test the visible clip, since the content extends beyond the window.
            if (args.IsHandled) return;
            int x = client.Input.MouseX, y = client.Input.MouseY;
            if (viewport.PointInside(x, y) || Bounds.PointInside(x, y)) base.OnMouseWheel(client, args);
        }
    }
    private sealed class WindowState(GuiDialog dialog, GuiComposer composer, string language)
    {
        internal readonly GuiDialog Dialog = dialog;
        internal readonly GuiComposer Composer = composer;
        internal readonly string Language = language;
    }

    public static LevelingSkillPanel? TryCreate(ICoreClientAPI api)
    {
        if (!LevelingSurvivalCompatibility.Enabled(api.ModLoader)) return null;
        if (owners.Count == 0)
        {
            try
            {
                var type = AccessTools.TypeByName("Seraph.Client.CharacterPanelIntegration") ?? throw new TypeLoadException("SLSS CharacterPanelIntegration");
                Validate(type);
                logger = api.Logger; warned = false;
                void Patch(string target, string method, bool prefix = false)
                {
                    var patch = new HarmonyMethod(typeof(LevelingSkillPanel), method);
                    harmony.Patch(LevelingSurvivalCompatibility.RequireMethod(type, target), prefix: prefix ? patch : null, postfix: prefix ? null : patch);
                }
                Patch("ComposeExtraPanels", nameof(ComposeWindow));
                Patch("RefreshPanels", nameof(RefreshWindow));
                Patch("OnCharacterClosed", nameof(DetachWindow), true);
                Patch("RemoveOwnedPanels", nameof(RemoveWindow), true);
                Patch("DetachDialog", nameof(RemoveWindow), true);
            }
            catch (Exception ex)
            {
                harmony.UnpatchAll(Id);
                api.Logger.Warning("[vsscpmod] SLSS skill window disabled: {0}", ex.GetBaseException().Message);
                return null;
            }
        }
        var owner = new LevelingSkillPanel(); owners.Add(owner); return owner;
    }

    internal static void Validate(Type type)
    {
        MethodInfo M(string name) => LevelingSurvivalCompatibility.RequireMethod(type, name);
        metrics = M("Metrics"); rowHeight = M("MetricRowHeight"); font = M("AddedPanelFont");
        fit = M("SetFittedValue"); hover = M("SetPanelHoverText");
        sizes = AccessTools.Field(type, "valueFontSizes") ?? throw new MissingFieldException("valueFontSizes");
        apiField = AccessTools.Field(type, "api") ?? throw new MissingFieldException("api");
        dialogField = AccessTools.Field(type, "dialog") ?? throw new MissingFieldException("dialog");
        if (sizes.FieldType != typeof(Dictionary<string, float>) || apiField.FieldType != typeof(ICoreClientAPI)
            || !typeof(GuiDialog).IsAssignableFrom(dialogField.FieldType) || rowHeight.ReturnType != typeof(double)
            || font.ReturnType != typeof(CairoFont) || !metrics.ReturnType.IsArray) throw new MissingMemberException("SLSS metric UI contract");
        var metric = metrics.ReturnType.GetElementType()!;
        foreach (var key in new[] { "Key", "Text", "Detail" }) LevelingSurvivalCompatibility.RequireProperty(metric, key, typeof(string));
        LevelingSurvivalCompatibility.RequireProperty(metric, "Available", typeof(bool));
        var hoverType = type.Assembly.GetType("Seraph.Client.CharacterPanelHoverExtensions", true)!;
        addHover = LevelingSurvivalCompatibility.RequireMethod(hoverType, "AddCharacterPanelHoverText",
            typeof(GuiComposer), typeof(string), typeof(CairoFont), typeof(int), typeof(ElementBounds), typeof(string));
        void Signature(string name, params Type[] arguments)
        {
            var method = M(name);
            if (method.ReturnType != typeof(void) || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments))
                throw new MissingMethodException(type.FullName, name + " signature");
        }
        var panel = metrics.GetParameters().Single().ParameterType;
        functionsPanel = Enum.Parse(panel, "Functions");
        foreach (var name in new[] { "ComposeExtraPanels", "RefreshPanels", "OnCharacterClosed", "RemoveOwnedPanels", "DetachDialog" }) Signature(name);
        Signature("SetFittedValue", typeof(GuiComposer), typeof(string), typeof(string), typeof(string));
        Signature("SetPanelHoverText", typeof(GuiComposer), typeof(string), typeof(string));
    }

    // Height is bounded by the remaining space under the body window. The list scrolls;
    // adding a skill never enlarges either upstream physiology window.
    internal static double WindowHeight(double available, double row)
        => Math.Clamp(available, 58 + row, 58 + 6 * row);

    private static void ComposeWindow(object __instance)
    {
        try
        {
            if (dialogField.GetValue(__instance) is not GuiDialog dialog || dialog.Composers[BodyKey] is not { } body) return;
            if (windows.TryGetValue(__instance, out var existing))
            {
                if (existing.Dialog == dialog && existing.Language == Lang.CurrentLocale)
                { dialog.Composers[WindowKey] = existing.Composer; RefreshRows(__instance, existing.Composer); return; }
                RemoveWindow(__instance);
            }
            var api = (ICoreClientAPI)apiField.GetValue(__instance)!;
            var scale = Math.Max(.1, RuntimeEnv.GUIScale);
            body.Bounds.CalcWorldBounds();
            var left = body.Bounds.absX / scale;
            var top = (body.Bounds.absY + body.Bounds.OuterHeight) / scale + 10;
            var width = body.Bounds.OuterWidth / scale;
            var row = (double)rowHeight.Invoke(__instance, null)!;
            var height = WindowHeight(api.Render.FrameHeight / scale - top - 16, row);
            var background = ElementBounds.Fixed(0, 0, Math.Max(1, width - 24), height - 24).WithFixedPadding(12);
            var bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None).WithFixedPosition(left, top);
            var composer = api.Gui.CreateCompo(WindowKey, bounds);
            try
            {
                composer.AddShadedDialogBG(background).AddDialogTitleBar(Lang.Get("vsscpmod:skill-title"), () => dialog.TryClose());
                composer.BeginChildElements(background);
                ComposeRows(__instance, composer, background.fixedWidth, height - 24);
                composer.EndChildElements().Compose();
                windows.Add(__instance, new(dialog, composer, Lang.CurrentLocale));
                views.RemoveAll(reference => !reference.TryGetTarget(out _));
                if (!views.Any(reference => reference.TryGetTarget(out var view) && ReferenceEquals(view, __instance))) views.Add(new(__instance));
                dialog.Composers[WindowKey] = composer;
                RefreshRows(__instance, composer);
            }
            catch { if (!windows.TryGetValue(__instance, out _)) composer.Dispose(); throw; }
        }
        catch (Exception ex) { RemoveWindow(__instance); Warn(ex); }
    }

    private static void RefreshWindow(object __instance)
    {
        if (!windows.TryGetValue(__instance, out var state) || !state.Dialog.IsOpened()) return;
        try { RefreshRows(__instance, state.Composer); } catch (Exception ex) { Warn(ex); }
    }
    private static void Warn(Exception ex)
    {
        if (warned) return;
        warned = true; logger.Warning("[vsscpmod] Skill window unavailable: {0}", ex);
    }
    private static void DetachWindow(object __instance)
    {
        // Vanilla clears and disposes composers on reopening. Retain our detached one,
        // just as SLSS retains its own windows, so a close/reopen cannot double-dispose it.
        if (windows.TryGetValue(__instance, out var state)) state.Dialog.Composers.Remove(WindowKey);
    }
    private static void RemoveWindow(object __instance)
    {
        if (!windows.TryGetValue(__instance, out var state)) return;
        state.Dialog.Composers.Remove(WindowKey);
        states.Remove(state.Composer); state.Composer.Dispose(); windows.Remove(__instance);
    }

    private static void ComposeRows(object view, GuiComposer composer, double width, double height)
    {
        double row = (double)rowHeight.Invoke(view, null)!;
        double contentWidth = Math.Max(1, width - 22), visible = Math.Max(row, height - 34);
        double total = (LevelingEffects.Definitions.Length + Groups.Length) * row;
        CairoFont Font() => (CairoFont)font.Invoke(view, null)!;
        var content = ElementBounds.Fixed(0, 0, contentWidth, total);
        var viewport = ElementBounds.Fixed(0, 34, contentWidth, visible);
        composer.BeginClip(viewport).BeginChildElements(content);
        var fontSizes = (Dictionary<string, float>)sizes.GetValue(view)!;
        double y = 0, valueWidth = Math.Clamp(contentWidth * .3, 64, 90), labelWidth = Math.Max(1, contentWidth - valueWidth - 8);
        foreach (var group in Groups)
        {
            var groupId = "vsscpmod-skill-group-" + group.Title;
            var groupFont = Font().WithColor([.93, .77, .48, 1]);
            composer.AddDynamicText("", groupFont, ElementBounds.Fixed(0, y, contentWidth, row - 4), groupId);
            fontSizes[groupId] = (float)groupFont.UnscaledFontsize;
            y += row;
            foreach (var stat in group.Stats)
            {
                var key = LevelingEffects.Prefix + stat;
                var valueFont = Font().WithOrientation(EnumTextOrientation.Right);
                composer.AddDynamicText("", Font(), ElementBounds.Fixed(0, y, labelWidth, row - 4), key + "-label");
                composer.AddDynamicText("", valueFont, ElementBounds.Fixed(labelWidth + 8, y, valueWidth, row - 4), key);
                fontSizes[key] = fontSizes[key + "-label"] = (float)valueFont.UnscaledFontsize;
                addHover.Invoke(null, [composer, "", Font(), 360, ElementBounds.Fixed(0, y, contentWidth, row - 4), key + "-help"]);
                y += row;
            }
        }
        composer.EndChildElements().EndClip();
        composer.AddInteractiveElement(new SkillScrollbar(composer.Api,
            value => Position(content, value / Math.Max(.1, RuntimeEnv.GUIScale), total, visible),
            ElementBounds.Fixed(width - 16, 34, 16, visible), viewport), "vsscpmod-skill-scroll");
        states.Remove(composer); states.Add(composer, new(content, visible, total));
    }

    internal static void Position(ElementBounds content, double offset, double total, double visible)
    {
        content.fixedY = -Math.Clamp(offset, 0, Math.Max(0, total - visible));
        content.MarkDirtyRecursive(); content.CalcWorldBounds();
    }

    private static void RefreshRows(object view, GuiComposer composer)
    {
        if (!states.TryGetValue(composer, out var state)) return;
        // Metrics() rejects stale snapshots. Do not substitute client-side estimates.
        var rows = ((IEnumerable)metrics.Invoke(view, [functionsPanel])!).Cast<object>()
            .ToDictionary(m => (string)m.GetType().GetProperty("Key")!.GetValue(m)!);
        foreach (var group in Groups)
            fit.Invoke(view, [composer, "vsscpmod-skill-group-" + group.Title, Lang.Get("vsscpmod:skill-group-" + group.Title), null]);
        foreach (var d in LevelingEffects.Definitions)
        {
            rows.TryGetValue(d.Key, out var metric);
            bool available = metric?.GetType().GetProperty("Available")!.GetValue(metric) is true;
            string value = available ? (string)metric!.GetType().GetProperty("Text")!.GetValue(metric)! : Lang.Get("vsscpmod:skill-unknown");
            string detail = available ? (string)metric!.GetType().GetProperty("Detail")!.GetValue(metric)! : Lang.Get("vsscpmod:skill-unavailable");
            fit.Invoke(view, [composer, d.Key + "-label", Lang.Get("vsscpmod:skill-label-" + d.Stat), null]);
            fit.Invoke(view, [composer, d.Key, value, null]);
            hover.Invoke(view, [composer, d.Key + "-help", detail]);
        }
        if (state.Initialized) return;
        state.Initialized = true;
        composer.GetScrollbar("vsscpmod-skill-scroll").SetHeights((float)(state.Visible * RuntimeEnv.GUIScale), (float)(state.Total * RuntimeEnv.GUIScale));
        Position(state.Content, 0, state.Total, state.Visible);
    }

    public void Dispose()
    {
        owners.Remove(this);
        if (owners.Count != 0) return;
        foreach (var reference in views) if (reference.TryGetTarget(out var view)) RemoveWindow(view);
        views.Clear(); harmony.UnpatchAll(Id); states = new(); windows = new();
    }
}
