using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Index only facts that determine whether a source can enter vanilla's first
// scan. The original code still evaluates all matches and builds every panel.
internal sealed class HandbookDetailCandidates
{
    private readonly Dictionary<(EnumItemClass, int), HashSet<ItemStack>> staticDrops = new();
    private readonly Dictionary<(EnumItemClass, int), HashSet<ItemStack>> staticOutputs = new();
    private readonly HashSet<ItemStack> always = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ItemStack> createdByAlways = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(EnumItemClass, int), ItemStack[]> firstPass = new();
    private readonly Dictionary<(EnumItemClass, int), ItemStack[]> createdBy = new();
    private readonly bool scanAllDrops;
    private readonly bool scanAllCombustibles;
    private readonly bool unknownEquality;
    public ItemStack[] All { get; }
    public ItemStack[] Storable { get; }
    public ItemStack[] Harvestable { get; }
    public HandbookGridIngredientCandidates GridIngredients { get; }
    public HandbookRecipeCandidates MealIngredients { get; }

    public HandbookDetailCandidates(ItemStack[] all)
    {
        All = all;
        GridIngredients = new HandbookGridIngredientCandidates(all);
        MealIngredients = new HandbookRecipeCandidates(all);
        var dropMethod = AccessTools.Method(typeof(Block), nameof(Block.GetDropsForHandbook));
        var dropPatches = Harmony.GetPatchInfo(dropMethod);
        scanAllDrops = dropPatches != null && dropPatches.Prefixes.Concat(dropPatches.Postfixes)
            .Concat(dropPatches.Transpilers).Concat(dropPatches.Finalizers)
            .Any(p => p.PatchMethod.DeclaringType != typeof(HandbookDetailSafety)
                || p.PatchMethod.Name != "SafeRegisteredDrops");
        var combustibleMethod = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetCombustibleProperties));
        scanAllCombustibles = Harmony.GetPatchInfo(combustibleMethod)?.Owners.Any() == true;
        var equalsMethod = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.Equals),
            [typeof(ItemStack), typeof(ItemStack), typeof(string[])]);
        unknownEquality = HandbookRecipeCandidates.HasUnknownPatches(equalsMethod, HandbookRecipeCandidates.KnownEquality);
        bool dynamicOutputs = new[]
        {
            nameof(CollectibleObject.GetCombustibleProperties), nameof(CollectibleObject.GetGrindingProperties),
            nameof(CollectibleObject.GetCrushingProperties), nameof(CollectibleObject.GetTransitionableProperties)
        }.Any(name => Harmony.GetPatchInfo(AccessTools.Method(typeof(CollectibleObject), name))?.Owners.Any() == true);
        var storable = new List<ItemStack>();
        var harvestable = new List<ItemStack>();
        var methods = new Dictionary<Type, (bool drops, bool combustible, bool producer)>();
        foreach (var source in all)
        {
            if (source?.Collectible == null) continue;
            var collectible = source.Collectible;
            if (IsStorableCandidate(source)) storable.Add(source);
            if (source.Block?.GetBehavior<BlockBehaviorHarvestable>() != null
                || source.Block?.GetBehavior<BlockBehaviorFruitingBush>() != null
                || source.Block is BlockFruitTreeBranch) harvestable.Add(source);
            Type type = collectible.GetType();
            if (!methods.TryGetValue(type, out var behavior))
            {
                behavior = (source.Block != null && AccessTools.Method(type, nameof(Block.GetDropsForHandbook))?.DeclaringType != typeof(Block),
                    AccessTools.Method(type, nameof(CollectibleObject.GetCombustibleProperties))?.DeclaringType != typeof(CollectibleObject),
                    new[] { nameof(CollectibleObject.GetCombustibleProperties), nameof(CollectibleObject.GetGrindingProperties),
                        nameof(CollectibleObject.GetCrushingProperties), nameof(CollectibleObject.GetTransitionableProperties) }
                        .Any(name => AccessTools.Method(type, name)?.DeclaringType != typeof(CollectibleObject)));
                methods[type] = behavior;
            }
            if (source.ItemAttributes?.KeyExists("cookingContainerSlots") == true
                || collectible is BlockToolMold or BlockIngotMold or BlockAnvil
                || collectible.CombustibleProps != null || behavior.combustible || scanAllCombustibles)
                always.Add(source);
            if (dynamicOutputs || behavior.producer || collectible is BlockFruitPress or BlockQuern or ItemOre
                || source.ItemAttributes?.KeyExists("beehivekiln") == true
                || source.ItemAttributes?.KeyExists("carburizableProps") == true
                || source.ItemAttributes?.KeyExists("juiceableProperties") == true
                || source.ItemAttributes?.KeyExists("distillationProps") == true
                || collectible.GetCollectibleBehavior<CollectibleBehaviorSqueezable>(true) != null
                || collectible.GetCollectibleBehavior<CollectibleBehaviorGroundStoredProcessable>(true) != null
                || collectible.GetCollectibleInterface<IAnvilWorkable>() != null)
                createdByAlways.Add(source);
            AddOutput(collectible.CombustibleProps?.SmeltedStack?.ResolvedItemstack, source);
            AddOutput(collectible.GrindingProps?.GroundStack?.ResolvedItemstack, source);
            AddOutput(collectible.CrushingProps?.CrushedStack?.ResolvedItemstack, source);
            foreach (var transition in collectible.TransitionableProps ?? [])
                AddOutput(transition?.TransitionedStack?.ResolvedItemstack, source);
            if (source.Block == null) continue;
            if (behavior.drops || scanAllDrops || unknownEquality) { always.Add(source); continue; }
            foreach (var drop in source.Block.Drops ?? [])
            {
                var output = drop?.ResolvedItemstack;
                if (output?.Collectible == null || output.Collectible is IResolvableCollectible
                    || AccessTools.Method(output.Collectible.GetType(), nameof(CollectibleObject.Equals),
                        [typeof(ItemStack), typeof(ItemStack), typeof(string[])])?.DeclaringType != typeof(CollectibleObject))
                {
                    always.Add(source);
                    break;
                }
                var key = (output.Class, output.Id);
                if (!staticDrops.TryGetValue(key, out var sources)) staticDrops[key] = sources = new(ReferenceEqualityComparer.Instance);
                sources.Add(source);
            }
        }
        Storable = storable.ToArray();
        Harvestable = harvestable.ToArray();
    }

    public ItemStack[] FirstPass(ItemStack target)
    {
        var key = (target.Class, target.Id);
        if (firstPass.TryGetValue(key, out var result)) return result;
        staticDrops.TryGetValue(key, out var sources);
        result = All.Where(s => s != null && (always.Contains(s) || sources?.Contains(s) == true)).ToArray();
        firstPass[key] = result;
        return result;
    }

    public ItemStack[] CreatedBy(ItemStack target, Vintagestory.API.Client.ICoreClientAPI api)
    {
        if (unknownEquality || AccessTools.Method(target.Collectible.GetType(), nameof(CollectibleObject.Equals),
                [typeof(ItemStack), typeof(ItemStack), typeof(string[])])?.DeclaringType != typeof(CollectibleObject)
            || target.Block is BlockAnvil
            || api.GetKnappingRecipes().Any(r => CanMatchOutput(r.Output?.ResolvedItemstack, target))
            || api.GetClayformingRecipes().Any(r => CanMatchOutput(r.Output?.ResolvedItemstack, target))) return All;
        var key = (target.Class, target.Id);
        if (createdBy.TryGetValue(key, out var result)) return result;
        staticOutputs.TryGetValue(key, out var sources);
        result = All.Where(s => s != null && (createdByAlways.Contains(s) || sources?.Contains(s) == true)).ToArray();
        createdBy[key] = result;
        return result;
    }

    private static bool CanMatchOutput(ItemStack? output, ItemStack target)
    {
        if (output?.Collectible == null) return false;
        return output.Class == target.Class && output.Id == target.Id
            || AccessTools.Method(output.Collectible.GetType(), nameof(CollectibleObject.Equals),
                [typeof(ItemStack), typeof(ItemStack), typeof(string[])])?.DeclaringType != typeof(CollectibleObject);
    }

    private void AddOutput(ItemStack? output, ItemStack source)
    {
        if (output?.Collectible == null) return;
        if (AccessTools.Method(output.Collectible.GetType(), nameof(CollectibleObject.Equals),
            [typeof(ItemStack), typeof(ItemStack), typeof(string[])])?.DeclaringType != typeof(CollectibleObject))
        {
            createdByAlways.Add(source);
            return;
        }
        var key = (output.Class, output.Id);
        if (!staticOutputs.TryGetValue(key, out var sources)) staticOutputs[key] = sources = new(ReferenceEqualityComparer.Instance);
        sources.Add(source);
    }

    private static bool IsStorableCandidate(ItemStack source)
    {
        if (source.Collectible.GetCollectibleInterface<ILiquidInterface>() != null) return true;
        if (source.Block is BlockShelf or BlockToolRack or BlockPlantContainer or BlockTroughBase or BlockAnimalTrap
            or BlockMoldRack or BlockBookshelf or BlockScrollRack or BlockDisplayCase or BlockAntlerMount
            or BlockOmokTable or BlockCrock) return true;
        return source.Block?.GetBehavior<BlockBehaviorDisplay>() != null;
    }
}
