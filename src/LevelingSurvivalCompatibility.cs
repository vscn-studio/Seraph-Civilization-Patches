using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

/// <summary>Optional, read-only projection into SLSS's existing snapshot. Never writes skill or body stats.</summary>
internal sealed class LevelingSurvivalCompatibility : IDisposable
{
    private const string Id = "vsscpmod.leveling-survival";
    private static readonly Harmony harmony = new(Id);
    private static readonly HashSet<LevelingSurvivalCompatibility> owners = [];
    private static PropertyInfo player = null!;
    private static MethodInfo actions = null!;
    private static Type metricType = null!;
    private static Dictionary<string, PropertyInfo> metricProperties = null!;
    private static ILogger logger = null!;
    private static bool reportedFailure;

    internal static bool Enabled(IModLoader loader) => loader.IsModEnabled("seraphleveling") && loader.IsModEnabled("vsslssmod");

    public static LevelingSurvivalCompatibility? TryCreate(ICoreAPI api)
    {
        if (!Enabled(api.ModLoader)) return null;
        if (owners.Count == 0)
        {
            try
            {
                var coordinator = AccessTools.TypeByName("Seraph.Server.ServerCoordinator") ?? throw new TypeLoadException("SLSS ServerCoordinator");
                var target = RequireMethod(coordinator, "FunctionMetrics");
                if (target.GetParameters().Length != 1 || !target.ReturnType.IsArray) throw new MissingMethodException("FunctionMetrics(Session) -> PanelMetric[]");
                player = target.GetParameters()[0].ParameterType.GetProperty("Player") ?? throw new MissingMemberException("Session.Player");
                if (!typeof(IServerPlayer).IsAssignableFrom(player.PropertyType)) throw new MissingMemberException("Session.Player type");
                actions = RequireMethod(coordinator, "ActionFunctions", typeof(Entity));
                foreach (var name in new[] { "Manipulation", "CanUseActiveHand", "CanDrawBow" }) RequireProperty(actions.ReturnType, name, typeof(bool));
                foreach (var name in new[] { "Mining", "Bow", "MeleeAttackSpeed" }) RequireProperty(actions.ReturnType, name, typeof(double));
                var metric = target.ReturnType.GetElementType()!;
                foreach (var name in new[] { "Key", "Unit", "Text", "Detail" }) RequireProperty(metric, name, typeof(string));
                foreach (var name in new[] { "Value", "Minimum", "Maximum" }) RequireProperty(metric, name, typeof(double));
                foreach (var name in new[] { "Available", "Bar" }) RequireProperty(metric, name, typeof(bool));
                if (metric.GetConstructor(Type.EmptyTypes) is null) throw new MissingMethodException("PanelMetric constructor");
                if (metric.GetProperties().Where(p => new[] { "Key", "Unit", "Text", "Detail", "Value", "Minimum", "Maximum", "Available", "Bar" }.Contains(p.Name)).Any(p => !p.CanWrite))
                    throw new MissingMemberException("PanelMetric setters");
                metricType = metric;
                metricProperties = metric.GetProperties().ToDictionary(p => p.Name);
                LevelingEffects.Bind(AccessTools.TypeByName("SeraphLeveling.SeraphLevelingModSystem") ?? throw new TypeLoadException("SeraphLevelingModSystem"), api.Logger);
                logger = api.Logger;
                reportedFailure = false;
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(LevelingSurvivalCompatibility), nameof(Append)));
            }
            catch (Exception ex)
            {
                harmony.UnpatchAll(Id);
                api.Logger.Warning("[vsscpmod] Leveling/SLSS snapshot integration disabled: {0}", ex.GetBaseException().Message);
                return null;
            }
        }
        var owner = new LevelingSurvivalCompatibility();
        owners.Add(owner);
        api.Logger.Notification("[vsscpmod] Leveling/SLSS authoritative skill metrics enabled; native bonuses remain single-applied.");
        return owner;
    }

    private static void Append(object __instance, object __0, ref object __result)
    {
        LevelingEffects.Result[] results;
        try
        {
            var p = (IServerPlayer)player.GetValue(__0)!;
            results = LevelingEffects.Read(p.Entity, actions.Invoke(__instance, [p.Entity]), p.LanguageCode);
        }
        catch (Exception ex)
        {
            if (!reportedFailure) { reportedFailure = true; logger.Warning("[vsscpmod] Skill metrics unavailable: {0}", ex.GetBaseException().Message); }
            results = LevelingEffects.Definitions.Select(d => new LevelingEffects.Result(d.Key, 0, false, "", "")).ToArray();
        }
        // Rebuilding a snapshot is idempotent even if an upstream caller reuses an array.
        var existing = ((Array)__result).Cast<object>().Where(m => !((string)metricProperties["Key"].GetValue(m)!).StartsWith(LevelingEffects.Prefix, StringComparison.Ordinal)).ToArray();
        var combined = Array.CreateInstance(metricType, existing.Length + results.Length);
        for (int i = 0; i < existing.Length; i++) combined.SetValue(existing[i], i);
        for (int i = 0; i < results.Length; i++) combined.SetValue(CreateMetric(results[i]), existing.Length + i);
        __result = combined;
    }

    private static object CreateMetric(LevelingEffects.Result result)
    {
        object metric = Activator.CreateInstance(metricType)!;
        foreach (var (name, value) in new (string, object)[] {
            ("Key", result.Key), ("Value", result.Value), ("Available", result.Available),
            ("Text", result.Text), ("Detail", result.Detail), ("Minimum", 0d), ("Maximum", 1d), ("Unit", ""), ("Bar", false) })
            metricProperties[name].SetValue(metric, value);
        return metric;
    }

    internal static MethodInfo RequireMethod(Type type, string name, params Type[] args)
        => (args.Length == 0 ? AccessTools.DeclaredMethod(type, name) : AccessTools.DeclaredMethod(type, name, args))
            ?? throw new MissingMethodException(type.FullName, name);
    internal static PropertyInfo RequireProperty(Type type, string name, Type value)
    {
        var p = type.GetProperty(name) ?? throw new MissingMemberException(type.FullName, name);
        if (p.PropertyType != value) throw new MissingMemberException(type.FullName, name + " type");
        return p;
    }

    public void Dispose()
    {
        owners.Remove(this);
        if (owners.Count == 0) harmony.UnpatchAll(Id);
    }
}
