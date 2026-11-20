using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

internal sealed partial class HandbookDetailSafety
{
    private static IEnumerable<CodeInstruction> NarrowMealScan(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (!LoadsArgument(instruction, 3)) continue;
            yield return new CodeInstruction(OpCodes.Ldarg_2);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HandbookDetailSafety), nameof(MealCandidates)));
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported meal ingredient enumeration.");
    }

    private static ItemStack[] MealCandidates(ItemStack[] allStacks, CookingRecipeIngredient ingredient)
    {
        var index = current?.candidates;
        return index != null && ReferenceEquals(index.All, allStacks) ? index.MealIngredients.For(ingredient) : allStacks;
    }

    private static Exception? FinishMealStage(Exception? __exception, long __state,
        GuiHandbookMealRecipePage __instance, MethodBase __originalMethod)
    {
        if (current is { } owner && Stopwatch.GetElapsedTime(__state).TotalMilliseconds >= 250)
            ReportSlow(owner, __instance.PageCode, __originalMethod.Name, __state);
        return __exception;
    }

    private static bool AddUniqueStack(ICoreClientAPI capi, List<ItemStack> list, ItemStack entry)
    {
        var context = active;
        if (context == null || !ReferenceEquals(context.Owner.api, capi) || entry == null) return true;
        if (!context.UniqueLists.TryGetValue(list, out var index))
            context.UniqueLists[list] = index = new HandbookUniqueStacks(list);
        index.Add(capi.World, entry);
        return false;
    }
}

// Only skip comparisons that cannot match by class/ID. Keep real attribute
// equality, custom cross-ID equality, original order and duplicate variants.
// Lifetime is a single detail composition, never a cached live inventory.
internal sealed class HandbookUniqueStacks(List<ItemStack> list)
{
    private readonly Dictionary<(EnumItemClass, int), List<int>> byId = new();
    private readonly List<int> custom = new();
    private readonly Dictionary<Type, bool> customTypes = new();
    private readonly bool scanAll = HandbookRecipeCandidates.HasUnknownPatches(
        AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.Equals),
            [typeof(ItemStack), typeof(ItemStack), typeof(string[])]), HandbookRecipeCandidates.KnownEquality)
        || Harmony.GetPatchInfo(AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Equals),
            [typeof(IWorldAccessor), typeof(ItemStack), typeof(string[])]))?.Owners.Any() == true;
    private int indexed;

    public void Add(IWorldAccessor world, ItemStack entry)
    {
        if (scanAll)
        {
            if (!list.Any(s => s.Equals(world, entry, GlobalConstants.IgnoredStackAttributes))) list.Add(entry);
            return;
        }
        if (indexed != list.Count) { byId.Clear(); custom.Clear(); indexed = 0; }
        while (indexed < list.Count) Index(indexed++);
        IEnumerable<int> candidates = custom;
        if (byId.TryGetValue((entry.Class, entry.Id), out var same)) candidates = same.Concat(custom).Order();
        if (candidates.Any(i => list[i].Equals(world, entry, GlobalConstants.IgnoredStackAttributes))) return;
        list.Add(entry);
        Index(indexed++);
    }

    private void Index(int i)
    {
        var stack = list[i];
        Type type = stack.Collectible.GetType();
        if (!customTypes.TryGetValue(type, out bool dynamic))
        {
            var equals = AccessTools.Method(type, nameof(CollectibleObject.Equals),
                [typeof(ItemStack), typeof(ItemStack), typeof(string[])]);
            customTypes[type] = dynamic = equals.DeclaringType != typeof(CollectibleObject)
                || HandbookRecipeCandidates.HasUnknownPatches(equals, HandbookRecipeCandidates.KnownEquality);
        }
        if (dynamic) { custom.Add(i); return; }
        var key = (stack.Class, stack.Id);
        if (!byId.TryGetValue(key, out var indices)) byId[key] = indices = new();
        indices.Add(i);
    }
}
