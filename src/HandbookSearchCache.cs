using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SeraphCivilizationPatches;

// Persist only the frozen search descriptions. Pages, stacks, inventories and GUI
// textures always belong to the current connection and are never cached on disk.
internal sealed class HandbookSearchCache : IDisposable
{
    private const string HarmonyId = "vsscpmod.handbook-search-cache";
    private const string DescriptionSemantics = "vsscpmod-handbook-description-v1";
    private static readonly string[] ContentPaths = ["blocktypes/", "itemtypes/", "recipes/", "config/", "lang/", "patches/", "entities/", "handbook/"];
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreClientAPI api;
    private MethodInfo filter = null!;
    private Type survivalDialog = null!;
    private HandbookPagePublisher? publisher;
    private static HandbookSearchCache? current;
    [ThreadStatic] private static Build? activeBuild;
    private volatile bool disposed;

    private HandbookSearchCache(ICoreClientAPI api) => this.api = api;

    public static HandbookSearchCache? TryCreate(ICoreClientAPI api)
    {
        var cache = new HandbookSearchCache(api);
        try
        {
            Type page = AccessTools.TypeByName("Vintagestory.GameContent.GuiHandbookItemStackPage")
                ?? throw new TypeLoadException("GuiHandbookItemStackPage");
            Type system = AccessTools.TypeByName("Vintagestory.GameContent.ModSystemSurvivalHandbook")
                ?? throw new TypeLoadException("ModSystemSurvivalHandbook");
            Type dialog = AccessTools.TypeByName("Vintagestory.GameContent.GuiDialogHandbook")
                ?? throw new TypeLoadException("GuiDialogHandbook");
            cache.survivalDialog = AccessTools.TypeByName("Vintagestory.GameContent.GuiDialogSurvivalHandbook")
                ?? throw new TypeLoadException("GuiDialogSurvivalHandbook");
            var constructor = AccessTools.Constructor(page, [typeof(ICoreClientAPI), typeof(ItemStack)])
                ?? throw new MissingMethodException(page.FullName, ".ctor");
            var create = AccessTools.Method(system, "onCreatePagesAsync")
                ?? throw new MissingMethodException(system.FullName, "onCreatePagesAsync");
            var load = AccessTools.Method(dialog, "LoadPages_Async")
                ?? throw new MissingMethodException(dialog.FullName, "LoadPages_Async");
            cache.filter = AccessTools.Method(dialog, "FilterItems")
                ?? throw new MissingMethodException(dialog.FullName, "FilterItems");
            current?.Dispose();
            cache.harmony.Patch(constructor, transpiler: new HarmonyMethod(typeof(HandbookSearchCache), nameof(CacheDescription)));
            cache.publisher = new HandbookPagePublisher(api, cache.harmony, dialog, cache.survivalDialog, constructor);
            HandbookRecipeCandidates.Install(cache.harmony, system);
            HandbookOverviewRefresh.Install(cache.harmony);
            cache.harmony.Patch(create,
                prefix: new HarmonyMethod(typeof(HandbookSearchCache), nameof(BeginBuild)),
                finalizer: new HarmonyMethod(typeof(HandbookSearchCache), nameof(EndBuild)));
            cache.harmony.Patch(load, postfix: new HarmonyMethod(typeof(HandbookSearchCache), nameof(PagesReady))
            {
                priority = Priority.Last
            });
            current = cache;
            api.Logger.Notification("[vsscpmod] Persistent client handbook search cache enabled.");
            return cache;
        }
        catch (Exception exception)
        {
            cache.publisher?.Dispose();
            cache.harmony.UnpatchAll(HarmonyId);
            api.Logger.Warning("[vsscpmod] Handbook search cache unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static IEnumerable<CodeInstruction> CacheDescription(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo original = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.GetDescription));
        MethodInfo replacement = AccessTools.Method(typeof(HandbookSearchCache), nameof(GetDescription));
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported handbook description constructor.");
    }

    private static void BeginBuild(ICoreClientAPI ___capi, out Build? __state)
    {
        __state = null;
        var owner = current;
        if (owner == null || owner.disposed || !ReferenceEquals(owner.api, ___capi)) return;
        try
        {
            __state = new Build(owner, activeBuild);
            activeBuild = __state;
        }
        catch (Exception exception)
        {
            owner.api.Logger.Warning("[vsscpmod] Handbook cache bypassed: {0}", exception.GetBaseException().Message);
        }
    }

    private static Exception? EndBuild(Exception? __exception, Build? __state)
    {
        if (__state == null) return __exception;
        activeBuild = __state.Previous;
        if (__state.Incomplete)
            __state.Owner.api.Logger.Warning("[vsscpmod] Handbook completed with skipped entries; previous disk search cache retained. See entry warnings above.");
        // A failed or interrupted generation must not replace a complete cache.
        if (__exception == null && !__state.Incomplete && !__state.Owner.disposed && !__state.Owner.api.IsShuttingDown)
        {
            try
            {
                __state.Store.Save();
                __state.Owner.api.Logger.Notification(
                    "[vsscpmod] Handbook search index: hits={0}, generated={1}, elapsed={2}ms, validation={3}ms, firstDescriptionDelay={4}ms, recipeCandidates={5}/{6} (including cache validation/I/O).",
                    __state.Store.Hits, __state.Store.Misses, __state.Timer.ElapsedMilliseconds,
                    __state.ValidationMilliseconds, __state.RecipeMilliseconds,
                    __state.Candidates?.Selected ?? 0, __state.Candidates?.Scanned ?? 0);
            }
            catch (Exception exception)
            {
                __state.Owner.api.Logger.Warning("[vsscpmod] Handbook cache write skipped: {0}", exception.GetBaseException().Message);
            }
        }
        return __exception;
    }

    private static string GetDescription(ItemStack stack, IWorldAccessor world, ItemSlot slot, bool debug)
    {
        Build? build = activeBuild;
        if (build == null || build.Owner.disposed || !ReferenceEquals(build.Owner.api.World, world))
            return stack.GetDescription(world, slot, debug);
        if (build.RecipeMilliseconds < 0)
            build.RecipeMilliseconds = build.Timer.ElapsedMilliseconds - build.ValidationMilliseconds;
        // Include ALL attributes and stack size, rather than the lossy handbook
        // page code, so variants cannot borrow another variant's description.
        string key;
        try { key = Convert.ToHexString(SHA256.HashData(stack.ToBytes())) + (debug ? "1" : "0"); }
        catch { return stack.GetDescription(world, slot, debug); }
        return build.Store.GetOrCreate(key, () => stack.GetDescription(world, slot, debug));
    }

    internal static ItemStack[] RecipeCandidates(ICoreAPI api, string key, object ingredient)
    {
        var stacks = Vintagestory.API.Util.ObjectCacheUtil.TryGet<ItemStack[]>(api, key)!;
        Build? build = activeBuild;
        if (build == null || build.Owner.disposed || !ReferenceEquals(build.Owner.api, api)) return stacks;
        return NarrowStacks(stacks, ingredient);
    }

    internal static ItemStack[] NarrowStacks(ItemStack[] stacks, object ingredient)
    {
        HandbookPagePublisher.CheckCancellation();
        Build? build = activeBuild;
        if (build == null || build.Owner.disposed) return stacks;
        build.Candidates ??= new HandbookRecipeCandidates(stacks);
        if (build.Progress.ElapsedMilliseconds >= 5000)
        {
            build.Owner.api.Logger.Notification("[vsscpmod] Handbook recipes building: elapsed={0}ms, candidates={1}/{2}.",
                build.Timer.ElapsedMilliseconds, build.Candidates.Selected, build.Candidates.Scanned);
            build.Progress.Restart();
        }
        return build.Candidates.For(ingredient);
    }

    internal static void InvalidateBuild() { if (activeBuild != null) activeBuild.Incomplete = true; }

    private static void PagesReady(object __instance)
    {
        var owner = current;
        if (owner == null || owner.disposed || !owner.survivalDialog.IsInstanceOfType(__instance)) return;
        // Vanilla only filters on opening/searching; opening during the async
        // build otherwise leaves an empty list even after all pages are ready.
        owner.api.Event.EnqueueMainThreadTask(() =>
        {
            if (owner.disposed || owner.api.IsShuttingDown || !ReferenceEquals(current, owner)) return;
            if (__instance is not GuiDialog dialog || !dialog.IsOpened()) return;
            try { HandbookOverviewRefresh.Run(dialog, () => owner.filter.Invoke(__instance, null)); }
            catch (Exception exception)
            {
                owner.api.Logger.Warning("[vsscpmod] Handbook ready refresh failed: {0}", exception.GetBaseException().Message);
            }
        }, "vsscpmod-handbook-ready");
    }

    private sealed class Build
    {
        public readonly HandbookSearchCache Owner;
        public readonly Build? Previous;
        public readonly Stopwatch Timer = Stopwatch.StartNew();
        public readonly Stopwatch Progress = Stopwatch.StartNew();
        public readonly HandbookDescriptionStore Store;
        public readonly long ValidationMilliseconds;
        public long RecipeMilliseconds = -1;
        public HandbookRecipeCandidates? Candidates;
        public bool Incomplete;

        public Build(HandbookSearchCache owner, Build? previous)
        {
            Owner = owner;
            Previous = previous;
            var api = owner.api;
            string scope = HashText(api.World.SavegameIdentifier ?? "", api.World.Player?.PlayerUID ?? "", Lang.CurrentLocale);
            string path = Path.Combine(GamePaths.Cache, "vsscpmod-handbook", scope + ".json");
            Store = new HandbookDescriptionStore(path, Fingerprint(api));
            ValidationMilliseconds = Timer.ElapsedMilliseconds;
            if (Store.ReadFailure != null)
                api.Logger.Warning("[vsscpmod] Handbook cache will rebuild: {0}", Store.ReadFailure);
        }
    }

    internal static string Fingerprint(ICoreClientAPI api)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add("handbook-descriptions-v2");
        Add(typeof(ItemStack).Assembly.ManifestModule.ModuleVersionId.ToString());
        Add(Lang.CurrentLocale);
        Add(api.World.SavegameIdentifier);
        Add(api.World.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(api.World.Player?.PlayerUID);
        Add(api.World.Config?.ToJsonToken());
        foreach (Mod mod in api.ModLoader.Mods.OrderBy(m => m.Info.ModID, StringComparer.Ordinal))
        {
            Add(mod.Info.ModID);
            if (mod.Info.ModID == "vsscpmod")
            {
                Add(DescriptionSemantics);
                continue;
            }
            Add(mod.Info.Version);
            if (File.Exists(mod.SourcePath))
            {
                var file = new FileInfo(mod.SourcePath);
                Add(file.Length.ToString()); Add(file.LastWriteTimeUtc.Ticks.ToString());
            }
        }
        foreach (var assembly in api.ModLoader.Systems.Select(s => s.GetType().Assembly).Distinct()
                     .OrderBy(a => a.FullName, StringComparer.Ordinal))
            if (assembly != typeof(HandbookSearchCache).Assembly)
                Add(assembly.ManifestModule.ModuleVersionId.ToString());
        // Text/content JSON and runtime collectible attributes include changes
        // that mod version numbers miss. Exclude lazily loaded render shapes:
        // their load order changes between connections without changing text.
        foreach (var asset in api.Assets.AllAssets.OrderBy(a => a.Key.ToString(), StringComparer.Ordinal))
        {
            if (!asset.Key.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || !ContentPaths.Any(p => asset.Key.Path.StartsWith(p, StringComparison.Ordinal))) continue;
            Add(asset.Key.ToString());
            byte[]? data = asset.Value.Data ?? api.Assets.Get(asset.Key).Data;
            hash.AppendData(BitConverter.GetBytes(data?.Length ?? -1));
            if (data != null) hash.AppendData(data);
        }
        foreach (var collectible in api.World.Collectibles)
        {
            Add(collectible.Code?.ToString()); Add(collectible.Id.ToString());
            Add(collectible.GetType().AssemblyQualifiedName); Add(collectible.Attributes?.ToString());
        }
        foreach (var translation in Lang.GetAllEntries().OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            Add(translation.Key); Add(translation.Value);
        }
        if (Directory.Exists(GamePaths.ModConfig))
        {
            foreach (string file in Directory.EnumerateFiles(GamePaths.ModConfig, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                // Better Handbook writes this on navigation; bookmarks do not
                // change descriptions and must not invalidate every reconnect.
                string name = Path.GetFileName(file);
                if (name.Equals("betterhandbook-bookmarks.json", StringComparison.OrdinalIgnoreCase)) continue;
                Add(Path.GetRelativePath(GamePaths.ModConfig, file));
                using var stream = File.OpenRead(file);
                hash.AppendData(SHA256.HashData(stream));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string HashText(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));

    public void Dispose()
    {
        disposed = true;
        publisher?.Dispose();
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
    }
}

internal sealed class HandbookDescriptionStore
{
    private const long MaxBytes = 128 * 1024 * 1024;
    private readonly string path;
    private readonly string fingerprint;
    private readonly Dictionary<string, string> saved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> used = new(StringComparer.Ordinal);
    public int Hits { get; private set; }
    public int Misses { get; private set; }
    public string? ReadFailure { get; }

    public sealed class Snapshot
    {
        public string Fingerprint { get; set; } = "";
        public Dictionary<string, string> Descriptions { get; set; } = new();
    }

    public HandbookDescriptionStore(string path, string fingerprint)
    {
        this.path = path;
        this.fingerprint = fingerprint;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Cache exceeds size limit.");
            using var stream = File.OpenRead(path);
            Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(stream);
            if (snapshot?.Fingerprint != fingerprint) return;
            if (snapshot.Descriptions == null || snapshot.Descriptions.Count > 200000
                || snapshot.Descriptions.Any(p => p.Key.Length != 65 || p.Value == null || p.Value.Length > 262144))
                throw new InvalidDataException("Invalid cache entries.");
            saved = snapshot.Descriptions;
        }
        catch (Exception exception) { ReadFailure = exception.Message; }
    }

    public string GetOrCreate(string key, Func<string> create)
    {
        if (used.TryGetValue(key, out string? value) || saved.TryGetValue(key, out value)) Hits++;
        else { value = create(); Misses++; }
        used[key] = value;
        return value;
    }

    public void Save()
    {
        if (Misses == 0 && used.Count == saved.Count) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, new Snapshot { Fingerprint = fingerprint, Descriptions = used });
                if (stream.Length > MaxBytes) throw new InvalidDataException("Cache exceeds size limit.");
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
