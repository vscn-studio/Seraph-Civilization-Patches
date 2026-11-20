using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace SeraphCivilizationPatches;

internal static class LevelingEffects
{
    internal const string Prefix = "vsscpmod.skill.";
    internal sealed record Definition(string Stat, string Kind = "factor", string Gate = "", string[]? Remaining = null)
    {
        internal string Key => Prefix + Stat;
    }
    internal sealed record Result(string Key, double Value, bool Available, string Text, string Detail);
    internal static readonly Definition[] Definitions = [
        new("miningSpeedMul", Gate: "hand", Remaining: ["sitWeakMiningRemaining", "sitClaustrophobicMiningRemaining"]),
        new("meleeWeaponsDamage", Gate: "hand", Remaining: ["sitFarsightedRemaining", "sitNervousRemaining"]),
        new("meleeTiming", Gate: "hand"), new("rangedWeaponsDamage", Remaining: ["sitNearsightedRemaining"]),
        new("rangedWeaponsAcc"), new("bowDrawingStrength", Remaining: ["sitFrailDistanceRemaining"]), new("bowTiming", Gate: "bow"),
        new("walkspeed"), new("hungerrate", Remaining: ["sitRavenousRemaining"]), new("healingeffectivness"),
        new("maxhealthExtraPoints", "points", Remaining: ["sitWeakHpRemaining", "sitFrailHpRemaining"]),
        new("armorDurabilityLoss"), new("armorWalkSpeedAffectedness"), new("mechanicalsDamage"),
        new("animalLootDropRate", Remaining: ["sitKindLootRemaining"]), new("animalHarvestingTime", Gate: "hand", Remaining: ["sitKindSpeedRemaining"]),
        new("forageDropRate", Remaining: ["sitCivilRemaining", "sitHeavyhandedForagingRemaining"]),
        new("wildCropDropRate", Remaining: ["sitHeavyhandedWildCropRemaining"]), new("oreDropRate", Remaining: ["sitClaustrophobicOreRemaining"]),
        new("rustyGearDropRate"), new("vesselContentsDropRate"), new("wholeVesselLootChance", "chance", Remaining: ["sitHeavyhandedVesselRemaining"]),
        new("animalSeekingRange"), new("temporalGearTLRepairCost", "points"), new("unlocks", "unlocks")
    ];
    private static readonly string[] unlocks = ["Clothier", "Technical", "HardyHealth", "Bowyer", "Improviser", "Tinkerer", "Merciless"];
    private static MethodInfo? bowScale, meleeScale;
    private static FieldInfo? bowEnabled, meleeEnabled, bowPatched, meleePatched;

    internal static void Bind(Type leveling, ILogger logger)
    {
        // Optional timing contracts fail independently; all native stat rows remain useful.
        var bow = leveling.Assembly.GetType("SeraphLeveling.BowDrawSpeedPatches");
        var melee = leveling.Assembly.GetType("SeraphLeveling.MeleeAttackSpeedPatches");
        bowScale = AccessTools.Method(bow, "Scale", [typeof(float), typeof(EntityAgent)]);
        meleeScale = AccessTools.Method(melee, "GetMeleeSpeedMultiplier", [typeof(EntityAgent)]);
        bowEnabled = AccessTools.Field(leveling, "RangedDrawSpeedEnabled");
        meleeEnabled = AccessTools.Field(leveling, "MeleeAttackSpeedEnabled");
        bowPatched = AccessTools.Field(bow, "PatchedInProcess");
        meleePatched = AccessTools.Field(melee, "patchedInProcess");
        if (bowScale?.ReturnType != typeof(float) || bowEnabled?.FieldType != typeof(bool) || bowPatched?.FieldType != typeof(bool))
            logger.Warning("[vsscpmod] Leveling bow timing contract unavailable; its metric will be unavailable.");
        if (meleeScale?.ReturnType != typeof(float) || meleeEnabled?.FieldType != typeof(bool) || meleePatched?.FieldType != typeof(bool))
            logger.Warning("[vsscpmod] Leveling melee timing contract unavailable; its metric will be unavailable.");
    }

    internal static Result[] Read(EntityPlayer entity, object? action, string language)
        => Definitions.Select(d => ReadOne(d, entity, action, language)).ToArray();

    private static Result ReadOne(Definition d, EntityPlayer entity, object? action, string language)
    {
        string L(string key, params object[] args) => Lang.GetL(language, "vsscpmod:skill-" + key, args);
        var lines = new List<string> { L("scope-" + d.Stat) };
        if (d.Kind == "unlocks")
        {
            var active = unlocks.Where(u => entity.WatchedAttributes.GetBool("sit" + u + "Unlocked")).ToArray();
            lines.AddRange(unlocks.Select(u => L("unlock-" + u) + ": " + L(active.Contains(u) ? "unlocked" : "locked")));
            return new(d.Key, active.Length, true, active.Length + "/" + unlocks.Length, string.Join("\n", lines));
        }
        double value;
        if (d.Stat is "meleeTiming" or "bowTiming")
        {
            bool bow = d.Stat == "bowTiming";
            var flag = bow ? bowEnabled : meleeEnabled;
            var method = bow ? bowScale : meleeScale;
            var patched = bow ? bowPatched : meleePatched;
            if (flag?.FieldType != typeof(bool) || method?.ReturnType != typeof(float))
                return new(d.Key, 0, false, "", L("unavailable"));
            bool enabled = (bool)flag.GetValue(null)!;
            if (enabled && patched?.GetValue(null) is not true) return new(d.Key, 0, false, "", L("unavailable"));
            value = enabled ? Convert.ToDouble(method.Invoke(null, bow ? [1f, entity] : [entity]), CultureInfo.InvariantCulture) : 1;
            lines.Add(L(enabled ? "timing-enabled" : "timing-disabled", N(value)));
        }
        else
        {
            var stat = entity.Stats.FirstOrDefault(p => p.Key == d.Stat).Value;
            value = entity.Stats.GetBlended(d.Stat);
            var blend = stat?.BlendType ?? EnumStatBlendType.WeightedSum;
            bool additive = blend is EnumStatBlendType.WeightedSum or EnumStatBlendType.FlatSum;
            lines.Add(L("sources-title"));
            if (stat is null) lines.Add(L("source", L("base"), d.Kind is "points" or "chance" ? "0" : "×1"));
            else foreach (var group in stat.ValuesByKey.GroupBy(p => SourceKind(p.Key)))
            {
                if (group.Any(p => !float.IsFinite(p.Value.Value) || !float.IsFinite(p.Value.Weight)))
                    return new(d.Key, 0, false, "", L("unavailable"));
                if (additive)
                {
                    var contribution = group.Sum(p => (double)p.Value.Value * (blend == EnumStatBlendType.WeightedSum ? p.Value.Weight : 1));
                    if (group.Key == "base") lines.Add(L("source", L("base"), d.Kind is "points" or "chance" ? Format(contribution - 1, "points") : "×" + N(contribution)));
                    else if (Math.Abs(contribution) > .000001)
                        lines.Add(L("source", L(group.Key), d.Kind == "points" ? L("points", Format(contribution, "points"))
                            : L("percentage-points", Format(contribution * 100, "points"))));
                }
                else
                {
                    // Preserve the order/weights of non-additive sources; do not turn them into an additive breakdown.
                    foreach (var item in group)
                        lines.Add(L("source", L(group.Key), L("factor-weight", "×" + N(item.Value.Value), N(item.Value.Weight))));
                }
            }
            if (!additive) lines.Add(L("combined-sources"));
            if (d.Remaining is { } remaining)
                foreach (var key in remaining)
                    if (entity.WatchedAttributes[key]?.GetValue() is IConvertible raw)
                    {
                        double amount = raw.ToDouble(CultureInfo.InvariantCulture);
                        if (double.IsFinite(amount) && amount > 0) lines.Add(L("remaining", L(key), N(amount)));
                    }
            // These two categories are offsets from the implicit native base of one, not multipliers.
            if (d.Kind == "points") value -= 1;
            if (d.Kind == "chance") value = Math.Clamp(value - 1, 0, 1);
            // Match SLSS's guards at its physiology input boundary (not an extra multiplier).
            if (d.Stat is "hungerrate" or "healingeffectivness" && (!double.IsFinite(value) || value < 0 || value > 100))
            { value = 1; lines.Add(L("physiology-fallback")); }
        }
        if (!double.IsFinite(value)) return new(d.Key, 0, false, "", L("unavailable"));
        bool applies = action is not null && (bool)action.GetType().GetProperty("Manipulation")!.GetValue(action)!;
        bool blocked = applies && d.Gate.Length > 0 && !(bool)action!.GetType().GetProperty(d.Gate == "bow" ? "CanDrawBow" : "CanUseActiveHand")!.GetValue(action)!;
        if (applies && !blocked && d.Stat is "miningSpeedMul" or "bowTiming" or "meleeTiming")
        {
            string property = d.Stat == "miningSpeedMul" ? "Mining" : d.Stat == "bowTiming" ? "Bow" : "MeleeAttackSpeed";
            double body = (double)action!.GetType().GetProperty(property)!.GetValue(action)!;
            if (property == "MeleeAttackSpeed") body = Math.Clamp(body, 1, 1.2);
            value *= body;
            lines.Add(L("body-factor", N(body)));
        }
        if (d.Gate.Length > 0) lines.Add(L(!applies ? "gate-inactive" : blocked ? "gate-blocked" : "gate-open"));
        string text = blocked ? L("blocked") : Format(value, d.Kind);
        lines.Insert(0, L("final", text));
        return new(d.Key, blocked ? 0 : value, double.IsFinite(value), text, string.Join("\n", lines));
    }

    private static string SourceKind(string source) => source switch {
        "base" => "base", "trait" => "class", "sitHungerBonus" => "hunger-adjustment", "vsslssmod:function" => "body",
        "wearablemod" => "equipment",
        _ when source.StartsWith("sit", StringComparison.Ordinal) => source.Contains("Cancel", StringComparison.Ordinal)
            || source.Contains("Removal", StringComparison.Ordinal) ? "cancellation" : "reward",
        _ => "other"
    };

    internal static string Format(double value, string kind) => kind switch {
        "points" => value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture),
        "chance" => (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%", _ => "×" + N(value)
    };
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
