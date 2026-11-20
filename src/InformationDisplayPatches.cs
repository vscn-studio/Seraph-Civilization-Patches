using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace SeraphCivilizationPatches;

internal sealed class InformationDisplayPatches : IDisposable
{
    private sealed class TrackedText(string kind, string raw)
    {
        public string Kind = kind;
        public string Raw = raw;
    }

    private static InformationDisplayPatches? active;
    private readonly ICoreClientAPI api;
    private readonly ConditionalWeakTable<GuiElementDynamicText, TrackedText> texts = new();
    private readonly ConditionalWeakTable<GuiElementHoverText, TrackedText> hovers = new();
    private static readonly Regex Clock = new(@"(?<!\d)\d{1,2}:\d{2}(?::\d{2})?(?:\s*[AP]M)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CoordinateTriple = new(@"(?<![\d.])-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?\s*,\s*-?\d+(?:\.\d+)?(?![\d.])", RegexOptions.Compiled);
    private static readonly HashSet<string> HiddenDebugKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "position", "chunkpos", "regpos", "orientation", "time", "datetime", "calendar", "worldtime", "latitude", "longitude"
    };

    public InformationDisplayPatches(ICoreClientAPI api, Harmony harmony)
    {
        this.api = api;
        active = this;
        Patch(harmony, "Vintagestory.Common.GameCalendar", "PrettyDate", nameof(KeepMonthAndDay), postfix: true);
        foreach (MethodInfo method in typeof(GuiElementDynamicTextHelper).GetMethods().Where(method => method.Name == "AddDynamicText"))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(InformationDisplayPatches), nameof(FilterInitialText)),
                postfix: new HarmonyMethod(typeof(InformationDisplayPatches), nameof(TrackDynamicText)));
        harmony.Patch(AccessTools.Method(typeof(GuiElementDynamicText), "SetNewText"),
            prefix: new HarmonyMethod(typeof(InformationDisplayPatches), nameof(FilterDynamicText)));
        harmony.Patch(AccessTools.Method(typeof(GuiElementDynamicText), "RenderInteractiveElements"),
            prefix: new HarmonyMethod(typeof(InformationDisplayPatches), nameof(RefreshDynamicText)));
        Patch(harmony, "Vintagestory.Client.NoObf.HudElementCoordinates", "OnRenderGUI", nameof(RenderCoordinates));
        Patch(harmony, "Vintagestory.GameContent.GuiDialogWorldMap", "OnMouseMove", nameof(TrackMapHover));
        harmony.Patch(AccessTools.Method(typeof(GuiElementHoverText), "SetNewText"),
            prefix: new HarmonyMethod(typeof(InformationDisplayPatches), nameof(FilterHoverText)));
        harmony.Patch(AccessTools.Method(typeof(GuiElementHoverText), "RenderInteractiveElements"),
            prefix: new HarmonyMethod(typeof(InformationDisplayPatches), nameof(RefreshHoverText)));
    }

    internal static void Patch(Harmony harmony, string typeName, string methodName, string patchName, bool postfix = false)
    {
        Type? type = AccessTools.TypeByName(typeName);
        if (type == null) return;
        MethodInfo? method = AccessTools.DeclaredMethod(type, methodName);
        if (method == null)
        {
            active?.api.Logger.Warning("[vsscpmod] Information display method missing: {0}.{1}", typeName, methodName);
            return;
        }
        var patch = new HarmonyMethod(typeof(InformationDisplayPatches), patchName) { priority = Priority.Last };
        if (postfix) harmony.Patch(method, postfix: patch);
        else harmony.Patch(method, prefix: patch);
    }

    private static void KeepMonthAndDay(IGameCalendar __instance, ref string __result)
    {
        IClientWorldAccessor? world = active?.api.World;
        if (!InformationRestrictions.ClientTimeRestricted || world == null || !ReferenceEquals(__instance, world.Calendar)) return;
        __result = MonthAndDay(__instance);
    }

    internal static string MonthAndDay(IGameCalendar calendar)
        => Lang.Get("vsscpmod:month-day", Lang.Get("month-" + calendar.MonthName),
            (int)(calendar.TotalDays % calendar.DaysPerMonth) + 1);

    private static string? TextKind(GuiComposer composer, string? key) => composer.DialogName switch
    {
        "debugScreenText" => "debug",
        "coordinateshud" => "coordinates",
        "environment" when key == "dyntext" => "date",
        _ => null
    };

    private static void FilterInitialText(GuiComposer composer, ref string text, string? key)
    {
        if (InformationRestrictions.ClientRestricted && TextKind(composer, key) is { } kind)
            text = active!.Filter(kind, text);
    }

    private static void TrackDynamicText(GuiComposer composer, string text, string? key)
    {
        if (active == null || key == null || TextKind(composer, key) is not { } kind) return;
        GuiElementDynamicText? element = composer.GetDynamicText(key);
        if (element != null) active.texts.GetValue(element, _ => new TrackedText(kind, text));
    }

    private static void FilterDynamicText(GuiElementDynamicText __instance, ref string text)
    {
        if (active == null) return;
        if (!active.texts.TryGetValue(__instance, out TrackedText? tracked))
            tracked = active.texts.GetValue(__instance, _ => new TrackedText("generic", string.Empty));
        tracked.Raw = text;
        if (InformationRestrictions.ClientRestricted) text = active.Filter(tracked.Kind, text);
    }

    private static void RefreshDynamicText(GuiElementDynamicText __instance)
    {
        if (active == null || !active.texts.TryGetValue(__instance, out TrackedText? tracked)) return;
        string wanted = InformationRestrictions.ClientRestricted ? active.Filter(tracked.Kind, tracked.Raw) : tracked.Raw;
        if (__instance.GetText() != wanted) __instance.SetNewText(tracked.Raw);
    }

    private static bool RenderCoordinates() => !InformationRestrictions.ClientCoordinatesRestricted;

    private static void TrackMapHover(GuiDialog __instance)
    {
        if (active == null || __instance.SingleComposer == null) return;
        GuiElementHoverText? hover = __instance.SingleComposer.GetHoverText("hoverText");
        if (hover != null) active.hovers.GetValue(hover, _ => new TrackedText("map", string.Empty));
    }

    private static void FilterHoverText(GuiElementHoverText __instance, ref string text)
    {
        if (active == null || !active.hovers.TryGetValue(__instance, out TrackedText? tracked)) return;
        tracked.Raw = text;
        if (InformationRestrictions.ClientCoordinatesRestricted) text = HideCoordinateTriples(text);
    }

    private static void RefreshHoverText(GuiElementHoverText __instance)
    {
        if (active == null || !active.hovers.TryGetValue(__instance, out TrackedText? tracked)) return;
        // Hover text does not expose its source text; SetNewText handles unchanged text cheaply.
        __instance.SetNewText(tracked.Raw);
    }

    private string Filter(string kind, string text)
    {
        if (kind == "coordinates") return InformationRestrictions.ClientCoordinatesRestricted ? string.Empty : text;
        if (kind == "date") return InformationRestrictions.ClientTimeRestricted ? RemoveClock(text) : text;
        if (kind == "generic")
        {
            if (InformationRestrictions.ClientTimeRestricted) text = RemoveClock(text);
            if (InformationRestrictions.ClientCoordinateNotificationsRestricted) text = HideCoordinateTriples(text);
            return text;
        }
        if (kind != "debug") return text;
        IClientWorldAccessor? world = api.World;
        if (world != null && AccessTools.Field(world.GetType(), "DebugScreenInfo")?.GetValue(world)
            is Vintagestory.API.Datastructures.OrderedDictionary<string, string> entries)
        {
            foreach ((string key, string value) in entries)
            {
                if (InformationRestrictions.ClientDebugRestricted && HiddenDebugKeys.Contains(key) && value.Length > 0)
                    text = text.Replace(value, string.Empty, StringComparison.Ordinal);
            }
        }
        if (InformationRestrictions.ClientTimeRestricted) text = RemoveClock(text);
        if (InformationRestrictions.ClientCoordinatesRestricted) text = HideCoordinateTriples(text);
        return text;
    }

    internal static string RemoveClock(string text) => Clock.Replace(text, string.Empty);
    internal static string HideCoordinateTriples(string text)
        => CoordinateTriple.Replace(text, Lang.Get("vsscpmod:map-location-hidden"));

    public void Refresh()
    {
        foreach (var entry in texts) RefreshDynamicText(entry.Key);
        foreach (var entry in hovers) RefreshHoverText(entry.Key);
        foreach (GuiDialog dialog in api.Gui.OpenedGuis.ToArray())
        {
            if (InformationRestrictions.ClientImmersiveMapsRestricted && dialog.GetType().FullName == "ImmersiveMaps.Gui.GuiDialogEditWaypoint") dialog.TryClose();
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(active, this)) active = null;
        texts.Clear();
        hovers.Clear();
    }
}
