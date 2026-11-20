using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

static class HandbookCacheChecks
{
    public static void Run(string root, string betterDll)
    {
        string culinaryPath = Path.Combine(root, "tests/dependencies/handbook-inspection/foods/culinary/ACulinaryArtillery.dll");
        Assembly? culinary = File.Exists(culinaryPath) ? Assembly.LoadFrom(culinaryPath) : null;
        string previousPath = GamePaths.DataPath;
        string directory = Path.Combine(root, "artifacts/handbook-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        GamePaths.DataPath = directory;
        var translations = new Dictionary<string, string> { ["test"] = "中文手册" };
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((m, a) => m.Name switch
        {
            "GetAllEntries" => translations, "Get" => a![0], _ => null
        });
        Lang.AvailableLanguages["zh-cn"] = Lang.AvailableLanguages["en"];
        Lang.ChangeLanguage("en");
        var messages = new List<string>();
        var tasks = new ConcurrentQueue<Action>();
        void NextTask() { if (!tasks.TryDequeue(out var task)) throw new Exception("Missing GUI task"); task(); }
        var logger = Proxy.Create<ILogger>((m, a) =>
        {
            messages.Add(string.Join(" ", a ?? []));
            return null;
        });
        var events = Proxy.Create<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "EnqueueMainThreadTask") tasks.Enqueue((Action)a![0]!);
            return null;
        });
        var item = new DescriptionItem { Code = new AssetLocation("test:cache"), ItemId = 1 };
        var collectibles = new List<CollectibleObject> { item };
        var config = new TreeAttribute();
        string worldId = "world-one", playerId = "player-one";
        int seed = 123;
        var playerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.NoObf.ClientPlayer", true)!;
        var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(playerType);
        var playerDataType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.NoObf.ClientWorldPlayerData", true)!;
        object playerData = RuntimeHelpers.GetUninitializedObject(playerDataType);
        AccessTools.Field(playerDataType, "PlayerUID").SetValue(playerData, playerId);
        AccessTools.Field(playerType, "worlddata").SetValue(player, playerData);
        var random = new Random(1);
        var blocks = Proxy.Create<IBlockAccessor>((m, _) => m.Name == "GetBlock"
            ? new BlockPie { BlockId = 900, Code = new("game:pie-perfect") } : null);
        IClientWorldAccessor World() => Proxy.Create<IClientWorldAccessor>((m, a) => m.Name switch
        {
            "get_Collectibles" => collectibles, "get_Config" => config,
            "GetItem" => collectibles.OfType<Item>().FirstOrDefault(i => a![0] is AssetLocation loc ? i.Code.Equals(loc) : i.Id == (int)a![0]!),
            "GetBlock" => collectibles.OfType<Block>().FirstOrDefault(i => a![0] is AssetLocation loc ? i.Code.Equals(loc) : i.Id == (int)a![0]!),
            "get_SavegameIdentifier" => worldId, "get_Seed" => seed, "get_Player" => player,
            "get_Rand" => random, "get_BlockAccessor" => blocks, _ => null
        });
        var world = World();
        var assets = new Dictionary<AssetLocation, IAsset>();
        byte[] assetData = Encoding.UTF8.GetBytes("{}");
        assets[new AssetLocation("test:config/test.json")] = Proxy.Create<IAsset>((m, _) => m.Name == "get_Data" ? assetData : null);
        var assetManager = Proxy.Create<IAssetManager>((m, _) => m.Name == "get_AllAssets" ? assets : null);
        var registry = new RecipeRegistrySystem();
        object? culinaryRegistry = culinary == null ? null : Activator.CreateInstance(culinary.GetType("ACulinaryArtillery.ACARecipeRegistrySystem", true)!);
        var objectCache = new Dictionary<string, object>();
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name switch
        {
            "get_Mods" => Array.Empty<Mod>(), "get_Systems" => Array.Empty<ModSystem>(),
            "GetModSystem" => m.IsGenericMethod && m.GetGenericArguments()[0].FullName == "ACulinaryArtillery.ACARecipeRegistrySystem"
                ? culinaryRegistry : registry, _ => null
        });
        bool shuttingDown = false;
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Logger" => logger, "get_Event" => events,
            "get_ModLoader" => loader, "get_Assets" => assetManager, "get_IsShuttingDown" => shuttingDown,
            "get_ObjectCache" => objectCache, _ => null
        });
        Type patchType = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.HandbookSearchCache", true)!;
        var begin = AccessTools.Method(patchType, "BeginBuild");
        var end = AccessTools.Method(patchType, "EndBuild");
        var ready = AccessTools.Method(patchType, "PagesReady");
        var create = AccessTools.Method(patchType, "TryCreate");
        var fingerprint = AccessTools.Method(patchType, "Fingerprint");
        var cachePath = Path.Combine(directory, "Cache/vsscpmod-handbook");
        var load = AccessTools.Method(typeof(GuiDialogHandbook), "LoadPages_Async");
        int count = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL: " + label);
            count++; Console.WriteLine("PASS: " + label);
        }
        object Begin()
        {
            object?[] args = [api, null]; begin.Invoke(null, args);
            Check(args[1] != null, "search generation starts with a validated cache");
            return args[1]!;
        }
        void End(object state, Exception? exception = null) => end.Invoke(null, [exception, state]);
        ItemStack Stack(string variant = "甲")
        {
            var stack = new ItemStack(item); stack.Attributes.SetString("variant", variant); return stack;
        }
        IDisposable? patch = null;
        var better = new Harmony("handbook-checks.betterhandbook");
        var refreshSpy = new Harmony("handbook-checks.refresh");
        try
        {
            // Use Better Handbook's actual invalidation hook beside our patch.
            if (!File.Exists(betterDll)) throw new FileNotFoundException("Extract Better Handbook 1.0.9 for compatibility checks.", betterDll);
            var betterAssembly = Assembly.LoadFrom(betterDll);
            var betterPostfix = AccessTools.Method(betterAssembly.GetType("HandbookCache.HandbookLoadPagesCacheInvalidationPatch", true), "Postfix");
            better.Patch(load, postfix: new HarmonyMethod(betterPostfix));
            patch = (IDisposable?)create.Invoke(null, [api]);
            Check(patch != null && !messages.Any(m => m.Contains("unavailable")), "patch installs against actual VS 1.22.7 alongside Better Handbook 1.0.9");
            Check(Harmony.GetPatchInfo(load)!.Owners.Contains("handbook-checks.betterhandbook"), "Better Handbook completion hook remains installed");

            object state = Begin();
            var cold = new GuiHandbookItemStackPage(api, Stack());
            var secondVariant = new GuiHandbookItemStackPage(api, Stack("乙"));
            End(state);
            Check(item.Calls == 2 && cold.TextCacheAll.Contains("甲") && secondVariant.TextCacheAll.Contains("乙"), "cold index generates distinct Chinese variant descriptions");
            string path = Directory.GetFiles(cachePath, "*.json").Single();
            Check(File.Exists(path), "completed index persists on disk");
            Check(!File.ReadAllText(path).Contains("Inventory"), "disk snapshot contains descriptions without world or GUI objects");

            world = World(); // Same save, a new connection and new world reference.
            state = Begin();
            var warmStack = Stack();
            var warm = new GuiHandbookItemStackPage(api, warmStack);
            var warmVariant = new GuiHandbookItemStackPage(api, Stack("乙"));
            End(state);
            Check(item.Calls == 2 && warm.TextCacheAll == cold.TextCacheAll && warmVariant.TextCacheAll == secondVariant.TextCacheAll,
                "reconnect reads disk index without calling expensive GetHeldItemInfo");
            Check(ReferenceEquals(warm.Stack, warmStack) && !ReferenceEquals(warm.dummySlot, cold.dummySlot)
                && !ReferenceEquals(warm.unspoilableInventory, cold.unspoilableInventory), "warm pages use fresh stacks, slots and inventories");
            _ = Stack().GetDescription(world, new DummySlot(Stack()));
            Check(item.Calls == 3, "live tooltips/details still call the real description generator");
            _ = new GuiHandbookItemStackPage(api, Stack());
            Check(item.Calls == 4, "page construction outside the handbook build keeps original behavior");

            string signature = (string)fingerprint.Invoke(null, [api])!;
            config.SetBool("changed", true);
            Check((string)fingerprint.Invoke(null, [api])! != signature, "server world config invalidates index");
            config.RemoveAttribute("changed");
            assetData = Encoding.UTF8.GetBytes("{\"changed\":true}");
            Check((string)fingerprint.Invoke(null, [api])! != signature, "patched JSON content invalidates index even with unchanged mod versions");
            assetData = Encoding.UTF8.GetBytes("{}");
            translations["test"] = "更新翻译";
            Check((string)fingerprint.Invoke(null, [api])! != signature, "runtime translation updates invalidate index");
            translations["test"] = "中文手册";
            seed++;
            Check((string)fingerprint.Invoke(null, [api])! != signature, "world seed changes invalidate index");
            seed--;
            item.Attributes = JsonObject.FromJson("{\"cacheTest\":true}");
            Check((string)fingerprint.Invoke(null, [api])! != signature, "runtime collectible attributes invalidate index");
            item.Attributes = null;
            item.ItemId++;
            Check((string)fingerprint.Invoke(null, [api])! != signature, "registry ID remapping invalidates serialized stack keys");
            item.ItemId--;
            Directory.CreateDirectory(Path.Combine(directory, "ModConfig"));
            string configFile = Path.Combine(directory, "ModConfig/test.json");
            File.WriteAllText(configFile, "{\"changed\":true}");
            Check((string)fingerprint.Invoke(null, [api])! != signature, "client mod configuration changes invalidate index");
            File.Delete(configFile);
            assets[new AssetLocation("test:shapes/test.json")] = Proxy.Create<IAsset>((m, _) => m.Name == "get_Data" ? assetData : null);
            string bookmarkFile = Path.Combine(directory, "ModConfig/betterhandbook-bookmarks.json");
            File.WriteAllText(bookmarkFile, "{\"page\":\"test\"}");
            Check((string)fingerprint.Invoke(null, [api])! == signature,
                "lazy render assets, handbook bookmarks do not invalidate search text");

            File.WriteAllText(path, "broken json");
            state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack()); End(state);
            Check(item.Calls == 5 && messages.Any(m => m.Contains("will rebuild")), "corrupted cache falls back to generation and reports recovery");
            string before = File.ReadAllText(path);
            state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack("丙"));
            var failure = new InvalidOperationException("original failure");
            Check(ReferenceEquals(end.Invoke(null, [failure, state]), failure) && File.ReadAllText(path) == before,
                "failed generation preserves the last complete cache and original exception");
            state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack("丁")); shuttingDown = true; End(state);
            shuttingDown = false;
            Check(File.ReadAllText(path) == before, "disconnect/shutdown does not replace the complete snapshot");
            Lang.ChangeLanguage("zh-cn"); state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack()); End(state);
            Check(Directory.GetFiles(cachePath, "*.json").Length == 2, "languages have separate persistent snapshots");
            Lang.ChangeLanguage("en"); worldId = "world-two";
            state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack()); End(state);
            Check(Directory.GetFiles(cachePath, "*.json").Length == 3, "different worlds have separate snapshots");
            playerId = "player-two"; AccessTools.Field(playerDataType, "PlayerUID").SetValue(playerData, playerId);
            state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack()); End(state);
            Check(Directory.GetFiles(cachePath, "*.json").Length == 4, "player dependent search text is isolated by player ID");

            worldId = "actual-pipeline";
            objectCache["handbookallstacks"] = new[] { Stack() };
            objectCache["handbookpierecipes"] = Array.Empty<CookingRecipe>();
            var system = new ModSystemSurvivalHandbook();
            AccessTools.Field(typeof(ModSystemSurvivalHandbook), "capi").SetValue(system, api);
            var actualBuild = AccessTools.Method(typeof(ModSystemSurvivalHandbook), "onCreatePagesAsync");
            int callsBefore = item.Calls;
            var realCold = (List<GuiHandbookPage>)actualBuild.Invoke(system, null)!;
            var realWarm = (List<GuiHandbookPage>)actualBuild.Invoke(system, null)!;
            Check(item.Calls == callsBefore + 1 && realCold.Count == 1 && realWarm.Count == 1,
                "actual patched survival handbook pipeline generates once and reuses persisted search text");
            Check(((GuiHandbookItemStackPage)realCold[0]).TextCacheAll == ((GuiHandbookItemStackPage)realWarm[0]).TextCacheAll,
                "actual cold and warm pipelines produce equivalent search text");
            _ = new GuiHandbookItemStackPage(api, Stack());
            Check(item.Calls == callsBefore + 2, "actual Harmony finalizer restores build context after completion");

            // Exercise the actual patched completion method without constructing
            // an OpenGL GUI; fake only the final FilterItems GUI operation.
            var dialog = (GuiDialogSurvivalHandbook)RuntimeHelpers.GetUninitializedObject(typeof(GuiDialogSurvivalHandbook));
            AccessTools.Field(typeof(GuiDialog), "capi").SetValue(dialog, api);
            AccessTools.Field(typeof(GuiDialog), "opened").SetValue(dialog, true);
            AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages").SetValue(dialog, new List<GuiHandbookPage>());
            AccessTools.Field(typeof(GuiDialogHandbook), "pageNumberByPageCode").SetValue(dialog, new Dictionary<string, int>());
            AccessTools.Field(typeof(GuiDialogHandbook), "createPageHandlerAsync").SetValue(dialog,
                (OnCreatePagesDelegate)(() => new List<GuiHandbookPage>()));
            AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync").SetValue(dialog, true);
            refreshCount = 0;
            refreshSpy.Patch(AccessTools.Method(typeof(GuiDialogHandbook), "FilterItems"),
                prefix: new HarmonyMethod(typeof(HandbookCacheChecks), nameof(CountRefresh)));
            load.Invoke(dialog, null);
            Check(tasks.Count == 1 && refreshCount == 0 && !(bool)AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync").GetValue(dialog)!,
                "actual LoadPages_Async plus Better Handbook hook enqueue refresh after completion");
            NextTask();
            Check(refreshCount == 1, "ready notification refreshes an already open handbook on the main thread");
            ready.Invoke(null, [dialog]); AccessTools.Field(typeof(GuiDialog), "opened").SetValue(dialog, false); NextTask();
            Check(refreshCount == 1, "closed handbooks skip unnecessary GUI work");
            HandbookLoadingChecks.Run(api, tasks, betterAssembly, callback => refreshAction = callback, Check, culinary, culinaryRegistry);
            HandbookRecipeChecks.Run(root, api, collectibles, Begin, End, Check, culinary);

            var snapshots = Directory.GetFiles(cachePath, "*.json").ToDictionary(p => p, File.ReadAllBytes);
            state = Begin(); _ = new GuiHandbookItemStackPage(api, Stack("incomplete-build"));
            AccessTools.Method(patchType, "InvalidateBuild").Invoke(null, null); End(state);
            Check(snapshots.All(p => p.Value.SequenceEqual(File.ReadAllBytes(p.Key)))
                && Directory.GetFiles(cachePath, "*.json").Length == snapshots.Count,
                "isolated entry failure never replaces the last complete on-disk description snapshot");
            AccessTools.Field(typeof(GuiDialog), "opened").SetValue(dialog, true); ready.Invoke(null, [dialog]);
            int beforeDispose = refreshCount;
            patch!.Dispose(); NextTask();
            Check(refreshCount == beforeDispose, "queued GUI refresh becomes inert after disposal");
            Check(Harmony.GetPatchInfo(load)?.Owners.Contains("vsscpmod.handbook-search-cache") != true
                && Harmony.GetPatchInfo(load)?.Owners.Contains("handbook-checks.betterhandbook") == true,
                "disposal removes only our patches and preserves Better Handbook");
            Console.WriteLine($"{count} handbook checks passed.");
        }
        finally
        {
            patch?.Dispose(); better.UnpatchAll(better.Id); refreshSpy.UnpatchAll(refreshSpy.Id);
            GamePaths.DataPath = previousPath;
        }
    }

    private static int refreshCount;
    private static Action<GuiDialogHandbook>? refreshAction;
    private static bool CountRefresh(GuiDialogHandbook __instance) { refreshCount++; refreshAction?.Invoke(__instance); return false; }

    private sealed class DescriptionItem : Item
    {
        public int Calls;
        public override string GetHeldItemName(ItemStack stack) => "中文测试";
        public override void GetHeldItemInfo(ItemSlot slot, StringBuilder text, IWorldAccessor world, bool debug)
        {
            Calls++; text.Append("完整说明 ").Append(slot.Itemstack!.Attributes.GetString("variant"));
        }
    }
}
