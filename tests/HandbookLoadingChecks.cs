using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

static class HandbookLoadingChecks
{
    private static readonly Queue<Action> workers = new();
    private sealed class HeadlessScrollbar(ICoreClientAPI api, Action<float> changed, ElementBounds bounds)
        : GuiElementScrollbar(api, changed, bounds)
    {
        public override void RecomposeHandle() { } // Texture drawing only; retain real scrolling and callbacks.
    }
    private static bool CaptureWorker(Action __0) { workers.Enqueue(__0); return false; }
    private static ManualResetEventSlim? recipeEntered, recipeRelease;
    private static bool failRecipe;
    private static void HoldRecipes()
    {
        recipeEntered?.Set();
        if (recipeRelease != null && !recipeRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        if (failRecipe) throw new InvalidOperationException("Simulated third-party recipe failure");
    }

    public static void Run(ICoreClientAPI api, ConcurrentQueue<Action> tasks, Assembly better,
        Action<Action<GuiDialogHandbook>?> setFilter, Action<bool, string> check, Assembly? culinary, object? culinaryRegistry)
    {
        var assembly = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly;
        var publisher = assembly.GetType("SeraphCivilizationPatches.HandbookPagePublisher", true)!;
        var queue = AccessTools.Method(publisher, "QueueLoad");
        var load = AccessTools.Method(typeof(GuiDialogHandbook), "LoadPages_Async");
        var all = AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages");
        var map = AccessTools.Field(typeof(GuiDialogHandbook), "pageNumberByPageCode");
        var loading = AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync");
        var create = AccessTools.Field(typeof(GuiDialogHandbook), "createPageHandlerAsync");
        var shown = AccessTools.Field(typeof(GuiDialogHandbook), "shownHandbookPages");
        var search = AccessTools.Field(typeof(GuiDialogHandbook), "currentSearchText");
        var apply = AccessTools.Method(better.GetType("HandbookCache.HandbookFilterCachePatch", true), "ApplyFilter");
        var harmony = new Harmony("handbook-checks.scheduler");
        void Drain() { while (tasks.TryDequeue(out var task)) task(); }
        var dialog = (GuiDialogSurvivalHandbook)RuntimeHelpers.GetUninitializedObject(typeof(GuiDialogSurvivalHandbook));
        AccessTools.Field(typeof(GuiDialog), "capi").SetValue(dialog, api);
        AccessTools.Field(typeof(GuiDialog), "opened").SetValue(dialog, true);
        var custom = new TestPage("guide");
        var list = new List<GuiHandbookPage> { custom };
        var displayed = new List<IFlatListItem>();
        all.SetValue(dialog, list); shown.SetValue(dialog, displayed);
        map.SetValue(dialog, new Dictionary<string, int> { [custom.PageCode] = 0 });
        void Schedule(OnCreatePagesDelegate handler)
        {
            create.SetValue(dialog, handler); loading.SetValue(dialog, true);
            queue.Invoke(null, [Delegate.CreateDelegate(typeof(Action), dialog, load), "test-handbook"]);
        }
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? worker = null;
        try
        {
            harmony.Patch(AccessTools.Method(typeof(TyronThreadPool), nameof(TyronThreadPool.QueueTask), [typeof(Action), typeof(string)]),
                prefix: new HarmonyMethod(typeof(HandbookLoadingChecks), nameof(CaptureWorker)));
            setFilter(d => apply.Invoke(null, [d]));
            check(PatchProcessor.GetCurrentInstructions(AccessTools.Method(typeof(GuiDialogHandbook), "loadEntries"))
                .Any(i => i.Calls(queue)), "actual handbook scheduler captures each load before its worker starts");
            var created = new List<GuiHandbookPage>();
            var meal = new TestPage("meal");
            Schedule(() =>
            {
                for (int i = 0; i < 256; i++)
                {
                    var stack = new ItemStack(api.World.Collectibles[0]);
                    stack.Attributes.SetString("variant", "stream" + i);
                    created.Add(new GuiHandbookItemStackPage(api, stack));
                    if (i == 127) { ready.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                }
                return new[] { meal }.Concat(created).ToList();
            });
            check(!(bool)loading.GetValue(dialog)!, "ready guide snapshot is browseable immediately while generation is pending");
            Drain();
            check(displayed.Count == 1 && ReferenceEquals(displayed[0], custom),
                "actual Better Handbook filter shows ready guides before the background task starts");
            worker = Task.Run(workers.Dequeue());
            check(ready.Wait(TimeSpan.FromSeconds(5)), "background generation reaches a blocked partial batch");
            check(list.Count == 1, "worker does not mutate GUI pages or page mappings");
            Drain();
            check(list.Count == 129 && displayed.Count == 129 && !worker.IsCompleted,
                "already open handbook displays 128 streamed items before generation finishes without reopening");
            search.SetValue(dialog, "stream127"); apply.Invoke(null, [dialog]);
            check(displayed.Count == 1, "actual Better Handbook search operates on the stable partial snapshot");
            release.Set();
            check(worker.Wait(TimeSpan.FromSeconds(10)), "background page generation completes");
            check(list.Count == 129, "completed worker still waits for main-thread publication");
            Drain();
            check(list.Count == 258 && ReferenceEquals(list[0], custom) && ReferenceEquals(list[1], meal)
                && list.Distinct().Count() == list.Count, "final publication restores original guide/recipe/item order without duplicates");
            search.SetValue(dialog, "stream255"); apply.Invoke(null, [dialog]);
            check(displayed.Count == 1 && ReferenceEquals(displayed[0], created[255]),
                "Better Handbook filter invalidation exposes final pages to search immediately");
            var mapping = (Dictionary<string, int>)map.GetValue(dialog)!;
            check(list.Select((p, i) => p.PageNumber == i && ReferenceEquals(list[mapping[p.PageCode]], p)).All(v => v),
                "published page numbers and links reference the current list");
            search.SetValue(dialog, null);
            apply.Invoke(null, [dialog]);
            var screen = ElementBounds.Fixed(0, 0, 1200, 800).WithParent(ElementBounds.Empty);
            screen.CalcWorldBounds();
            var bounds = ElementBounds.Fixed(0, 0, 20, 500).WithParent(screen);
            bounds.CalcWorldBounds();
            var positions = new List<float>();
            var scrollbar = new HeadlessScrollbar(api, positions.Add, bounds);
            var composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [api, screen, "refresh-check"], null)!;
            composer.AddInteractiveElement(scrollbar, "scrollbar");
            composer.AddInteractiveElement(new GuiElementFlatList(api, bounds, _ => { }, displayed), "stacklist");
            var overview = AccessTools.Field(typeof(GuiDialogHandbook), "overviewGui");
            overview.SetValue(dialog, composer);
            AccessTools.Field(typeof(GuiDialogHandbook), "listHeight").SetValue(dialog, 500d);
            scrollbar.SetHeights(500, 7205); scrollbar.CurrentYPosition = 2100; positions.Clear();
            AccessTools.Method(better.GetType("HandbookCache.HandbookFilterCachePatch"), "AppendMoreResults").Invoke(null, [dialog]);
            check(displayed.Count == 258, "actual Better Handbook expands beyond its initial 160 rows");
            check(positions.Count == 1 && Math.Abs(positions[0] - 2100) < 0.01,
                "actual lazy append preserves content pixels and emits only the corrected scrollbar callback");
            var refresh = assembly.GetType("SeraphCivilizationPatches.HandbookOverviewRefresh", true)!;
            void Refresh()
            {
                AccessTools.Method(refresh, "Run").Invoke(null, [dialog, (Action)(() => {
                    AccessTools.Method(better.GetType("HandbookCache.HandbookFilterCachePatch"), "Clear").Invoke(null, [dialog]);
                    apply.Invoke(null, [dialog]);
                })]);
            }
            list.Add(new TestPage("extra")); Refresh();
            check(displayed.Count == 258, "stream invalidation retains all expanded empty-search rows instead of shrinking to 160");
            check(Math.Abs(scrollbar.CurrentYPosition - 2100) < 0.01,
                "stream refresh preserves the overview scroll position");
            void RunRefresh(Action action) => AccessTools.Method(refresh, "Run").Invoke(null, [dialog, action]);
            positions.Clear(); RunRefresh(() => scrollbar.SetNewTotalHeight(1000));
            check(positions.Count == 1 && Math.Abs(positions[0] - 500) < 0.01,
                "shrinking overview clamps to the new end before notifying the list");
            var other = new HeadlessScrollbar(api, _ => { }, bounds);
            other.SetHeights(500, 1000); other.CurrentYPosition = 100;
            RunRefresh(() => other.SetNewTotalHeight(2000));
            check(Math.Abs(other.CurrentYPosition - 100) > 1,
                "detail and unrelated scrollbars keep the original height-update behavior");
            try { RunRefresh(() => throw new InvalidOperationException("scope-check")); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
            scrollbar.CurrentYPosition = 100; scrollbar.SetNewTotalHeight(2000);
            check(Math.Abs(scrollbar.CurrentYPosition - 100) > 1,
                "exception restores the previous refresh scope without leaking scroll interception");
            search.SetValue(dialog, "stream"); apply.Invoke(null, [dialog]);
            AccessTools.Method(better.GetType("HandbookCache.HandbookFilterCachePatch"), "AppendMoreResults").Invoke(null, [dialog]);
            Refresh();
            check(displayed.Count == 256, "stream invalidation also retains expanded text-search rows");
            search.SetValue(dialog, "stream255"); apply.Invoke(null, [dialog]);
            check(displayed.Count == 1, "a user search still resets the result batch normally");
            search.SetValue(dialog, null);
            overview.SetValue(dialog, null);

            // Hotkey updates and /debug reloadhandbook can schedule another
            // build while an earlier worker/publication is still pending.
            list.Clear(); list.Add(custom);
            var stale = new TestPage("stale"); var fresh = new TestPage("fresh");
            Schedule(() => new List<GuiHandbookPage> { stale });
            Schedule(() => new List<GuiHandbookPage> { fresh });
            workers.Dequeue()(); workers.Dequeue()(); Drain();
            check(list.Count == 2 && ReferenceEquals(list[1], fresh) && !list.Contains(stale),
                "reload cancels stale queued workers and publications");

            list.Clear(); list.Add(custom);
            var replacement = new TestPage("replacement");
            Schedule(() => new List<GuiHandbookPage> { replacement });
            workers.Dequeue()(); Drain();
            check(displayed.Count == 2 && displayed.Contains(replacement) && !displayed.Contains(fresh),
                "same-count reload invalidates Better Handbook cached results on the GUI thread");

            // Block the REAL original builder at its first recipe access. The
            // old implementation reached this before constructing any items.
            var recipeMethod = AccessTools.Method(typeof(ApiAdditions), "GetCookingRecipes");
            harmony.Patch(recipeMethod, prefix: new HarmonyMethod(typeof(HandbookLoadingChecks), nameof(HoldRecipes)));
            var system = new ModSystemSurvivalHandbook();
            AccessTools.Field(typeof(ModSystemSurvivalHandbook), "capi").SetValue(system, api);
            // Recompile the Harmony wrapper after patching the small accessor
            // so its earlier inlined body does not bypass the barrier.
            harmony.Patch(AccessTools.Method(typeof(ModSystemSurvivalHandbook), "onCreatePagesAsync"),
                prefix: new HarmonyMethod(typeof(HandbookLoadingChecks), nameof(BuildHook)));
            var actualBuilder = (OnCreatePagesDelegate)Delegate.CreateDelegate(typeof(OnCreatePagesDelegate), system,
                AccessTools.Method(typeof(ModSystemSurvivalHandbook), "onCreatePagesAsync"));
            list.Clear(); list.Add(custom); ready.Reset(); release.Reset();
            recipeEntered = ready; recipeRelease = release;
            Schedule(actualBuilder); worker = Task.Run(workers.Dequeue());
            check(ready.Wait(TimeSpan.FromSeconds(5)), "actual original builder reaches blocked recipe phase");
            Drain();
            int expected = ((ItemStack[])api.ObjectCache["handbookallstacks"]).Length;
            check(list.OfType<GuiHandbookItemStackPage>().Count() == expected && !worker.IsCompleted,
                "all items are visible before real recipe preparation completes, including batches below 128");
            var early = list.OfType<GuiHandbookItemStackPage>().ToArray();
            release.Set(); check(worker.Wait(TimeSpan.FromSeconds(10)), "real recipe phase resumes and completes"); Drain();
            check(list.OfType<GuiHandbookItemStackPage>().SequenceEqual(early),
                "original item loop reuses the already published page instances without duplicate constructors");

            list.Clear(); list.Add(custom); failRecipe = true;
            Schedule(actualBuilder); workers.Dequeue()(); Drain();
            check(list.OfType<GuiHandbookItemStackPage>().Count() == expected && displayed.Count == list.Count,
                "third-party recipe exception preserves and refreshes all ready item pages");
            failRecipe = false; recipeEntered = null; recipeRelease = null;
            harmony.Unpatch(recipeMethod, HarmonyPatchType.All, harmony.Id);

            var originalStacks = (ItemStack[])api.ObjectCache["handbookallstacks"];
            var badStack = new ItemStack(new BrokenItem { ItemId = 9876, Code = new("test:broken-description") });
            api.ObjectCache["handbookallstacks"] = new[] { badStack }.Concat(originalStacks).ToArray();
            list.Clear(); list.Add(custom);
            Schedule(actualBuilder); workers.Dequeue()(); Drain();
            check(list.OfType<GuiHandbookItemStackPage>().Count() == expected
                && list.OfType<GuiHandbookItemStackPage>().All(p => !ReferenceEquals(p.Stack, badStack)),
                "broken first item is isolated and original builder still completes with all healthy items");
            api.ObjectCache["handbookallstacks"] = originalStacks;

            // Malformed recipe cache data must not prevent the next recipe.
            var cacheRecipe = AccessTools.Method(typeof(ModSystemSurvivalHandbook), "CreateCachedMealRecipeStacks");
            bool continued = false;
            var brokenRecipe = new CookingRecipe { Code = "test-broken-recipe", Ingredients = null! };
            var healthyRecipe = new CookingRecipe { Code = "test-healthy-recipe", Ingredients = [] };
            list.Clear(); list.Add(custom);
            Schedule(() =>
            {
                cacheRecipe.Invoke(system, [brokenRecipe]);
                cacheRecipe.Invoke(system, [healthyRecipe]);
                continued = true;
                return new List<GuiHandbookPage>();
            });
            workers.Dequeue()(); Drain();
            check(continued && !api.ObjectCache.ContainsKey("valstacksbying-" + brokenRecipe.Code)
                && api.ObjectCache.ContainsKey("valstacksbying-" + healthyRecipe.Code),
                "malformed recipe is isolated without publishing an incomplete ingredient cache or stopping subsequent recipes");

            if (culinary != null)
            {
                var registry = api.ModLoader.GetModSystem<RecipeRegistrySystem>();
                var mixing = (List<CookingRecipe>)AccessTools.Field(culinaryRegistry!.GetType(), "MixingRecipes").GetValue(culinaryRegistry)!;
                var cooking = new CookingRecipe { Code = "stream-cooking", Ingredients = [] };
                var pie = new CookingRecipe { Code = "stream-pie", Ingredients = [] };
                var mixed = new CookingRecipe { Code = "stream-mixing", Ingredients = [] };
                var previousCrusts = BlockPie.TopCrustTypes;
                BlockPie.TopCrustTypes = [new PieTopCrustType { Code = "full", ShapeElement = "full" }];
                registry.CookingRecipes.AddRange([brokenRecipe, cooking]);
                mixing.AddRange([brokenRecipe, mixed]);
                api.ObjectCache["allMealBowls"] = new[] { new BlockMeal { BlockId = 901, Code = new("game:bowl-meal") } };
                api.ObjectCache["handbookpierecipes"] = new[] { pie };
                var builderMethod = AccessTools.Method(typeof(ModSystemSurvivalHandbook), "onCreatePagesAsync");
                var acaPostfix = AccessTools.Method(culinary.GetType("ACulinaryArtillery.HandbookCreatePagesPatch", true), "Postfix");
                harmony.Patch(builderMethod, postfix: new HarmonyMethod(acaPostfix));
                // Stop after vanilla recipes but before ACA adds mixing pages.
                harmony.Patch(builderMethod, postfix: new HarmonyMethod(typeof(HandbookLoadingChecks), nameof(HoldRecipes)) { priority = Priority.First });
                list.Clear(); list.Add(custom); ready.Reset(); release.Reset();
                recipeEntered = ready; recipeRelease = release;
                try
                {
                    Schedule(actualBuilder); worker = Task.Run(workers.Dequeue());
                    check(ready.Wait(TimeSpan.FromSeconds(5)), "original cooking and pie builders finish before blocked ACA postfix");
                    Drain();
                    var partialMeals = list.OfType<GuiHandbookMealRecipePage>().ToArray();
                    check(partialMeals.Any(p => p.Recipe == cooking) && partialMeals.All(p =>
                        api.ObjectCache.ContainsKey("valstacksbying-" + p.Recipe.Code)),
                        "real recipe pages publish only with ready ingredient caches while later mod hooks are blocked");
                    release.Set(); check(worker.Wait(TimeSpan.FromSeconds(10)), "actual ACA handbook postfix completes"); Drain();
                    var mealPages = list.OfType<GuiHandbookMealRecipePage>().ToArray();
                    check(mealPages.Select(p => p.Recipe.Code).SequenceEqual(new[] { cooking.Code, mixed.Code, pie.Code }),
                        "actual ACA mixing insertion preserves vanilla final ordering and isolates malformed cooking and mixing recipes");
                    check(list.OfType<GuiHandbookItemStackPage>().Count() == expected && list.Distinct().Count() == list.Count,
                        "full original and ACA builder chain retains all items without duplicate streamed pages");
                }
                finally
                {
                    release.Set(); worker?.Wait(TimeSpan.FromSeconds(10));
                    recipeEntered = null; recipeRelease = null;
                    registry.CookingRecipes.Clear(); mixing.Clear(); api.ObjectCache["handbookpierecipes"] = Array.Empty<CookingRecipe>();
                    BlockPie.TopCrustTypes = previousCrusts;
                    harmony.Unpatch(builderMethod, HarmonyPatchType.Postfix, harmony.Id);
                }
            }

            list.Clear(); list.Add(custom);
            Schedule(() => new List<GuiHandbookPage> { new TestPage("disposed") });
            workers.Dequeue()();
            AccessTools.Method(publisher, "Cancel").Invoke(null, [dialog]);
            Drain();
            check(list.Count == 1, "dialog disposal prevents queued publications from touching its GUI collections");

            var commands = (GuiDialogHandbook)RuntimeHelpers.GetUninitializedObject(typeof(GuiDialogHandbook));
            all.SetValue(commands, new List<GuiHandbookPage>()); map.SetValue(commands, new Dictionary<string, int>());
            create.SetValue(commands, (OnCreatePagesDelegate)(() => new List<GuiHandbookPage> { fresh }));
            loading.SetValue(commands, true);
            queue.Invoke(null, [Delegate.CreateDelegate(typeof(Action), commands, load), "commands"]);
            check((bool)loading.GetValue(commands)!, "command handbook retains its original loading behavior");
            workers.Dequeue()();
            check(((List<GuiHandbookPage>)all.GetValue(commands)!).Single() == fresh,
                "command handbook still runs the original page loader");
        }
        finally
        {
            release.Set(); worker?.Wait(TimeSpan.FromSeconds(10));
            recipeEntered = null; recipeRelease = null; failRecipe = false;
            setFilter(null); harmony.UnpatchAll(harmony.Id); workers.Clear(); Drain();
        }
    }

    private static void BuildHook() { }

    private sealed class BrokenItem : Item
    {
        public override string GetHeldItemName(ItemStack stack) => "Broken item";
        public override void GetHeldItemInfo(ItemSlot slot, StringBuilder text, IWorldAccessor world, bool debug)
            => throw new InvalidOperationException("Test broken description");
    }

    private sealed class TestPage(string name) : GuiHandbookPage
    {
        public override string PageCode => "test:" + name;
        public override string CategoryCode => "stack";
        public override bool IsDuplicate => false;
        public override float SearchWeightOffset => 0;
        public override PageText GetPageText() => new() { Title = name, Text = name };
        public override void Dispose() { }
        public override void RenderListEntryTo(ICoreClientAPI api, float dt, double x, double y, double width, double height) { }
        public override void ComposePage(GuiComposer gui, ElementBounds bounds, ItemStack[] stacks, Vintagestory.API.Common.ActionConsumable<string> open) { }
    }
}
