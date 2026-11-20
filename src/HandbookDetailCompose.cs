using System.Reflection;
using System.Reflection.Emit;
using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Better Handbook adds two groups of buttons after the vanilla page has been
// painted, repainting the entire page twice. Add those same buttons once the
// vanilla bounds are known, immediately before the first paint instead.
internal sealed class HandbookDetailCompose : IDisposable
{
    private const string HarmonyId = "vsscpmod.handbook-detail-compose";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreClientAPI api;
    private readonly Action<GuiDialogHandbook> bookmarks;
    private readonly Action<object> recipes;
    private static HandbookDetailCompose? current;
    [ThreadStatic] private static Composition? active;
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, GuiComposer> DetailGui =
        AccessTools.FieldRefAccess<GuiDialogHandbook, GuiComposer>("detailViewGui");
    private static readonly AccessTools.FieldRef<GuiComposer, Dictionary<string, GuiElement>> Elements =
        AccessTools.FieldRefAccess<GuiComposer, Dictionary<string, GuiElement>>("staticElements");
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, Stack<BrowseHistoryElement>> History =
        AccessTools.FieldRefAccess<GuiDialogHandbook, Stack<BrowseHistoryElement>>("browseHistory");
    private readonly Dictionary<string, int> slowReports = new();

    private HandbookDetailCompose(ICoreClientAPI api, MethodInfo bookmarks, MethodInfo recipes)
    {
        this.api = api;
        this.bookmarks = bookmarks.CreateDelegate<Action<GuiDialogHandbook>>();
        this.recipes = recipes.CreateDelegate<Action<object>>();
    }

    public static HandbookDetailCompose? TryCreate(ICoreClientAPI api)
    {
        current?.Dispose();
        var type = AccessTools.TypeByName("HandbookCache.HandbookBookmarkButtonPatch");
        if (type == null) return null;
        var bookmarks = AccessTools.Method(type, "Postfix");
        var recipes = AccessTools.Method(AccessTools.TypeByName("RecipeExplorer.AutoCraftSystem"), "InitDetailGuiPostfix");
        var addRecipes = AccessTools.Method(AccessTools.TypeByName("RecipeExplorer.AutoCraftSystem"), "AddAutoCraftButton");
        // These extensions depend on computed absolute bounds. Only relocate
        // the audited build; future versions keep their original lifecycle.
        if (type.Module.ModuleVersionId != new Guid("6fa232a5-fb01-4de4-9b37-139e87fe5a5b")
            || bookmarks == null || recipes == null || addRecipes == null
            || new[] { bookmarks, recipes, addRecipes }.Any(m => Harmony.GetPatchInfo(m)?.Owners.Any() == true))
        {
            api.Logger.Notification("[vsscpmod] Handbook single composition skipped: unrecognized or patched Better Handbook.");
            return null;
        }
        var owner = new HandbookDetailCompose(api, bookmarks, recipes);
        try
        {
            owner.harmony.Patch(bookmarks, transpiler: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(DeferRecompose)));
            owner.harmony.Patch(addRecipes, transpiler: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(DeferRecompose)));
            owner.harmony.Patch(AccessTools.Method(typeof(GuiComposer), nameof(GuiComposer.Compose)),
                transpiler: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(InsertButtonsBeforePaint)));
            owner.harmony.Patch(AccessTools.Method(typeof(GuiDialogHandbook), "initDetailGui"),
                prefix: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(Begin)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(Finish)) { priority = Priority.Last });
            foreach (var method in new[] {
                AccessTools.Method(typeof(GuiComposer), nameof(GuiComposer.Compose)),
                AccessTools.Method(typeof(GuiElementRichtext), nameof(GuiElementRichtext.CalcHeightAndPositions)),
                AccessTools.Method(typeof(GuiElementRichtext), nameof(GuiElementRichtext.Compose)) })
                owner.harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(BeginTiming)),
                    finalizer: new HarmonyMethod(typeof(HandbookDetailCompose), nameof(FinishTiming)));
            current = owner;
            api.Logger.Notification("[vsscpmod] Better Handbook single detail composition enabled.");
            return owner;
        }
        catch (Exception error)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Handbook single composition unavailable: {0}", error);
            return null;
        }
    }

    private static void Begin(GuiDialogHandbook __instance, out Composition? __state)
    {
        __state = active;
        if (current is { } owner) active = new Composition(owner, __instance);
    }

    private static Exception? Finish(Exception? __exception, Composition? __state)
    {
        var state = active;
        active = __state;
        if (state != null && Stopwatch.GetElapsedTime(state.Started).TotalMilliseconds >= 250)
        {
            var history = History(state.Dialog);
            string page = history?.TryPeek(out var entry) == true ? entry.Page?.PageCode ?? "<unknown>" : "<search>";
            state.Owner.slowReports.TryGetValue(page, out int count);
            if (count < 3 && (state.Owner.slowReports.Count < 64 || count > 0))
            {
                state.Owner.slowReports[page] = count + 1;
                state.Owner.api.Logger.Notification(
                    "[vsscpmod] Handbook GUI timing: page={0}, total={1:F0}ms, compose={2:F0}ms, layout={3:F0}ms, richtextPaint={4:F0}ms, buttons={5:F0}ms, composeCalls={6}",
                    page, Stopwatch.GetElapsedTime(state.Started).TotalMilliseconds, state.ComposeMs,
                    state.LayoutMs, state.PaintMs, state.ButtonMs, state.ComposeCalls);
            }
        }
        return __exception;
    }

    private static void BeginTiming(out long __state) => __state = active == null ? 0 : Stopwatch.GetTimestamp();

    private static Exception? FinishTiming(Exception? __exception, long __state, MethodBase __originalMethod)
    {
        if (__state == 0 || active is not { } state) return __exception;
        double elapsed = Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        if (__originalMethod.DeclaringType == typeof(GuiComposer)) { state.ComposeMs += elapsed; state.ComposeCalls++; }
        else if (__originalMethod.Name == nameof(GuiElementRichtext.CalcHeightAndPositions)) state.LayoutMs += elapsed;
        else state.PaintMs += elapsed;
        return __exception;
    }

    private static IEnumerable<CodeInstruction> DeferRecompose(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        var recompose = AccessTools.Method(typeof(GuiComposer), nameof(GuiComposer.ReCompose));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(recompose))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookDetailCompose), nameof(RecomposeUnlessBatching));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported Better Handbook recompose call.");
    }

    private static void RecomposeUnlessBatching(GuiComposer composer)
    {
        if (!ReferenceEquals(active?.Batching, composer)) composer.ReCompose();
    }

    private static IEnumerable<CodeInstruction> InsertButtonsBeforePaint(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        var drawing = AccessTools.Field(typeof(ElementBounds), nameof(ElementBounds.IsDrawingSurface));
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode != OpCodes.Stfld || !Equals(instruction.operand, drawing)) continue;
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookDetailCompose), nameof(AddButtons)));
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported GUI composition paint boundary.");
    }

    private static void AddButtons(GuiComposer composer)
    {
        var state = active;
        if (state == null || state.Added || !ReferenceEquals(state.Owner.api, composer.Api)
            || !ReferenceEquals(DetailGui(state.Dialog), composer)) return;
        state.Added = true;
        var elements = Elements(composer);
        var previous = new HashSet<GuiElement>(elements.Values, ReferenceEqualityComparer.Instance);
        state.Batching = composer;
        long started = Stopwatch.GetTimestamp();
        try
        {
            state.Owner.bookmarks(state.Dialog);
            state.Owner.recipes(state.Dialog);
        }
        finally
        {
            state.Batching = null;
            // Existing elements already completed BeforeCalcBounds. Repeating
            // it would lay out the whole rich-text page again. Only the newly
            // attached controls need initialization and absolute bounds.
            foreach (var element in elements.Values)
            {
                if (previous.Contains(element)) continue;
                element.BeforeCalcBounds();
                element.Bounds.CalcWorldBounds();
            }
            state.ButtonMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    public void Dispose()
    {
        harmony.UnpatchAll(HarmonyId);
        slowReports.Clear();
        if (ReferenceEquals(current, this)) current = null;
    }

    private sealed class Composition(HandbookDetailCompose owner, GuiDialogHandbook dialog)
    {
        public readonly HandbookDetailCompose Owner = owner;
        public readonly GuiDialogHandbook Dialog = dialog;
        public bool Added;
        public GuiComposer? Batching;
        public readonly long Started = Stopwatch.GetTimestamp();
        public double ComposeMs, LayoutMs, PaintMs, ButtonMs;
        public int ComposeCalls;
    }
}
