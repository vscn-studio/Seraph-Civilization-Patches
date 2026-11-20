using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeraphCivilizationPatches;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

static class FromGoldenCombsChecks
{
    public static void Run(string dll)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(dll));
        var type = assembly.GetType("FromGoldenCombs.BlockEntities.BELangstrothStack", true)!;
        var update = AccessTools.DeclaredMethod(type, "UpdateClimateValues", [typeof(ICoreAPI)]);
        var configType = assembly.GetType("FromGoldenCombs.Util.Config.FGCServerConfig", true)!;
        AccessTools.Property(configType, "Current").SetValue(null, Activator.CreateInstance(configType));
        var entity = (BlockEntity)RuntimeHelpers.GetUninitializedObject(type);
        entity.Pos = new BlockPos(510081, 140, 513440);
        int warnings = 0, climateCalls = 0;
        bool enabled = true;
        var logger = Proxy.Create<ILogger>((m, _) => { if (m.Name == "Warning") warnings++; return null; });
        var calendar = Proxy.Create<IGameCalendar>((m, _) => m.Name switch
        {
            "get_TotalHours" => 2400d, "get_TotalDays" => 100d,
            "get_DaysPerMonth" => 30, "get_HoursPerDay" => 24f, _ => null
        });
        var accessor = Proxy.Create<IBlockAccessor>((m, a) =>
        {
            if (m.Name != "GetClimateAt") return null;
            climateCalls++;
            return new ClimateCondition { Temperature = 20 };
        });
        var world = Proxy.Create<IWorldAccessor>((m, _) => m.Name switch
        { "get_Calendar" => calendar, "get_BlockAccessor" => accessor, "get_Logger" => logger, _ => null });
        var mods = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" && enabled);
        var api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_Logger" => logger, "get_ModLoader" => mods, "get_Side" => EnumAppSide.Server, _ => null });
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
        }
        void Update() => update.Invoke(entity, [api]);
        bool Crashes()
        {
            try { Update(); return false; }
            catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { return true; }
        }
        Check(Crashes(), "real FGC 2.1.0 throws when upper hive updates an uninitialized bottom entity");
        var system = new FromGoldenCombsCompatibilitySystem();
        var second = new FromGoldenCombsCompatibilitySystem();
        try
        {
            enabled = false; system.Start(api);
            Check(Crashes(), "absent mod does not install a patch");
            enabled = true; system.Start(api); system.Start(api);
            Check(Harmony.GetPatchInfo(update)?.Owners.Contains("vsscpmod.fromgoldencombs-initialization") == true,
                "patch installed on actual mod method");
            Update(); Update();
            Check(warnings == 1 && climateCalls == 0 && entity.Api == null,
                "early updates defer once without forcing initialization or inventing climate data");
            entity.Api = api;
            AccessTools.Field(type, "minTemp").SetValue(entity, 5f);
            AccessTools.Field(type, "maxTemp").SetValue(entity, 35f);
            AccessTools.Field(type, "harvestableAtTotalHours").SetValue(entity, 2472d);
            Update();
            Check(climateCalls == 5 && (float)AccessTools.Field(type, "threeDayTemp").GetValue(entity)! == 20f,
                "initialized hive resumes the real climate calculations");
            Check((int)AccessTools.Field(type, "daysTillHarvest").GetValue(entity)! == 3 &&
                (float)AccessTools.Field(type, "harvestBase").GetValue(entity)! == 84f,
                "original harvest duration and remaining days are preserved");
            second.Start(api); system.Dispose(); entity.Api = null;
            Update();
            Check(warnings == 1, "second owner keeps the patch alive without repeated warnings");
            second.Dispose();
            Check(Crashes(), "disposing the final owner restores upstream behavior");
        }
        finally { system.Dispose(); second.Dispose(); }
    }
}
