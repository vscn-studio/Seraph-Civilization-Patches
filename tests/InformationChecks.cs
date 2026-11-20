using System.Reflection;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

static class InformationChecks
{
    public static void Run()
    {
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((method, arguments) => method.Name switch
        {
            "HasTranslation" => true,
            "Get" => arguments![0] is "vsscpmod:map-location-hidden" ? "Location hidden" : arguments[0],
            _ => null
        });
        Lang.ChangeLanguage("en");
        Assembly assembly = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly;
        Type restrictions = assembly.GetType("SeraphCivilizationPatches.InformationRestrictions")!;
        MethodInfo blocks = restrictions.GetMethod("BlocksCommand", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Check((bool)blocks.Invoke(null, ["tp", false])!, "server /tp is restricted");
        Check((bool)blocks.Invoke(null, ["tpwp", false])!, "server /tpwp is restricted");
        Check((bool)blocks.Invoke(null, ["player entity", false])!, "server /player entity is restricted");
        Check((bool)blocks.Invoke(null, ["waypoint addati", false])!, "server waypoint coordinate command is restricted");
        Check((bool)blocks.Invoke(null, ["stars goto-lat 45", false])! == false, "AstraTerra latitude command remains available");
        Check((bool)blocks.Invoke(null, ["time get", false])!, "server /time get is restricted");
        Check((bool)blocks.Invoke(null, ["tp", true])! == false, "client command matcher does not block server-only /tp");
        Check((bool)blocks.Invoke(null, ["cp apos", true])!, "client .cp apos is restricted");
        Check((bool)blocks.Invoke(null, ["stars sightings", true])! == false, "AstraTerra observation records remain available");

        Type displays = assembly.GetType("SeraphCivilizationPatches.InformationDisplayPatches")!;
        MethodInfo removeClock = displays.GetMethod("RemoveClock", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        MethodInfo hideCoordinates = displays.GetMethod("HideCoordinateTriples", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        string noClock = (string)removeClock.Invoke(null, ["Day 3 12:34:56, ready"])!;
        Check(!noClock.Contains("12:34:56"), "dynamic text clock values are removed");
        string noCoordinates = (string)hideCoordinates.Invoke(null, ["Marker at 12, 34, -56"])!;
        Check(!noCoordinates.Contains("12, 34, -56"), "dynamic text coordinate triples are removed");
        Console.WriteLine("Information checks passed.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
}
