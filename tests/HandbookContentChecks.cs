using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

static class HandbookContentChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    private static int candidates;
    private static void Selected(ItemStack[] __result) => candidates += __result.Length;
    private static long unknownComparisons;
    private static void UnknownEquality() => unknownComparisons++;

    public static void Run(string root, string game)
    {
        if (OperatingSystem.IsWindows()) SetDllDirectory(Path.Combine(game, "Lib"));
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((m, a) => m.Name == "Get" ? a![0] : null);
        Lang.ChangeLanguage("en");
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((_, a) => { messages.Add(string.Join(" ", a ?? [])); return null; });
        var registry = new RecipeRegistrySystem();
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "GetModSystem" ? registry : null);
        var cache = new Dictionary<string, object>();
        var world = Proxy.Create<IClientWorldAccessor>((m, _) => m.Name switch {
            "get_FrameProfiler" => RuntimeHelpers.GetUninitializedObject(typeof(FrameProfilerUtil)),
            "get_FastSearchRecipesByIngredient" => new System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>(),
            _ => null
        });
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch {
            "get_World" => world, "get_Logger" => logger, "get_ModLoader" => loader, "get_ObjectCache" => cache,
            "get_ClassRegistry" => Proxy.Create<IClassRegistryAPI>((_, _) => null), _ => null
        });
        var type = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.HandbookDetailSafety", true)!;
        var begin = AccessTools.Method(type, "Begin");
        var finish = AccessTools.Method(type, "Finish");
        var add = AccessTools.Method(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "addStoredInInfo");
        var meal = AccessTools.Method(typeof(GuiHandbookMealRecipePage), "getIngredientStacks");
        var hooks = new Harmony("handbook-content.checks");
        var tool = Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/toolsmith-audit/Toolsmith.dll"));
        var equals = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.Equals),
            [typeof(ItemStack), typeof(ItemStack), typeof(string[])]);
        hooks.Patch(equals, prefix: new HarmonyMethod(AccessTools.Method(tool.GetType("Toolsmith.ToolTinkering.ToolTinkeringCraftingPatches"), "EqualsModularPartPrefix")));
        var komet = Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/handbook-inspection/komet/Komet.dll"));
        var cookingMatch = komet.GetType("Komet.World.CookingMatch", true)!;
        hooks.Patch(AccessTools.Method(typeof(CookingRecipeIngredient), "GetMatchingStack"),
            transpiler: new HarmonyMethod(AccessTools.Method(cookingMatch, "RewriteMatching")));
        if (((int)AccessTools.Field(cookingMatch, "_rewritten").GetValue(null)! & 2) == 0)
            throw new Exception("Actual Komet cooking matcher rewrite was not exercised.");
        var stacks = Enumerable.Range(1, 2400).Select(i => new ItemStack(new Item {
            ItemId = i, Code = new($"test:tool-{i}"), Tool = EnumTool.Axe
        })).ToArray();
        foreach (var stack in stacks) AccessTools.Field(typeof(CollectibleObject), "api").SetValue(stack.Collectible, api);
        var variant = stacks[0].Clone(); variant.Attributes.SetString("variant", "甲");
        var pie = new ItemStack(new BlockPie { BlockId = 2, Code = new("test:pie"), Tool = EnumTool.Axe });
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(pie.Collectible, api);
        stacks = stacks.Concat([stacks[0].Clone(), variant, new ItemStack(new Block { BlockId = 1, Code = new("test:block") }), pie]).ToArray();
        var target = new ItemStack(new BlockToolRack { BlockId = 9999, Code = new("test:rack") });
        var behavior = new CollectibleBehaviorHandbookTextAndExtraInfo(target.Collectible);
        AccessTools.Field(behavior.GetType(), "Api").SetValue(behavior, api);
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(target.Collectible, api);
        var ingredientFor = AccessTools.Method(behavior.GetType(), "addIngredientForInfo");
        RichTextComponentBase[] IngredientFor()
        {
            var components = new List<RichTextComponentBase>();
            ingredientFor.Invoke(behavior, [api, stacks, (ActionConsumable<string>)(_ => true), target,
                components, 0f, new List<ItemStack>(), new List<ItemStack>(), new List<ItemStack>(), false]);
            return components.ToArray();
        }
        // A valid non-pie output must survive while the async pie cache is absent.
        registry.CookingRecipes.Add(new CookingRecipe { Code = "ready-recipe", Ingredients = [
            new CookingRecipeIngredient { Code = "target", ValidStacks = [new CookingRecipeStack {
                Code = target.Collectible.Code, Type = EnumItemClass.Block, ResolvedItemstack = target
            }] } ], CooksInto = new JsonItemStack { ResolvedItemstack = stacks[0] } });
        cache["handbookpierecipes"] = Array.Empty<CookingRecipe>();
        var readyIngredients = IngredientFor();
        cache.Remove("handbookpierecipes");
        try { IngredientFor(); throw new Exception("Expected missing pie-cache crash before repair."); }
        catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { }
        Console.WriteLine("PASS: actual ingredient-for section reproduces missing pie-cache crash while other recipes are ready.");
        RichTextComponentBase[] Stored()
        {
            var components = new List<RichTextComponentBase>();
            add.Invoke(behavior, [api, stacks, (ActionConsumable<string>)(_ => true), target, components, 0f]);
            return components.ToArray();
        }
        string[] Items(IEnumerable<RichTextComponentBase> components) => components.OfType<SlideshowItemstackTextComponent>()
            .SelectMany(c => c.Itemstacks).Select(s => Convert.ToHexString(s.ToBytes())).ToArray();
        GuiHandbookMealRecipePage Page()
        {
            var page = (GuiHandbookMealRecipePage)RuntimeHelpers.GetUninitializedObject(typeof(GuiHandbookMealRecipePage));
            AccessTools.Field(page.GetType(), "cachedIngredientStacks").SetValue(page, new Dictionary<string, List<ItemStack>>());
            page.pageCode = "handbook-mealrecipe-test";
            return page;
        }
        var ingredient = new CookingRecipeIngredient { Code = "food", PortionSizeLitres = 0.5f,
            ValidStacks = [new CookingRecipeStack { Code = stacks[0].Collectible.Code, Type = EnumItemClass.Item,
                ResolvedItemstack = stacks[0], StackSize = 2 }] };
        List<ItemStack> Ingredients(GuiHandbookMealRecipePage page) => (List<ItemStack>)meal.Invoke(page, [api, ingredient, stacks])!;
        var watch = Stopwatch.StartNew();
        cache["handbookpierecipes"] = Array.Empty<CookingRecipe>();
        var expected = Stored();
        long originalMs = watch.ElapsedMilliseconds;
        cache.Remove("handbookpierecipes");
        try { Stored(); throw new Exception("Expected missing stored-in pie-cache crash before repair."); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentNullException) { }
        Console.WriteLine("PASS: actual stored-in section reproduces missing pie-cache crash.");
        var expectedMeal = Ingredients(Page()).Select(s => Convert.ToHexString(s.ToBytes())).ToArray();
        IDisposable? safety = null;
        try
        {
            safety = (IDisposable?)AccessTools.Method(type, "TryCreate").Invoke(null, [api]);
            if (safety == null) throw new Exception("Failed installation: " + string.Join(" | ", messages));
            hooks.Patch(AccessTools.Method(type, "MealCandidates"), postfix: new HarmonyMethod(typeof(HandbookContentChecks), nameof(Selected)));
            object?[] args = [new DummySlot(target), api, stacks, null];
            begin.Invoke(null, args);
            try
            {
                var pendingIngredients = IngredientFor();
                if (!Items(readyIngredients).SequenceEqual(Items(pendingIngredients))
                    || readyIngredients.Length != pendingIngredients.Length
                    || cache.ContainsKey("handbookpierecipes") || messages.Any(m => m.Contains("recovered")))
                    throw new Exception("Missing pie cache lost existing recipes, wrote shared cache or recovered an exception.");
                // A later publication must be read, not replaced with a permanently cached empty value.
                var published = new CookingRecipe[] { new() { Code = "published", CooksInto = new JsonItemStack { ResolvedItemstack = stacks[1] } } };
                cache["handbookpierecipes"] = published;
                IngredientFor();
                if (!ReferenceEquals(AccessTools.Method(type, "AvailableHandbookRecipes").Invoke(null, [api, "handbookpierecipes"]), published))
                    throw new Exception("Published recipes were not read on the next composition.");
                cache.Remove("handbookpierecipes");
                Console.WriteLine("PASS: pending pie cache preserves other recipe panels without recovery or shared cache mutation.");
                watch.Restart();
                var actual = Stored();
                if (messages.Any(m => m.Contains("recovered"))) throw new Exception("Stored-in recovered an exception with a pending pie cache.");
                Console.WriteLine($"Real stored-in loop with Toolsmith, {stacks.Length} stacks: original={originalMs}ms, indexed={watch.ElapsedMilliseconds}ms");
                if (!Items(expected).SequenceEqual(Items(actual)) || expected.Length != actual.Length)
                    throw new Exception("Stored-in panels changed order, duplicate handling, attributes or class.");
                var page = Page();
                var actualMeal = Ingredients(page);
                if (!expectedMeal.SequenceEqual(actualMeal.Select(s => Convert.ToHexString(s.ToBytes()))) || candidates >= stacks.Length / 100)
                    throw new Exception($"Meal scan changed matching or retained unrelated stacks: {candidates}/{stacks.Length}");
                actualMeal.Clear();
                if (Ingredients(page).Count != expectedMeal.Length) throw new Exception("Meal consumer mutated cached stacks.");
                Console.WriteLine($"PASS: real stored-in content is identical; real cold meal enumeration selects {candidates}/{stacks.Length}, cached clones survive destructive consumers.");
                hooks.Patch(equals, postfix: new HarmonyMethod(typeof(HandbookContentChecks), nameof(UnknownEquality)));
                unknownComparisons = 0;
                var unknown = Stored();
                if (!Items(expected).SequenceEqual(Items(unknown)) || unknownComparisons < 2_000_000)
                    throw new Exception($"Unknown equality fallback changed content or skipped original comparisons: {unknownComparisons}.");
                Console.WriteLine("PASS: unknown equality retains the original full comparisons.");
            }
            finally { finish.Invoke(null, [null, args[3], Array.Empty<RichTextComponentBase>()]); }
        }
        finally { safety?.Dispose(); hooks.UnpatchAll(hooks.Id); }
    }
}
