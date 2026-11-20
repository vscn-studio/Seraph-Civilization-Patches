using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

static class HandbookRecipeChecks
{
    public static void Run(string root, ICoreClientAPI api, List<CollectibleObject> collectibles, Func<object> begin,
        Action<object, Exception?> end, Action<bool, string> check, Assembly? culinary)
    {
        var raw = new Item { ItemId = 10, Code = new("test:food-raw") };
        var cooked = new Item { ItemId = 11, Code = new("test:food-cooked") };
        var block = new Block { BlockId = 10, Code = new("test:food-block") };
        var custom = new CustomEqualityItem { ItemId = 12, Code = new("test:custom") };
        var liquid = new Item { ItemId = 13, Code = new("test:liquid"),
            Attributes = JsonObject.FromJson("{\"waterTightContainerProps\":{\"itemsPerLitre\":100}}") };
        collectibles.AddRange([raw, cooked, block, custom, liquid]);
        for (int i = 0; i < 200; i++) collectibles.Add(new Item { ItemId = i + 100, Code = new($"test:unrelated-{i}") });
        foreach (var collectible in collectibles) AccessTools.Field(typeof(CollectibleObject), "api").SetValue(collectible, api);
        var variant = new ItemStack(raw); variant.Attributes.SetString("variant", "甲");
        var stacks = new[] { new ItemStack(raw), variant, new ItemStack(cooked), new ItemStack(block), new ItemStack(custom), new ItemStack(liquid) }
            .Concat(collectibles.Where(c => c.Id >= 100).Select(c => new ItemStack(c))).ToArray();
        api.ObjectCache["handbookallstacks"] = stacks;
        var recipe = new CookingRecipe
        {
            Code = "candidate-checks",
            Ingredients = [
                new CookingRecipeIngredient { Code = "exact", MinQuantity = 1, MaxQuantity = 4, ValidStacks = [
                    new CookingRecipeStack { Type = EnumItemClass.Item, Code = raw.Code, StackSize = 2,
                        CookedStack = new JsonItemStack { Type = EnumItemClass.Item, Code = cooked.Code, StackSize = 5 } }
                ] },
                new CookingRecipeIngredient { Code = "wildcard", MinQuantity = 0, MaxQuantity = 4, ValidStacks = [
                    new CookingRecipeStack { Type = EnumItemClass.Item, Code = new("test:food-*"), StackSize = 3 },
                    new CookingRecipeStack { Type = EnumItemClass.Item, Code = raw.Code, StackSize = 7 }
                ] },
                new CookingRecipeIngredient { Code = "attribute", MinQuantity = 1, MaxQuantity = 4, ValidStacks = [
                    new CookingRecipeStack { Type = EnumItemClass.Item, Code = raw.Code, StackSize = 9,
                        Attributes = JsonObject.FromJson("{\"variant\":\"甲\"}") }
                ] },
                new CookingRecipeIngredient { Code = "liquid", MinQuantity = 1, MaxQuantity = 4, PortionSizeLitres = 0.5f, ValidStacks = [
                    new CookingRecipeStack { Type = EnumItemClass.Item, Code = liquid.Code, StackSize = 2 }
                ] }
            ]
        };
        var system = new ModSystemSurvivalHandbook();
        AccessTools.Field(typeof(ModSystemSurvivalHandbook), "capi").SetValue(system, api);
        var create = AccessTools.Method(typeof(ModSystemSurvivalHandbook), "CreateCachedMealRecipeStacks");
        string key = "valstacksbying-" + recipe.Code;
        create.Invoke(system, [recipe]);
        var baseline = (Dictionary<CookingRecipeIngredient, HashSet<ItemStack>>)api.ObjectCache[key];
        api.ObjectCache.Remove(key);
        object state = begin();
        try { create.Invoke(system, [recipe]); }
        finally { end(state, null); }
        var optimized = (Dictionary<CookingRecipeIngredient, HashSet<ItemStack>>)api.ObjectCache[key];
        string[] Values(Dictionary<CookingRecipeIngredient, HashSet<ItemStack>> cache) => cache.SelectMany(pair => pair.Value.Select(s =>
            pair.Key.Code + ":" + (s == null ? "null" : Convert.ToHexString(s.ToBytes())))).Order().ToArray();
        check(Values(baseline).SequenceEqual(Values(optimized)),
            "actual vanilla recipe cache keeps exact attributes, cooked matches, optional nulls, quantities and wildcard precedence");
        check(optimized.Single(p => p.Key.Code == "wildcard").Value.Any(s => s?.Class == EnumItemClass.Block),
            "wildcard ingredients retain vanilla cross-class matches");
        check(optimized.All(p => p.Value.Any(s => s?.Id == custom.Id)),
            "custom collectible equality retains matches outside the candidate ID");
        check(optimized.Single(p => p.Key.Code == "liquid").Value.Single(s => s.Id == liquid.Id).StackSize == 100,
            "actual recipe cache retains liquid items-per-litre and portion scaling");
        var candidates = AccessTools.Field(state.GetType(), "Candidates").GetValue(state)!;
        long scanned = (long)AccessTools.Property(candidates.GetType(), "Scanned").GetValue(candidates)!;
        long selected = (long)AccessTools.Property(candidates.GetType(), "Selected").GetValue(candidates)!;
        check(selected < scanned / 10, "recipe candidate index removes over 90% of unrelated comparisons in the fixture");
        // A mod patch may redefine matching; in that case preserve full scanning.
        var harmony = new Harmony("handbook-checks.custom-matcher");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(CookingRecipeIngredient), "GetMatchingStack"),
                postfix: new HarmonyMethod(typeof(HandbookRecipeChecks), nameof(MatcherHook)));
            var result = (ItemStack[])AccessTools.Method(candidates.GetType(), "For").Invoke(candidates, [recipe.Ingredients[0]])!;
            check(ReferenceEquals(result, stacks), "patched ingredient matchers fall back to the full original stack array");
        }
        finally { harmony.UnpatchAll(harmony.Id); }

        if (culinary == null) { Console.WriteLine("SKIP: ACA DLL not supplied; expanded-food compatibility checks unavailable."); return; }
        var matcher = culinary.GetType("ACulinaryArtillery.CookingRecipeIngredientPatcher", true)!;
        var helper = AccessTools.Method(culinary.GetType("ACulinaryArtillery.Util.HandbookInfoExtensions", true), "CreateCachedMealRecipeStacks");
        var aca = new Harmony("handbook-checks.actual-culinary");
        try
        {
            aca.Patch(AccessTools.Method(typeof(CookingRecipeIngredient), "GetMatchingStack"),
                prefix: new HarmonyMethod(AccessTools.Method(matcher, "displayFix")));
            aca.CreateReversePatcher(AccessTools.Method(typeof(CookingRecipeIngredient), "Resolve"),
                new HarmonyMethod(AccessTools.Method(matcher, "Resolve"))).Patch();
            // Real ACA matching ignores these additional attributes. Keep the
            // custom cross-ID equality item and wildcard/cooked/liquid cases.
            stacks[0].Attributes.SetString("madeWith", "expandedfoods:ingredient");
            stacks[0].Attributes.SetFloat("expandedSats", 42);
            var large = Enumerable.Range(20000, 12000).Select(i => (CollectibleObject)new Item
                { ItemId = i, Code = new($"test:large-pack-{i}") }).ToArray();
            collectibles.AddRange(large);
            api.ObjectCache["handbookallstacks"] = stacks.Concat(large.Select(c => new ItemStack(c))).ToArray();
            var batch = Enumerable.Range(0, 80).Select(i => new CookingRecipe
                { Code = "large-pack-recipe-" + i, Ingredients = recipe.Ingredients.Select(v => v.Clone()).ToArray() }).ToArray();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var baselineBatch = batch.Select(r => (Dictionary<CookingRecipeIngredient, HashSet<ItemStack>>)helper.Invoke(null, [api, r])!).ToArray();
            var acaBaseline = baselineBatch[0];
            long baselineMs = timer.ElapsedMilliseconds;
            foreach (var r in batch) api.ObjectCache.Remove("valstacksbying-" + r.Code);
            state = begin(); timer.Restart();
            Dictionary<CookingRecipeIngredient, HashSet<ItemStack>>[] fastBatch;
            try { fastBatch = batch.Select(r => (Dictionary<CookingRecipeIngredient, HashSet<ItemStack>>)helper.Invoke(null, [api, r])!).ToArray(); }
            finally { end(state, null); }
            long fastMs = timer.ElapsedMilliseconds;
            check(baselineBatch.Zip(fastBatch).All(pair => Values(pair.First).SequenceEqual(Values(pair.Second))),
                "actual ACA helper and patched matcher retain identical expanded attributes, cooked foods, wildcards, custom equality and liquid quantities");
            candidates = AccessTools.Field(state.GetType(), "Candidates").GetValue(state)!;
            scanned = (long)AccessTools.Property(candidates.GetType(), "Scanned").GetValue(candidates)!;
            selected = (long)AccessTools.Property(candidates.GetType(), "Selected").GetValue(candidates)!;
            check(selected < scanned / 100, "ACA mixing recipe path removes over 99% of unrelated candidates in a 12,206-stack pack");
            Console.WriteLine($"ACA fixture (80 recipes): baseline={baselineMs}ms, indexed={fastMs}ms, candidates={selected}/{scanned}");
            api.ObjectCache.Remove(key);
            state = begin();
            try { create.Invoke(system, [recipe]); }
            finally { end(state, null); }
            check(Values(acaBaseline).SequenceEqual(Values((Dictionary<CookingRecipeIngredient, HashSet<ItemStack>>)api.ObjectCache[key])),
                "vanilla cooking/pie cache also keeps ACA matching semantics with candidate narrowing enabled");
            aca.Patch(AccessTools.Method(typeof(CookingRecipeIngredient), "GetMatchingStack"),
                postfix: new HarmonyMethod(typeof(HandbookRecipeChecks), nameof(MatcherHook)));
            var full = (ItemStack[])AccessTools.Method(candidates.GetType(), "For").Invoke(candidates, [recipe.Ingredients[0]])!;
            check(ReferenceEquals(full, api.ObjectCache["handbookallstacks"]), "unknown patch alongside ACA still triggers safe full-scan fallback");
        }
        finally { aca.UnpatchAll(aca.Id); }
    }

    private static void MatcherHook() { }
    private sealed class CustomEqualityItem : Item
    {
        public override bool Equals(ItemStack current, ItemStack other, params string[] ignored) => true;
    }
}
