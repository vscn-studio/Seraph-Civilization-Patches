using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

/// <summary>Defer climate updates on a hive section which has not initialized yet.</summary>
public sealed class FromGoldenCombsCompatibilitySystem : ModSystem
{
    private const string HarmonyId = "vsscpmod.fromgoldencombs-initialization";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<FromGoldenCombsCompatibilitySystem> owners = [];
    private static ConditionalWeakTable<BlockEntity, object> reported = new();

    public override double ExecuteOrder() => 1.4;

    public override void Start(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("fromgoldencombs") || owners.Contains(this)) return;
        if (owners.Count == 0)
        {
            try
            {
                var type = AccessTools.TypeByName("FromGoldenCombs.BlockEntities.BELangstrothStack")
                    ?? throw new TypeLoadException("FromGoldenCombs BELangstrothStack");
                var method = AccessTools.DeclaredMethod(type, "UpdateClimateValues", [typeof(ICoreAPI)])
                    ?? throw new MissingMethodException(type.FullName, "UpdateClimateValues");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(FromGoldenCombsCompatibilitySystem), nameof(BeforeClimate)));
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(HarmonyId);
                api.Logger.Warning("[vsscpmod] FromGoldenCombs initialization compatibility unavailable: {0}", e.Message);
                return;
            }
        }
        owners.Add(this);
        api.Logger.Notification("[vsscpmod] FromGoldenCombs stacked-hive initialization compatibility enabled.");
    }

    private static bool BeforeClimate(BlockEntity __instance, ICoreAPI api)
    {
        // Initialize on an upper section calls bottomStack.UpdateClimateValues(api).
        // The bottom entity may already exist in the chunk but not yet have its Api.
        // Do not assign Api or call Initialize manually: its own Initialize will run
        // the normal climate update and register the normal five-second hive tick.
        if (__instance.Api != null) return true;
        if (!reported.TryGetValue(__instance, out _))
        {
            reported.Add(__instance, new object());
            api?.Logger.Warning("[vsscpmod] Deferred FromGoldenCombs climate update at {0}: bottom hive section is not initialized yet.", __instance.Pos);
        }
        return false;
    }

    public override void Dispose()
    {
        if (owners.Remove(this) && owners.Count == 0)
        {
            harmony.UnpatchAll(HarmonyId);
            reported = new();
        }
        base.Dispose();
    }
}
