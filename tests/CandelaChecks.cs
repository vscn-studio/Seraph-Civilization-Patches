using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

static class CandelaChecks
{
    public static void Run(string game)
    {
        int count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }

        var enabled = new HashSet<string>();
        var loader = Proxy.Create<IModLoader>((method, arguments) =>
            method.Name == "IsModEnabled" && enabled.Contains((string)arguments![0]!));
        var logger = Proxy.Create<ILogger>((_, _) => null);
        var api = Proxy.Create<ICoreAPI>((method, _) => method.Name switch
        {
            "get_ModLoader" => loader,
            "get_Logger" => logger,
            _ => null
        });
        Assembly fixAssembly = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly;
        Type candelaType = fixAssembly.GetType("SeraphCivilizationPatches.CandelaClickUpTorchesCompatibility", true)!;
        MethodInfo createCandela = candelaType.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static)!;

        Check(createCandela.Invoke(null, [api]) == null, "Candela compatibility remains inactive when Candela and Click Up Torches are absent");
        enabled.UnionWith(["candela", "clickuptorches"]);
        using var candela = (IDisposable?)createCandela.Invoke(null, [api]);
        Check(candela != null, "Candela compatibility installs when both torch mods are enabled");

        var pickupMode = AccessTools.Field(typeof(BlockBehaviorRightClickPickup), "dropsPickupMode")!;
        MethodInfo prefix = candelaType.GetMethod("UseDropsForTorchPickup", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo postfix = candelaType.GetMethod("RestorePickupMode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var torchPickup = new BlockBehaviorRightClickPickup(new BlockTorch());
        pickupMode.SetValue(torchPickup, false);
        object?[] prefixArgs = [torchPickup, null];
        prefix.Invoke(null, prefixArgs);
        Check((bool)pickupMode.GetValue(torchPickup)!, "torch pickup temporarily uses GetDrops so Candela can stamp remaining burn time");
        postfix.Invoke(null, [torchPickup, prefixArgs[1]!]);
        Check(!(bool)pickupMode.GetValue(torchPickup)!, "torch pickup restores Click Up Torches' original pickup mode after the interaction");

        var otherPickup = new BlockBehaviorRightClickPickup(new Block());
        pickupMode.SetValue(otherPickup, false);
        object?[] otherArgs = [otherPickup, null];
        prefix.Invoke(null, otherArgs);
        Check(!(bool)pickupMode.GetValue(otherPickup)! && !(bool)otherArgs[1]!, "non-torch right-click pickups retain their existing behavior");
        Console.WriteLine($"{count} Candela compatibility checks passed.");
    }

}
