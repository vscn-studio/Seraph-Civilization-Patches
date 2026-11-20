using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace SeraphCivilizationPatches;

// The slideshow's own cache lives only until its constructor returns. Each new
// page used to scan every handbook stack for each wildcard again. A sorted path
// index narrows even the FIRST query, without caching matches or GUI components.
internal sealed class HandbookGridIngredientCandidates
{
    private readonly ItemStack[] all;
    private readonly (string Path, int Index)[] paths;
    private static readonly System.Reflection.MethodInfo matcher = AccessTools.Method(typeof(CraftingRecipeIngredient), "SatisfiesAsIngredient");
    private static readonly System.Reflection.MethodInfo wildcard = AccessTools.Method(typeof(WildcardUtil), "Match",
        [typeof(AssetLocation), typeof(AssetLocation), typeof(string[])]);

    public HandbookGridIngredientCandidates(ItemStack[] all)
    {
        this.all = all;
        paths = all.Select((stack, i) => (Path: stack?.Collectible?.Code?.Path ?? "", Index: i))
            .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
    }

    public ItemStack[] For(IRecipeIngredient ingredient)
    {
        // Custom matchers, exact-stack equality, tags-only and regex rules can
        // match outside a literal prefix. Keep their complete original input.
        if (ingredient is not CraftingRecipeIngredient crafting
            || crafting.MatchingType == EnumRecipeMatchType.Exact
            || string.IsNullOrEmpty(crafting.Code?.Path)
            || crafting.Code.Path.StartsWith('@') || crafting.Code.Path.Contains('(')
            || AccessTools.Method(ingredient.GetType(), "SatisfiesAsIngredient") != matcher
            || Harmony.GetPatchInfo(matcher)?.Owners.Any() == true
            || Harmony.GetPatchInfo(wildcard)?.Owners.Any() == true) return all;
        string path = crafting.Code.Path;
        int star = path.IndexOf('*');
        string prefix = star < 0 ? path : path[..star];
        if (prefix.Length == 0) return all;
        int lo = 0, hi = paths.Length;
        while (lo < hi)
        {
            int middle = lo + (hi - lo) / 2;
            if (string.CompareOrdinal(paths[middle].Path, prefix) < 0) lo = middle + 1;
            else hi = middle;
        }
        var selected = new List<int>();
        for (int i = lo; i < paths.Length && paths[i].Path.StartsWith(prefix, StringComparison.Ordinal); i++)
            selected.Add(paths[i].Index);
        // Preserve source order and all class/domain/attribute variants. Vanilla
        // still checks allowed/skipped variants, tags and dynamic item behavior.
        selected.Sort();
        return selected.Select(i => all[i]).ToArray();
    }
}
