using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Recovery belongs to a single handbook composition. Never alter registered
// drops/recipes, suppress gameplay errors, or cache live rich-text components.
internal sealed partial class HandbookDetailSafety : IDisposable
{
    private const string HarmonyId = "vsscpmod.handbook-detail-safety";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreClientAPI api;
    private readonly HashSet<string> reported = new();
    private readonly Dictionary<string, int> slowReports = new();
    private volatile HandbookDetailCandidates? candidates;
    private HandbookDetailCompose? detailCompose;
    private static HandbookDetailSafety? current;
    [ThreadStatic] private static Composition? active;
    private static readonly AccessTools.FieldRef<GuiDialogHandbook, Stack<BrowseHistoryElement>> BrowseHistory =
        AccessTools.FieldRefAccess<GuiDialogHandbook, Stack<BrowseHistoryElement>>("browseHistory");

    private HandbookDetailSafety(ICoreClientAPI api) => this.api = api;

    public static HandbookDetailSafety? TryCreate(ICoreClientAPI api)
    {
        current?.Dispose();
        var owner = new HandbookDetailSafety(api);
        try
        {
            Type behavior = AccessTools.TypeByName("Vintagestory.GameContent.CollectibleBehaviorHandbookTextAndExtraInfo")
                ?? throw new TypeLoadException("CollectibleBehaviorHandbookTextAndExtraInfo");
            var detail = AccessTools.Method(behavior, "GetHandbookInfo");
            owner.harmony.Patch(detail,
                prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(Begin)) { priority = Priority.First },
                transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(SafeInputs)),
                finalizer: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(Finish)) { priority = Priority.Last });
            owner.harmony.Patch(AccessTools.Method(behavior, "addStoredInInfo"),
                prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(SkipNonContainers)));
            owner.harmony.Patch(AccessTools.Method(behavior, "addStorableInfo"),
                transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(NarrowStorableScan)));
            owner.harmony.Patch(AccessTools.Method(behavior, "addToListUniquely"),
                prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(AddUniqueStack)));
            owner.harmony.Patch(AccessTools.Method(typeof(GuiHandbookMealRecipePage), "getIngredientStacks"),
                transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(NarrowMealScan)));
            foreach (string stage in new[] { "getIngredientStacks", "getNutritionFacts", "addIngredientLists" })
                owner.harmony.Patch(AccessTools.Method(typeof(GuiHandbookMealRecipePage), stage),
                    prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(BeginDetailGui)),
                    finalizer: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(FinishMealStage)));
            owner.harmony.Patch(AccessTools.Method(behavior, "addCreatedByInfo"),
                transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(NarrowCreatedByScan)));
            owner.harmony.Patch(AccessTools.Method(behavior, "addObtainedThroughInfo"),
                transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(NarrowHarvestableScan)));
            owner.harmony.Patch(AccessTools.Method(typeof(SlideshowGridRecipeTextComponent), "MatchIngredients"),
                prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(NarrowGridIngredients)));
            owner.harmony.Patch(AccessTools.Method(typeof(GuiDialogHandbook), "initDetailGui"),
                prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(BeginDetailGui)),
                finalizer: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(FinishDetailGui)));
            owner.harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.GetDropsForHandbook)),
                transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(SafeRegisteredDrops)));
            var entityDrops = AccessTools.Method(AccessTools.TypeByName(
                "ExtraInfo.Systems.Handbook.HandbookEntityDrops.HandbookEntityDropsPatch"), "AddEntityDropsInfoForDrop");
            if (entityDrops != null)
                owner.harmony.Patch(entityDrops, transpiler: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(SafeRegisteredDrops)));
            foreach (MethodInfo section in behavior.GetMethods(AccessTools.allDeclared)
                .Where(m => m.Name.StartsWith("add", StringComparison.Ordinal)
                    && m.GetParameters().Any(p => p.ParameterType == typeof(List<RichTextComponentBase>))
                    && (m.ReturnType == typeof(void) || m.ReturnType == typeof(bool))))
            {
                owner.harmony.Patch(section,
                    prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(BeginSection)) { priority = Priority.First },
                    transpiler: section.Name is "addDropsInfo" or "addObtainedThroughInfo" or "addIngredientForInfo" or "addStoredInInfo"
                        ? new HarmonyMethod(typeof(HandbookDetailSafety), nameof(SafeInputs)) : null,
                    finalizer: new HarmonyMethod(typeof(HandbookDetailSafety), section.ReturnType == typeof(bool)
                        ? nameof(FinishBooleanSection) : nameof(FinishSection)) { priority = Priority.Last });
            }
            // Guard the actual extension methods separately so one failed
            // postfix does not prevent the following mods from adding recipes.
            string[] extensions = [
                "ExtraInfo.Systems.Handbook.HandbookCreatureDiet.HandbookCreatureDietPatch",
                "ExtraInfo.Systems.Handbook.HandbookEatableByCreatures.HandbookEatableByCreaturesPatch",
                "ExtraInfo.Systems.Handbook.HandbookEntityDrops.HandbookEntityDropsPatch",
                "ExtraInfo.Systems.Handbook.HandbookPanningDrops.HandbookPanningDropsPatch",
                "ExtraInfo.Systems.Handbook.HandbookPitKiln.HandbookPitKilnPatch",
                "ExtraInfo.Systems.Handbook.HandbookTraderGoods.HandbookTraderGoodsPatch",
                "ExtraInfo.Systems.Handbook.HandbookTroughFeedOptions.HandbookTroughFeedOptionsPatch",
                "BetterNails.src.patching.molds.ToolMoldHandbookOutputPatch",
                "ACulinaryArtillery.GetHandbookCreatedByPatch",
                "ACulinaryArtillery.GetHandbookIngredientForPatch",
                "ACulinaryArtillery.GetHandbookProcessesIntoPatch",
                "ACulinaryArtillery.GetHandbookStoredInPatch",
                "ACulinaryArtillery.GetHandbookStorablePatch"
            ];
            foreach (string typeName in extensions)
            {
                var method = AccessTools.Method(AccessTools.TypeByName(typeName), "Postfix");
                if (method == null || method.ReturnType != typeof(void)) continue;
                owner.harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(BeginSection)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(HandbookDetailSafety), nameof(ExtensionFailed)) { priority = Priority.Last });
            }
            owner.detailCompose = HandbookDetailCompose.TryCreate(api);
            current = owner;
            api.Logger.Notification("[vsscpmod] Client handbook detail recovery enabled.");
            return owner;
        }
        catch (Exception exception)
        {
            owner.Dispose();
            api.Logger.Warning("[vsscpmod] Handbook detail recovery unavailable: {0}", exception);
            return null;
        }
    }

    internal static void WarmCandidates(ICoreClientAPI api, ItemStack[] stacks)
    {
        if (current is not { } owner || !ReferenceEquals(owner.api, api)) return;
        try { owner.candidates = new HandbookDetailCandidates(stacks); }
        catch (Exception exception)
        {
            owner.api.Logger.Warning("[vsscpmod] Handbook detail candidate index unavailable: {0}", exception.GetBaseException().Message);
        }
    }

    private static void Begin(ItemSlot inSlot, ICoreClientAPI capi, ItemStack[] allStacks, out Composition? __state)
    {
        __state = null;
        if (current is not { } owner || !ReferenceEquals(owner.api, capi)) return;
        var stack = inSlot?.Itemstack;
        string pageCode = stack?.Collectible?.Code?.ToString() ?? "<unknown>";
        if (stack?.Collectible?.Code != null)
        {
            try
            {
                pageCode = (string?)AccessTools.Method(AccessTools.TypeByName("Vintagestory.GameContent.GuiHandbookItemStackPage"),
                    "PageCodeForStack")?.Invoke(null, [stack]) ?? pageCode;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is { } error && Recoverable(error)) { }
        }
        if (!ReferenceEquals(owner.candidates?.All, allStacks)) WarmCandidates(capi, allStacks);
        __state = new Composition(owner, pageCode, active, stack!, allStacks);
        active = __state;
    }

    private static List<RichTextComponentBase> CaptureComponents()
    {
        var components = new List<RichTextComponentBase>();
        if (active != null) active.Components = components;
        return components;
    }

    private static IEnumerable<CodeInstruction> SafeInputs(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var constructor = AccessTools.Constructor(typeof(List<RichTextComponentBase>), Type.EmptyTypes);
        var drops = AccessTools.Method(typeof(Block), nameof(Block.GetDropsForHandbook));
        var equals = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Equals),
            [typeof(IWorldAccessor), typeof(ItemStack), typeof(string[])]);
        int captured = 0;
        int firstPass = 0;
        int recipeReads = 0;
        bool recipesOnly = __originalMethod.Name is "addIngredientForInfo" or "addStoredInInfo";
        foreach (var instruction in instructions)
        {
            if (__originalMethod.Name == "GetHandbookInfo" && LoadsArgument(instruction, 3) && firstPass++ == 0)
            {
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookDetailSafety), nameof(FirstPassCandidates)));
                continue;
            }
            string? replacement = null;
            if (__originalMethod.Name == "GetHandbookInfo" && instruction.opcode == OpCodes.Newobj
                && Equals(instruction.operand, constructor)) { replacement = nameof(CaptureComponents); captured++; }
            else if (!recipesOnly && instruction.Calls(drops)) replacement = nameof(GetValidDrops);
            else if (!recipesOnly && instruction.Calls(equals)) replacement = nameof(StackEquals);
            else if (recipesOnly
                && instruction.operand is MethodInfo cacheRead && cacheRead.DeclaringType == typeof(ObjectCacheUtil)
                && cacheRead.Name == nameof(ObjectCacheUtil.TryGet) && cacheRead.IsGenericMethod
                && cacheRead.GetGenericArguments().SequenceEqual(new[] { typeof(CookingRecipe[]) }))
            {
                replacement = nameof(AvailableHandbookRecipes);
                recipeReads++;
            }
            if (replacement != null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookDetailSafety), replacement);
            }
            yield return instruction;
        }
        if (__originalMethod.Name == "GetHandbookInfo" && (captured != 1 || firstPass == 0))
            throw new InvalidOperationException("Unsupported handbook component list.");
        if (recipesOnly && recipeReads != 1)
            throw new InvalidOperationException("Unsupported handbook pie recipe cache read.");
    }

    // Streamed item pages are usable before pie recipes finish building. Do
    // not publish an empty value to the shared cache: the background builder
    // owns it, and a later detail composition must see its completed recipes.
    private static CookingRecipe[] AvailableHandbookRecipes(ICoreAPI api, string key)
        => ObjectCacheUtil.TryGet<CookingRecipe[]>(api, key) ?? [];

    private static ItemStack[] FirstPassCandidates(ItemStack[] allStacks)
    {
        var context = active;
        var index = context?.Owner.candidates;
        return index != null && ReferenceEquals(index.All, allStacks)
            ? index.FirstPass(context!.Target) : allStacks;
    }

    private static IEnumerable<CodeInstruction> NarrowStorableScan(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (!LoadsArgument(instruction, 2)) continue;
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookDetailSafety), nameof(StorableCandidates)));
            replaced++;
        }
        if (replaced == 0) throw new InvalidOperationException("Unsupported handbook storable scan.");
    }

    private static ItemStack[] StorableCandidates(ItemStack[] allStacks)
    {
        var index = active?.Owner.candidates;
        return index != null && ReferenceEquals(index.All, allStacks) ? index.Storable : allStacks;
    }

    private static IEnumerable<CodeInstruction> NarrowHarvestableScan(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (!LoadsArgument(instruction, 2)) continue;
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookDetailSafety), nameof(HarvestableCandidates)));
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported handbook harvestable scan.");
    }

    private static ItemStack[] HarvestableCandidates(ItemStack[] allStacks)
    {
        var index = active?.Owner.candidates;
        return index != null && ReferenceEquals(index.All, allStacks) ? index.Harvestable : allStacks;
    }

    private static IEnumerable<CodeInstruction> NarrowCreatedByScan(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        bool inSourceScan = true;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.Name == "BuildBarrelRecipesText")
                inSourceScan = false;
            yield return instruction;
            if (!inSourceScan || !LoadsArgument(instruction, 2)) continue;
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookDetailSafety), nameof(CreatedByCandidates)));
            replaced++;
        }
        if (replaced == 0 || inSourceScan) throw new InvalidOperationException("Unsupported handbook created-by scan.");
    }

    private static ItemStack[] CreatedByCandidates(ItemStack[] allStacks)
    {
        var context = active;
        var index = context?.Owner.candidates;
        return index != null && ReferenceEquals(index.All, allStacks)
            ? index.CreatedBy(context!.Target, context.Owner.api) : allStacks;
    }

    private static void NarrowGridIngredients(IWorldAccessor world, IRecipeIngredient ingredient, ref ItemStack[]? allStacks)
    {
        var context = active;
        var index = context?.Owner.candidates;
        if (allStacks != null && index != null && ReferenceEquals(index.All, allStacks)
            && ReferenceEquals(context!.Owner.api.World, world))
            allStacks = index.GridIngredients.For(ingredient);
    }

    private static void BeginDetailGui(out long __state) => __state = Stopwatch.GetTimestamp();

    private static Exception? FinishDetailGui(Exception? __exception, long __state, GuiDialogHandbook __instance)
    {
        if (current is { } owner && Stopwatch.GetElapsedTime(__state).TotalMilliseconds >= 250)
            ReportSlow(owner, DetailPageCode(__instance), "initDetailGui", __state);
        return __exception;
    }

    private static string DetailPageCode(GuiDialogHandbook dialog)
    {
        try
        {
            var history = BrowseHistory(dialog);
            return history?.TryPeek(out var entry) == true ? entry.Page?.PageCode ?? "<search>" : "<search>";
        }
        catch { return "<unknown>"; }
    }

    private static bool LoadsArgument(CodeInstruction instruction, int index) => index switch
    {
        2 when instruction.opcode == OpCodes.Ldarg_2 => true,
        3 when instruction.opcode == OpCodes.Ldarg_3 => true,
        _ => (instruction.opcode == OpCodes.Ldarg || instruction.opcode == OpCodes.Ldarg_S)
            && Convert.ToInt32(instruction.operand) == index
    };

    private static bool SkipNonContainers(ItemStack stack)
    {
        if (active == null || stack?.Collectible == null) return true;
        if (stack.Collectible.GetCollectibleInterface<ILiquidInterface>() != null) return true;
        if (stack.Block is BlockShelf or BlockPlantContainer or BlockToolRack or BlockAnimalTrap or BlockTroughBase
            or BlockMoldRack or BlockBookshelf or BlockScrollRack or BlockDisplayCase or BlockAntlerMount
            or BlockCrock or BlockOmokTable) return true;
        return stack.Block?.GetBehavior<BlockBehaviorDisplay>() != null;
    }

    private static BlockDropItemStack[]? GetValidDrops(Block block, ItemStack stack, IPlayer player)
    {
        if (active == null) return block.GetDropsForHandbook(stack, player);
        BlockDropItemStack[]? drops;
        try { drops = block.GetDropsForHandbook(stack, player); }
        catch (Exception exception) when (Recoverable(exception))
        {
            Report("drops " + block.Code, exception); return [];
        }
        if (drops == null || drops.All(d => d?.ResolvedItemstack?.Collectible != null)) return drops;
        foreach (var drop in drops.Where(d => d?.ResolvedItemstack?.Collectible == null))
            Report("drops " + block.Code + " -> " + (drop?.Code?.ToString() ?? "<null>"), null);
        return drops.Where(d => d?.ResolvedItemstack?.Collectible != null).ToArray();
    }

    private static bool StackEquals(ItemStack? stack, IWorldAccessor world, ItemStack other, string[] ignored)
    {
        if (active != null && (stack?.Collectible == null || other?.Collectible == null))
        {
            Report("unresolved drop comparison", null); return false;
        }
        return stack!.Equals(world, other, ignored);
    }

    private static IEnumerable<CodeInstruction> SafeRegisteredDrops(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field
                && field.Name == "Drops" && field.DeclaringType is { } type
                && (type == typeof(Block) || type == typeof(EntityProperties)))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookDetailSafety), type == typeof(Block)
                    ? nameof(RegisteredBlockDrops) : nameof(RegisteredEntityDrops));
            }
            yield return instruction;
        }
    }

    private static BlockDropItemStack[]? RegisteredBlockDrops(Block block)
    {
        var drops = block.Drops;
        if (active == null || drops == null || drops.All(d => d?.ResolvedItemstack?.Collectible != null)) return drops;
        foreach (var drop in drops.Where(d => d?.ResolvedItemstack?.Collectible == null))
            Report("drops " + block.Code + " -> " + (drop?.Code?.ToString() ?? "<null>"), null);
        return drops.Where(d => d?.ResolvedItemstack?.Collectible != null).ToArray();
    }

    private static BlockDropItemStack[]? RegisteredEntityDrops(EntityProperties entity)
    {
        var drops = entity.Drops;
        if (active == null) return drops;
        // Null means this entity has no drops, which is valid game content.
        if (drops == null) return [];
        if (drops.All(d => d?.Code != null)) return drops;
        Report("entity drops " + entity.Code, null);
        return drops.Where(d => d?.Code != null).ToArray();
    }

    private static void BeginSection(object[] __args, out Section? __state)
    {
        var components = __args.OfType<List<RichTextComponentBase>>().FirstOrDefault();
        __state = active == null || components == null ? null : new Section(components, components.ToArray());
    }

    private static Exception? FinishSection(Exception? __exception, Section? __state, MethodBase __originalMethod)
    {
        if (__state != null) ReportSlow(__originalMethod.Name, __state.Started);
        if (__exception == null || __state == null || !Recoverable(__exception)) return __exception;
        Rollback(__state);
        Report(__originalMethod.Name, __exception);
        return null;
    }

    private static Exception? FinishBooleanSection(Exception? __exception, Section? __state,
        MethodBase __originalMethod, object[] __args, ref bool __result)
    {
        var error = FinishSection(__exception, __state, __originalMethod);
        if (__exception != null && error == null)
            __result = __args.LastOrDefault() is bool haveText ? haveText : __state!.Components.Count > 0;
        return error;
    }

    private static Exception? ExtensionFailed(Exception? __exception, MethodBase __originalMethod, Section? __state)
    {
        if (__exception == null || active == null || !Recoverable(__exception)) return __exception;
        if (__state != null) Rollback(__state);
        Report(__originalMethod.DeclaringType?.FullName + "." + __originalMethod.Name, __exception);
        return null;
    }

    private static void Rollback(Section state)
    {
        // Mods can InsertRange in the middle of a page. Restore the exact
        // snapshot, rather than truncating by count and deleting older content.
        var retained = new HashSet<RichTextComponentBase>(state.Previous, ReferenceEqualityComparer.Instance);
        var disposed = new HashSet<RichTextComponentBase>(ReferenceEqualityComparer.Instance);
        foreach (var component in state.Components)
        {
            if (component == null || retained.Contains(component) || !disposed.Add(component)) continue;
            try { component.Dispose(); } catch (Exception e) when (Recoverable(e)) { }
        }
        state.Components.Clear(); state.Components.AddRange(state.Previous);
    }

    private static Exception? Finish(Exception? __exception, Composition? __state, ref RichTextComponentBase[]? __result)
    {
        if (__state == null) return __exception;
        try
        {
            ReportSlow("GetHandbookInfo", __state.Started);
            if (__exception != null)
            {
                if (!Recoverable(__exception)) return __exception;
                Report("GetHandbookInfo", __exception);
                // Postfix failures retain the returned page; failures inside
                // the original retain its already generated components.
                __result ??= __state.Components?.ToArray() ?? [];
            }
            if (__state.Incomplete)
            {
                __result = (__result ?? []).Append(new RichTextComponent(__state.Owner.api,
                    "\n" + Lang.Get("vsscpmod:handbook-detail-incomplete", __state.Code) + "\n",
                    CairoFont.WhiteSmallText())).ToArray();
            }
            return null;
        }
        finally { active = __state.Previous; }
    }

    private static bool Recoverable(Exception exception) => exception is NullReferenceException or ArgumentException
        or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException;

    private static void ReportSlow(string stage, long started)
    {
        var context = active;
        if (context == null) return;
        ReportSlow(context.Owner, context.Code, stage, started);
    }

    private static void ReportSlow(HandbookDetailSafety owner, string pageCode, string stage, long started)
    {
        long elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed < 250) return;
        string key = pageCode + "|" + stage;
        var counts = owner.slowReports;
        counts.TryGetValue(key, out int count);
        if (count >= 3 || counts.Count >= 64 && count == 0) return;
        counts[key] = count + 1;
        owner.api.Logger.Notification("[vsscpmod] Handbook detail timing: page={0}, stage={1}, elapsed={2}ms",
            pageCode, stage, elapsed);
    }

    private static void Report(string stage, Exception? exception)
    {
        var context = active!;
        context.Incomplete = true;
        string key = context.Code + "|" + stage + "|" + exception?.GetType().FullName;
        // Bounded per-connection logging; repeated clicks never grow a cache
        // of GUI objects or flood the log with the same broken extension.
        if (context.Owner.reported.Count >= 256 || !context.Owner.reported.Add(key)) return;
        context.Owner.api.Logger.Warning("[vsscpmod] Handbook detail recovered: page={0}, stage={1}, error={2}",
            context.Code, stage, exception?.ToString() ?? "unresolved/null drop skipped");
    }

    public void Dispose()
    {
        detailCompose?.Dispose();
        harmony.UnpatchAll(HarmonyId);
        if (ReferenceEquals(current, this)) current = null;
        reported.Clear();
        slowReports.Clear();
        candidates = null;
    }

    private sealed record Composition(HandbookDetailSafety Owner, string Code, Composition? Previous,
        ItemStack Target, ItemStack[] AllStacks)
    {
        public readonly long Started = Stopwatch.GetTimestamp();
        public List<RichTextComponentBase>? Components;
        public bool Incomplete;
        public readonly Dictionary<List<ItemStack>, HandbookUniqueStacks> UniqueLists = new(ReferenceEqualityComparer.Instance);
    }
    private sealed record Section(List<RichTextComponentBase> Components, RichTextComponentBase[] Previous)
    {
        public readonly long Started = Stopwatch.GetTimestamp();
    }
}
