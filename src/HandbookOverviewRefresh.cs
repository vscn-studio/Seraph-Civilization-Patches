using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Preserve the size of an expanded result list when a streamed batch
// invalidates Better Handbook's filter. Preserve content pixels rather than
// the scrollbar handle's fraction when the list grows.
internal static class HandbookOverviewRefresh
{
    [ThreadStatic] private static Refresh? active;
    private static readonly FieldInfo Overview = AccessTools.Field(typeof(GuiDialogHandbook), "overviewGui");
    private static readonly FieldInfo Shown = AccessTools.Field(typeof(GuiDialogHandbook), "shownHandbookPages");
    private static readonly FieldInfo VisibleHeight = AccessTools.Field(typeof(GuiElementScrollbar), "visibleHeight");
    private static readonly FieldInfo TotalHeight = AccessTools.Field(typeof(GuiElementScrollbar), "totalHeight");

    internal static void Install(Harmony harmony)
    {
        Type? filter = AccessTools.TypeByName("HandbookCache.HandbookFilterCachePatch");
        if (filter?.Module.ModuleVersionId != new Guid("6fa232a5-fb01-4de4-9b37-139e87fe5a5b")) return;
        harmony.Patch(AccessTools.Method(filter, "EnsureLazyEmptyLoaded"),
            prefix: new HarmonyMethod(typeof(HandbookOverviewRefresh), nameof(KeepLoaded)));
        harmony.Patch(AccessTools.Method(filter, "ApplyFilter"),
            transpiler: new HarmonyMethod(typeof(HandbookOverviewRefresh), nameof(KeepSearchLoaded)));
        harmony.Patch(AccessTools.Method(filter, "AppendMoreResults"),
            prefix: new HarmonyMethod(typeof(HandbookOverviewRefresh), nameof(BeginAppend)),
            finalizer: new HarmonyMethod(typeof(HandbookOverviewRefresh), nameof(EndAppend)));
        harmony.Patch(AccessTools.Method(typeof(GuiElementScrollbar), "SetNewTotalHeight"),
            transpiler: new HarmonyMethod(typeof(HandbookOverviewRefresh), nameof(KeepPosition)));
    }

    internal static void Run(GuiDialog dialog, Action update)
    {
        if (dialog is not GuiDialogSurvivalHandbook handbook) { update(); return; }
        Refresh? previous = active;
        active = Capture(handbook, ((List<IFlatListItem>?)Shown.GetValue(handbook))?.Count ?? 0);
        try { update(); }
        finally { active = previous; }
    }

    private static Refresh Capture(GuiDialogHandbook dialog, int count)
    {
        var scrollbar = ((GuiComposer?)Overview.GetValue(dialog))?.GetScrollbar("scrollbar");
        return new Refresh(scrollbar, scrollbar?.CurrentYPosition ?? 0, count);
    }

    private static void BeginAppend(GuiDialogHandbook dialog, out Refresh? __state)
    {
        __state = active;
        if (dialog is GuiDialogSurvivalHandbook) active = Capture(dialog, 0);
    }

    private static Exception? EndAppend(Exception? __exception, Refresh? __state)
    {
        active = __state;
        return __exception;
    }

    private static void KeepLoaded(ref int __2) => __2 = Math.Max(__2, active?.Count ?? 0);

    private static int SearchCount(int batch, int total) => Math.Min(Math.Max(batch, active?.Count ?? 0), total);

    private static IEnumerable<CodeInstruction> KeepSearchLoaded(IEnumerable<CodeInstruction> instructions)
    {
        var min = AccessTools.Method(typeof(Math), nameof(Math.Min), [typeof(int), typeof(int)]);
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(min))
            {
                instruction.operand = AccessTools.Method(typeof(HandbookOverviewRefresh), nameof(SearchCount));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported handbook search batch.");
    }

    private static IEnumerable<CodeInstruction> KeepPosition(IEnumerable<CodeInstruction> instructions)
    {
        var trigger = AccessTools.Method(typeof(GuiElementScrollbar), nameof(GuiElementScrollbar.TriggerChanged));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(trigger))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookOverviewRefresh), nameof(RestoreAndTrigger));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported scrollbar height update.");
    }

    private static void RestoreAndTrigger(GuiElementScrollbar scrollbar)
    {
        if (active is { } scope && ReferenceEquals(scope.Scrollbar, scrollbar))
        {
            float maximum = Math.Max(0, (float)TotalHeight.GetValue(scrollbar)! - (float)VisibleHeight.GetValue(scrollbar)!);
            float position = float.IsFinite(scope.Position) ? Math.Clamp(scope.Position, 0, maximum) : 0;
            if (scrollbar.ScrollConversionFactor > 0 && float.IsFinite(scrollbar.ScrollConversionFactor))
                scrollbar.CurrentYPosition = position;
        }
        scrollbar.TriggerChanged();
    }

    private sealed record Refresh(GuiElementScrollbar? Scrollbar, float Position, int Count);
}
