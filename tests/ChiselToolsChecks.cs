using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

static class ChiselToolsChecks
{
    public static void Run(string dll)
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }

        Assembly chisel = Assembly.LoadFrom(Path.GetFullPath(dll));
        Type planer = chisel.GetType("chisel.src.ItemHandPlaner", true)!;
        var setMode = AccessTools.DeclaredMethod(planer, "SetToolMode")!;
        var attack = AccessTools.DeclaredMethod(planer, "OnHeldAttackStart")!;
        var interact = AccessTools.DeclaredMethod(planer, "OnHeldInteractStart")!;
        bool enabled = true;
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" && (string)a![0]! == "chiseltools" && enabled);
        var logger = Proxy.Create<ILogger>((_, _) => null);
        var api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader,
            "get_Logger" => logger,
            _ => null
        });
        Type compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.ChiselToolsHandPlanerCompatibility", true)!;
        MethodInfo create = compatibility.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static)!;
        IDisposable? instance = null;
        try
        {
            instance = (IDisposable?)create.Invoke(null, [api]);
            const string owner = "vsscpmod.chiseltools-hand-planer";
            Check(instance != null, "real QP Chisel Tools compatibility initializes");
            Check(new[] { setMode, attack, interact }.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) == true),
                "hand planer mode and operation methods are patched");

            var stack = new ItemStack(new Item { Code = new AssetLocation("test:handplaner") });
            stack.Attributes.SetInt("toolMode", 0);
            stack.Attributes.SetInt("lastToolMode", 1);
            var slot = new DummySlot(stack);
            MethodInfo beforeUse = compatibility.GetMethod("BeforeUse", BindingFlags.NonPublic | BindingFlags.Static)!;
            beforeUse.Invoke(null, [slot]);
            Check(stack.Attributes.GetInt("lastToolMode", -1) == 0,
                "current visible hand planer mode is copied to the server operation mode");
        }
        finally
        {
            instance?.Dispose();
        }
        Check(Harmony.GetPatchInfo(setMode)?.Owners.Contains("vsscpmod.chiseltools-hand-planer") != true,
            "disposing compatibility removes hand planer patches");
        Console.WriteLine($"{checks} Chisel Tools checks passed.");
    }
}
