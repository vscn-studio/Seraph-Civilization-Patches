using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

// The worker owns new pages; the GUI owns the published list and page map.
// Never expose a list that the background generator is still mutating.
internal sealed class HandbookPagePublisher : IDisposable
{
    private static HandbookPagePublisher? current;
    [ThreadStatic] private static Session? active;
    private readonly ICoreClientAPI api;
    private readonly Type survivalDialog;
    private readonly FieldInfo pages, numbers, loading, handler;
    private readonly PropertyInfo code;
    private readonly FieldInfo number;
    private readonly MethodInfo filter;
    private readonly ConstructorInfo itemConstructor;
    private readonly ConditionalWeakTable<GuiDialog, Session> sessions = new();
    private readonly object workerGate = new();
    private volatile bool disposed;

    public HandbookPagePublisher(ICoreClientAPI api, Harmony harmony, Type dialog, Type survivalDialog, ConstructorInfo constructor)
    {
        this.api = api; this.survivalDialog = survivalDialog;
        itemConstructor = constructor;
        pages = AccessTools.Field(dialog, "allHandbookPages") ?? throw new MissingFieldException("allHandbookPages");
        numbers = AccessTools.Field(dialog, "pageNumberByPageCode") ?? throw new MissingFieldException("pageNumberByPageCode");
        loading = AccessTools.Field(dialog, "loadingPagesAsync") ?? throw new MissingFieldException("loadingPagesAsync");
        handler = AccessTools.Field(dialog, "createPageHandlerAsync") ?? throw new MissingFieldException("createPageHandlerAsync");
        Type page = AccessTools.TypeByName("Vintagestory.GameContent.GuiHandbookPage");
        code = AccessTools.Property(page, "PageCode") ?? throw new MissingMemberException("PageCode");
        number = AccessTools.Field(page, "PageNumber") ?? throw new MissingMemberException("PageNumber");
        filter = AccessTools.Method(dialog, "FilterItems");
        harmony.Patch(AccessTools.Method(dialog, "loadEntries"),
            transpiler: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(SchedulePublication)));
        harmony.Patch(AccessTools.Method(dialog, "LoadPages_Async"),
            prefix: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(Generate)));
        harmony.Patch(AccessTools.Method(dialog, "Dispose"),
            prefix: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(Cancel)));
        harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(ItemReady)));
        Type system = AccessTools.TypeByName("Vintagestory.GameContent.ModSystemSurvivalHandbook");
        harmony.Patch(AccessTools.Method(system, "onCreatePagesAsync"),
            transpiler: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(ItemsBeforeRecipes)));
        harmony.Patch(AccessTools.Method(system, "CreateCachedMealRecipeStacks"),
            finalizer: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(RecipeFailed)));
        Type? culinary = AccessTools.TypeByName("ACulinaryArtillery.HandbookCreatePagesPatch");
        if (culinary?.Module.ModuleVersionId == HandbookRecipeCandidates.CulinaryBuild)
        {
            harmony.Patch(AccessTools.Method(culinary, "Postfix"),
                transpiler: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(StreamRecipeAdds)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("ACulinaryArtillery.Util.HandbookInfoExtensions"), "CreateCachedMealRecipeStacks"),
                finalizer: new HarmonyMethod(typeof(HandbookPagePublisher), nameof(RecipeFailed)));
        }
        current = this;
    }

    // Keep the original builder and every mod's hooks. Pre-create its item
    // pages once, then reuse them when the original reaches its last loop.
    // Recipes retain their original final ordering and matching semantics.
    private static IEnumerable<CodeInstruction> ItemsBeforeRecipes(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(__originalMethod.DeclaringType, "capi"));
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookPagePublisher), nameof(WarmItems)));
        int replaced = 0;
        foreach (var instruction in StreamRecipeAdds(instructions))
        {
            if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo constructor
                && constructor.DeclaringType?.Name == "GuiHandbookItemStackPage")
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookPagePublisher), nameof(ReuseItem))
                    .MakeGenericMethod(constructor.DeclaringType);
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported handbook item loop.");
    }

    private static IEnumerable<CodeInstruction> StreamRecipeAdds(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && instruction.operand is ConstructorInfo ctor
                && ctor.DeclaringType?.Name == "GuiHandbookMealRecipePage")
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookPagePublisher), nameof(CreateMeal))
                    .MakeGenericMethod(ctor.GetParameters()[1].ParameterType, ctor.DeclaringType);
            }
            if (instruction.operand is MethodInfo setter && setter.Name == "set_Visible"
                && setter.DeclaringType?.Name == "GuiHandbookPage")
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookPagePublisher), nameof(SetVisible))
                    .MakeGenericMethod(setter.DeclaringType);
            }
            if (instruction.operand is MethodInfo method && method.DeclaringType?.IsGenericType == true
                && method.DeclaringType.GetGenericTypeDefinition() == typeof(List<>)
                && method.DeclaringType.GetGenericArguments()[0].Name == "GuiHandbookPage"
                && method.Name is "Add" or "Insert")
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookPagePublisher), method.Name == "Add" ? nameof(AddPage) : nameof(InsertPage))
                    .MakeGenericMethod(method.DeclaringType.GetGenericArguments());
            }
            yield return instruction;
        }
    }

    private static void WarmItems(ICoreClientAPI api)
    {
        Session? session = active;
        if (session == null) return;
        var stacks = Vintagestory.API.Util.ObjectCacheUtil.TryGet<ItemStack[]>(api, "handbookallstacks") ?? [];
        HandbookDetailSafety.WarmCandidates(api, stacks);
        var timer = Stopwatch.StartNew();
        api.Logger.Notification("[vsscpmod] Handbook items building: stacks={0}.", stacks.Length);
        foreach (var stack in stacks)
        {
            CheckCancellation();
            object page;
            try { session.CreatingItem = true; page = session.Owner.itemConstructor.Invoke([api, stack]); }
            catch (Exception exception)
            {
                session.FailedItems.Add(stack);
                ReportFailure("item " + stack.Collectible.Code, exception);
                continue;
            }
            finally { session.CreatingItem = false; }
            ItemReady(page);
            if (!session.Items.TryGetValue(stack, out var copies)) session.Items[stack] = copies = new();
            copies.Enqueue(page);
        }
        session.Queue(); // Publish even packs with fewer than 128 items.
        api.Logger.Notification("[vsscpmod] Handbook items ready: count={0}, failed={1}, elapsed={2}ms; preparing recipes.",
            stacks.Length - session.FailedItems.Count, session.FailedItems.Count, timer.ElapsedMilliseconds);
    }

    private static T ReuseItem<T>(ICoreClientAPI api, ItemStack stack)
    {
        CheckCancellation();
        if (active?.FailedItems.Contains(stack) == true) return default!;
        if (active?.Items.TryGetValue(stack, out var copies) == true && copies.Count > 0)
        {
            T page = (T)copies.Dequeue();
            if (copies.Count == 0) active.Items.Remove(stack);
            return page;
        }
        return (T)AccessTools.Constructor(typeof(T), [typeof(ICoreClientAPI), typeof(ItemStack)]).Invoke([api, stack]);
    }

    private static TPage CreateMeal<TRecipe, TPage>(ICoreClientAPI api, TRecipe recipe, int slots, bool pie)
    {
        CheckCancellation();
        try { return (TPage)AccessTools.Constructor(typeof(TPage), [typeof(ICoreClientAPI), typeof(TRecipe), typeof(int), typeof(bool)])
            .Invoke([api, recipe, slots, pie]); }
        catch (Exception exception) when (active != null && active.IsAlive)
        {
            active.FailedRecipes.Add(recipe!); ReportFailure("meal page " + RecipeCode(recipe!), exception); return default!;
        }
    }

    private static void SetVisible<T>(T page, bool visible)
    {
        if (page == null) return;
        // Reused item pages are already owned by the GUI; its active filter
        // owns Visible. Do not overwrite it from the worker's original loop.
        if (active?.Owner.itemConstructor.DeclaringType!.IsInstanceOfType(page) == true) return;
        AccessTools.Property(typeof(T), "Visible").SetValue(page, visible);
    }

    private static string? RecipeCode(object recipe) => AccessTools.Field(recipe.GetType(), "Code")?.GetValue(recipe)?.ToString();

    private static Exception? RecipeFailed(Exception? __exception, object[] __args)
    {
        if (__exception == null || active == null || !active.IsAlive || __exception.GetBaseException() is OperationCanceledException) return __exception;
        object recipe = __args[^1];
        active.FailedRecipes.Add(recipe);
        ReportFailure("recipe " + RecipeCode(recipe), __exception);
        return null;
    }

    private static void ReportFailure(string entry, Exception exception)
    {
        HandbookSearchCache.InvalidateBuild();
        active!.Owner.api.Logger.Warning("[vsscpmod] Handbook skipped broken {0}: {1}", entry, exception.GetBaseException());
    }

    private static bool CanPublish(object? page) => page != null && (active == null
        || active.Owner.itemConstructor.DeclaringType!.IsInstanceOfType(page)
        || AccessTools.Field(page.GetType(), "Recipe")?.GetValue(page) is not object recipe
        || !active.FailedRecipes.Contains(recipe));

    private static void AddPage<T>(List<T> list, T page) { if (CanPublish(page)) { list.Add(page); RecipeReady(page!); } }
    private static void InsertPage<T>(List<T> list, int index, T page)
    {
        int skipped = active?.SkippedInsertions.GetValueOrDefault(list) ?? 0;
        if (!CanPublish(page))
        {
            // ACA advances its insertion cursor even when this entry failed.
            if (active != null) active.SkippedInsertions[list] = skipped + 1;
            return;
        }
        list.Insert(index - skipped, page); RecipeReady(page!);
    }

    private static void RecipeReady(object page)
    {
        Session? session = active;
        if (session == null || !session.IsAlive || session.Owner.itemConstructor.DeclaringType!.IsInstanceOfType(page)) return;
        // Called only after the original builder has filled the ingredient
        // cache. Publishing at the meal constructor would move scans to render.
        lock (session.Gate)
        {
            session.Pending.Add(page);
            if (++session.ReadyRecipes == 1 || session.Timer.ElapsedMilliseconds >= 250) session.Queue();
        }
    }

    internal static void CheckCancellation()
    {
        if (active is { IsAlive: false }) throw new OperationCanceledException("Handbook load superseded or disposed.");
    }

    private static IEnumerable<CodeInstruction> SchedulePublication(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(TyronThreadPool)
                && method.Name == nameof(TyronThreadPool.QueueTask)
                && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Action), typeof(string) }))
            {
                instruction.operand = AccessTools.Method(typeof(HandbookPagePublisher), nameof(QueueLoad));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported handbook load scheduler.");
    }

    private static void QueueLoad(Action action, string name)
    {
        var owner = current;
        if (owner == null || owner.disposed || action.Target is not GuiDialog dialog
            || !owner.survivalDialog.IsInstanceOfType(dialog))
        {
            TyronThreadPool.QueueTask(action, name); return;
        }
        if (owner.sessions.TryGetValue(dialog, out var previous)) previous.Cancelled = true;
        var session = new Session(owner, dialog, ((IList)owner.pages.GetValue(dialog)!).Cast<object>().ToArray());
        owner.sessions.Remove(dialog); owner.sessions.Add(dialog, session);
        for (int i = 0; i < session.Custom.Length; i++) owner.number.SetValue(session.Custom[i], i);
        // Called on the GUI thread after initCustomPages. Its snapshot is now
        // safe to browse while the worker builds separate pages.
        owner.loading.SetValue(dialog, false);
        session.Queue();
        TyronThreadPool.QueueTask(() => Run(session, action), name);
    }

    private static void Run(Session session, Action action)
    {
        Session? previous = active;
        active = session;
        try { lock (session.Owner.workerGate) { if (session.IsAlive) action(); } }
        catch (Exception exception)
        {
            if (session.IsAlive)
            {
                session.Owner.api.Logger.Warning("[vsscpmod] Handbook generation interrupted; retaining ready pages. {0}", exception.GetBaseException());
                session.Queue();
            }
        }
        finally { active = previous; }
    }

    private static bool Generate(object __instance)
    {
        Session? session = active;
        if (session == null || !ReferenceEquals(session.Dialog, __instance)) return true;
        if (!session.IsAlive) return false;
        // Preserve the original create delegate and all other mods' patches on
        // it. Only replace vanilla's background writes into GUI collections.
        var result = (IEnumerable)((Delegate)session.Owner.handler.GetValue(__instance)!).DynamicInvoke()!;
        lock (session.Gate)
        {
            session.Complete = result.Cast<object>().ToArray();
            session.Queue();
        }
        return false;
    }

    private static void ItemReady(object __instance)
    {
        Session? session = active;
        if (session == null || !session.IsAlive || session.CreatingItem) return;
        lock (session.Gate)
        {
            session.Pending.Add(__instance);
            if (session.Pending.Count >= 128 && (session.PublishedItems == 0 || session.Timer.ElapsedMilliseconds >= 250))
                session.Queue();
        }
    }

    private static void Cancel(GuiDialog __instance)
    {
        if (current?.sessions.TryGetValue(__instance, out var session) == true) session.Cancelled = true;
    }

    private void Publish(Session session)
    {
        object[] pending;
        object[]? complete;
        lock (session.Gate)
        {
            session.Queued = false;
            if (!session.IsAlive) return;
            pending = session.Pending.ToArray(); session.Pending.Clear();
            complete = session.Complete;
            session.PublishedItems += pending.Length;
            session.Timer.Restart();
        }
        var list = (IList)pages.GetValue(session.Dialog)!;
        var map = (IDictionary)numbers.GetValue(session.Dialog)!;
        if (complete != null)
        {
            list.Clear(); map.Clear();
            foreach (object page in session.Custom.Concat(complete)) list.Add(page);
        }
        else foreach (object page in pending) list.Add(page);
        for (int i = complete != null ? 0 : list.Count - pending.Length; i < list.Count; i++)
        {
            object page = list[i]!;
            number.SetValue(page, i);
            map[code.GetValue(page)!] = i;
        }
        loading.SetValue(session.Dialog, false);
        // Better Handbook invalidates at LoadPages_Async return, before our
        // publication. Invalidate again here so same-count reloads also work.
        HandbookOverviewRefresh.Run(session.Dialog, () =>
        {
            AccessTools.Method(AccessTools.TypeByName("HandbookCache.HandbookFilterCachePatch"), "Clear")
                ?.Invoke(null, [session.Dialog]);
            if (session.Dialog.IsOpened()) filter.Invoke(session.Dialog, null);
        });
    }

    public void Dispose() { disposed = true; if (ReferenceEquals(current, this)) current = null; }

    private sealed class Session(HandbookPagePublisher owner, GuiDialog dialog, object[] custom)
    {
        public readonly HandbookPagePublisher Owner = owner;
        public readonly GuiDialog Dialog = dialog;
        public readonly object[] Custom = custom;
        public readonly object Gate = new();
        public readonly List<object> Pending = new();
        public readonly Dictionary<ItemStack, Queue<object>> Items = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<ItemStack> FailedItems = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<object> FailedRecipes = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<object, int> SkippedInsertions = new(ReferenceEqualityComparer.Instance);
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public object[]? Complete;
        public bool Queued;
        public bool CreatingItem;
        public int ReadyRecipes;
        public int PublishedItems;
        public volatile bool Cancelled;
        public bool IsAlive => !Cancelled && !Owner.disposed && !Owner.api.IsShuttingDown;

        public void Queue()
        {
            lock (Gate)
            {
                if (Queued || !IsAlive) return;
                Queued = true;
                Owner.api.Event.EnqueueMainThreadTask(() =>
                {
                    try { Owner.Publish(this); }
                    catch (Exception exception)
                    {
                        Owner.api.Logger.Warning("[vsscpmod] Handbook publication failed: {0}", exception.GetBaseException().Message);
                    }
                }, "vsscpmod-handbook-publish");
            }
        }
    }
}
