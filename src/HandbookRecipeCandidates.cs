using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace SeraphCivilizationPatches;

// Narrow only the enumeration. Vanilla still resolves, matches attributes,
// chooses the first matching alternative, clones and scales liquid portions.
internal sealed class HandbookRecipeCandidates
{
    // Audited ACA 2.0.0-dev.28: this prefix only adds ignored attributes;
    // code/ID/wildcard selection is identical to vanilla. Unknown builds keep
    // the conservative fallback instead of silently losing mod ingredients.
    internal static readonly Guid CulinaryBuild = new("c290f196-6b84-41e7-a484-7e8d4fec7424");
    // Toolsmith changes only modular-part attributes after class and ID agree.
    internal static readonly Guid ToolsmithBuild = new("ea4a83af-a1c9-47d9-bee2-7dd37e73dd05");
    // Komet 2.0.0 only reuses IgnoredStackAttributes + timeFrozen arrays in
    // GetMatchingStack; it leaves code, wildcard and equality calls intact.
    internal static readonly Guid KometBuild = new("e77f348b-a3d9-43b5-bac7-0119ec273725");
    private readonly ItemStack[] all;
    private readonly Dictionary<(EnumItemClass, int), List<int>> byId = new();
    private readonly Dictionary<string, int[]> wildcards = new(StringComparer.Ordinal);
    private readonly List<int> customEquality = new();
    private readonly bool patchedEquality;
    public long Scanned { get; private set; }
    public long Selected { get; private set; }

    public HandbookRecipeCandidates(ItemStack[] all)
    {
        this.all = all;
        var equals = AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.Equals),
            [typeof(ItemStack), typeof(ItemStack), typeof(string[])]);
        patchedEquality = HasUnknownPatches(equals, KnownEquality);
        var customTypes = new Dictionary<Type, bool>();
        for (int i = 0; i < all.Length; i++)
        {
            var stack = all[i];
            var key = (stack.Class, stack.Id);
            if (!byId.TryGetValue(key, out var indices)) byId[key] = indices = new();
            indices.Add(i);
            Type type = stack.Collectible.GetType();
            if (!customTypes.TryGetValue(type, out bool custom))
            {
                var method = AccessTools.Method(type, nameof(CollectibleObject.Equals),
                    [typeof(ItemStack), typeof(ItemStack), typeof(string[])]);
                customTypes[type] = custom = method.DeclaringType != typeof(CollectibleObject)
                    || HasUnknownPatches(method, KnownEquality);
            }
            if (custom) customEquality.Add(i);
        }
    }

    public ItemStack[] For(object ingredient)
    {
        Scanned += all.Length;
        // Patched matchers can accept stacks outside vanilla code/ID rules.
        var match = AccessTools.Method(ingredient.GetType(), "GetMatchingStack");
        if (patchedEquality || HasUnknownPatches(match, KnownMatcher))
            return Record(all);
        var selected = new HashSet<int>(customEquality);
        var alternatives = (Array)AccessTools.Field(ingredient.GetType(), "ValidStacks").GetValue(ingredient)!;
        foreach (JsonItemStack alternative in alternatives)
        {
            if (alternative.Code.Path.Contains('*'))
            {
                string key = alternative.Code.ToString();
                if (!wildcards.TryGetValue(key, out int[]? indices))
                {
                    // WildCardMatch does not check Item/Block class. Keep that
                    // behavior, including variants and cross-class matches.
                    indices = Enumerable.Range(0, all.Length)
                        .Where(i => all[i].Collectible.WildCardMatch(alternative.Code)).ToArray();
                    wildcards[key] = indices;
                }
                selected.UnionWith(indices);
            }
            else
            {
                Add(alternative.ResolvedItemstack);
            }
            var cooked = (JsonItemStack?)AccessTools.Field(alternative.GetType(), "CookedStack")?.GetValue(alternative);
            Add(cooked?.ResolvedItemstack);
        }
        // Preserve handbook order and duplicate stacks. The original matcher
        // remains responsible for alternative precedence and attributes.
        return Record(selected.Order().Select(i => all[i]).ToArray());

        void Add(ItemStack? stack)
        {
            if (stack != null && byId.TryGetValue((stack.Class, stack.Id), out var indices)) selected.UnionWith(indices);
        }
    }

    private ItemStack[] Record(ItemStack[] stacks) { Selected += stacks.Length; return stacks; }

    private static bool KnownMatcher(MethodInfo method) => method.Module.ModuleVersionId == CulinaryBuild
        && method.DeclaringType?.FullName == "ACulinaryArtillery.CookingRecipeIngredientPatcher" && method.Name == "displayFix"
        || method.Module.ModuleVersionId == KometBuild
        && method.DeclaringType?.FullName == "Komet.World.CookingMatch" && method.Name == "RewriteMatching";

    internal static bool KnownEquality(MethodInfo method) => method.Module.ModuleVersionId == ToolsmithBuild
        && method.DeclaringType?.FullName == "Toolsmith.ToolTinkering.ToolTinkeringCraftingPatches"
        && method.Name == "EqualsModularPartPrefix";

    internal static bool HasUnknownPatches(MethodInfo method, System.Func<MethodInfo, bool> known)
    {
        var patches = Harmony.GetPatchInfo(method);
        return patches != null && patches.Prefixes.Concat(patches.Postfixes)
            .Concat(patches.Transpilers).Concat(patches.Finalizers).Any(p => !known(p.PatchMethod));
    }

    public static void Install(Harmony harmony, Type system)
    {
        var closure = system.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .Single(m => m.Name.StartsWith("<CreateCachedMealRecipeStacks>b__", StringComparison.Ordinal));
        harmony.Patch(closure, transpiler: new HarmonyMethod(typeof(HandbookRecipeCandidates), nameof(NarrowEnumeration)));
        Type? culinary = AccessTools.TypeByName("ACulinaryArtillery.Util.HandbookInfoExtensions");
        if (culinary?.Module.ModuleVersionId != CulinaryBuild) return;
        var culinaryClosure = culinary.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            .Single(m => m.Name.StartsWith("<CreateCachedMealRecipeStacks>b__", StringComparison.Ordinal));
        harmony.Patch(culinaryClosure, transpiler: new HarmonyMethod(typeof(HandbookRecipeCandidates), nameof(NarrowCulinaryEnumeration)));
    }

    private static IEnumerable<CodeInstruction> NarrowCulinaryEnumeration(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int local = __originalMethod.GetMethodBody()!.LocalVariables
            .Single(l => l.LocalType.FullName == "Vintagestory.GameContent.CookingRecipeIngredient").LocalIndex;
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field
                && field.FieldType == typeof(ItemStack[]) && field.Name == "allstacks")
            {
                yield return new CodeInstruction(OpCodes.Ldloc, local);
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(HandbookSearchCache), nameof(HandbookSearchCache.NarrowStacks)));
                replaced++;
            }
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported ACA handbook recipe enumeration.");
    }

    private static IEnumerable<CodeInstruction> NarrowEnumeration(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        int local = __originalMethod.GetMethodBody()!.LocalVariables
            .Single(l => l.LocalType.FullName == "Vintagestory.GameContent.CookingRecipeIngredient").LocalIndex;
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(ObjectCacheUtil)
                && method.Name == nameof(ObjectCacheUtil.TryGet) && method.IsGenericMethod
                && method.GetGenericArguments().SequenceEqual(new[] { typeof(ItemStack[]) }))
            {
                var load = new CodeInstruction(OpCodes.Ldloc, local);
                load.labels.AddRange(instruction.labels); instruction.labels.Clear();
                load.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return load;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(HandbookSearchCache), nameof(HandbookSearchCache.RecipeCandidates));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Unsupported handbook recipe enumeration.");
    }
}
