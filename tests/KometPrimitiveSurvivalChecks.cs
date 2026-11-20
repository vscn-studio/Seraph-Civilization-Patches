using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using SeraphCivilizationPatches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

static class KometPrimitiveSurvivalChecks
{
    private sealed class TestMod : Mod { }

    public static void Run(string root, string game, string modsDirectory)
    {
        string inputs = Path.Combine(root, "tests/dependencies/komet-primitive");
        Directory.CreateDirectory(inputs);
        string kometZip = TestPackages.Find(modsDirectory, "Komet-3.0.0-pre.3.zip");
        string primitiveZip = TestPackages.Find(modsDirectory, "Primitive Survival-5.1.4.zip");
        foreach (string path in new[] { kometZip, primitiveZip })
        {
            using var zip = ZipFile.OpenRead(path);
            using var metadata = JsonDocument.Parse(zip.GetEntry("modinfo.json")!.Open());
            string expectedVersion = path == kometZip ? "3.0.0-pre.3" : "5.1.4";
            if (metadata.RootElement.GetProperty("version").GetString() != expectedVersion)
                throw new InvalidOperationException("This regression requires version " + expectedVersion + ": " + path);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                entry.ExtractToFile(Path.Combine(inputs, Path.GetFileName(entry.FullName)), true);
        }
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string path = Path.Combine(inputs, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        var library = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"));
        var rendering = Assembly.LoadFrom(Path.Combine(inputs, "Komet.Rendering.dll"));
        var komet = Assembly.LoadFrom(Path.Combine(inputs, "Komet.dll"));
        var primitive = Assembly.LoadFrom(Path.Combine(inputs, "primitivesurvival.dll"));
        Type shaders = rendering.GetType("Komet.Rendering.ShaderMods", true)!;
        MethodInfo all = AccessTools.Method(shaders, "All");
        MethodInfo hold = AccessTools.Method(shaders, "HoldBack");
        MethodInfo release = AccessTools.Method(shaders, "Release");
        var off = (HashSet<string>)AccessTools.Property(shaders, "Off").GetValue(null)!;
        Type state = rendering.GetType("Komet.Rendering.ShaderState", true)!;
        object saved = Activator.CreateInstance(state.GetNestedType("Saved", BindingFlags.NonPublic)!)!;
        // Avoid accessing or migrating the user's shader settings in this test.
        AccessTools.Field(state, "_saved").SetValue(null, saved);
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        var errors = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { if (m.Name is "Error" or "Warning") errors.Add(string.Join(" ", a ?? [])); return null; });
        Mod Make(string id, string version, string source)
        {
            var mod = new TestMod();
            AccessTools.Property(typeof(Mod), "Info").SetValue(mod, new ModInfo { ModID = id, Name = id, Version = version });
            AccessTools.Property(typeof(Mod), "SourcePath").SetValue(mod, source);
            AccessTools.Property(typeof(Mod), "SourceType").SetValue(mod, (EnumModSourceType)2);
            AccessTools.Property(typeof(Mod), "Logger").SetValue(mod, logger);
            return mod;
        }
        Mod km = Make("komet", "3.0.0-pre.3", kometZip);
        Mod ps = Make("primitivesurvival", "5.1.4", primitiveZip);
        // The real PS assets under another ID exercise unrelated shader handling.
        Mod other = Make("other-shader", "1.0.0", Path.Combine(inputs, "other-shader.zip"));
        File.Copy(primitiveZip, other.SourcePath, true);
        var enabled = new HashSet<string> { "komet", "primitivesurvival" };
        Mod[] mods = [km, ps, other];
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name switch {
            "IsModEnabled" => enabled.Contains((string)a![0]!),
            "GetMod" => mods.FirstOrDefault(x => x.Info.ModID == (string)a![0]!),
            "get_Mods" => mods, _ => null });
        Type registryType = library.GetType("Vintagestory.Common.ClassRegistry", true)!;
        object registry = Activator.CreateInstance(registryType)!;
        MethodInfo createBehavior = AccessTools.Method(registryType, "CreateCollectibleBehavior");
        int registrations = 0;
        EnumAppSide side = EnumAppSide.Client;
        var api = Proxy.Create<ICoreClientAPI>((m, a) => {
            if (m.Name == "RegisterCollectibleBehaviorClass")
            {
                if ((string)a![0]! == "fishDepletionProbe") registrations++;
                AccessTools.Method(registryType, m.Name).Invoke(registry, a);
            }
            return m.Name switch { "get_ModLoader" => loader, "get_Logger" => logger, "get_Side" => side, _ => null };
        });
        var psSystem = (ModSystem)Activator.CreateInstance(primitive.GetType("PrimitiveSurvival.ModSystem.PrimitiveSurvivalSystem", true)!)!;
        AccessTools.Property(typeof(ModSystem), "Mod").SetValue(psSystem, ps);
        Type loaderType = library.GetType("Vintagestory.Common.ModLoader", true)!;
        object realLoader = RuntimeHelpers.GetUninitializedObject(loaderType);
        MethodInfo phase = AccessTools.Method(loaderType, "TryRunModPhase");
        object start = Enum.Parse(phase.GetParameters()[3].ParameterType, "Start");
        void StartPrimitive() => Check((bool)phase.Invoke(realLoader, [ps, psSystem, api, start])!, "real mod loader accepts PS Start phase");
        string[] Listed() => ((IEnumerable)all.Invoke(null, [api])!).Cast<object>().Select(x => (string)AccessTools.Property(x.GetType(), "Id").GetValue(x)!).ToArray();
        var fix = new KometPrimitiveSurvivalCompatibilitySystem();
        const string owner = "vsscpmod.komet-primitive-survival";
        bool Installed() => Harmony.GetPatchInfo(all)?.Owners.Contains(owner) == true;
        try
        {
            Check(Listed().Contains("primitivesurvival"), "unpatched Komet classifies real PS ZIP as shader mod");
            hold.Invoke(null, [api, logger]);
            Check(off.Contains("primitivesurvival"), "unpatched Komet holds back PS");
            StartPrimitive();
            Check(registrations == 0, "unpatched loader skips actual PS registrations");
            bool failed = false;
            try { createBehavior.Invoke(registry, [new Item(), "fishDepletionProbe"]); }
            catch (TargetInvocationException ex) { failed = ex.InnerException?.Message.Contains("forget to register") == true; }
            Check(failed, "reproduces original missing fishDepletionProbe crash");
            release.Invoke(null, null);
            Check(fix.ShouldLoad(EnumAppSide.Client) && !fix.ShouldLoad(EnumAppSide.Server), "client-only system");
            var kometSystem = (ModSystem)Activator.CreateInstance(komet.GetType("Komet.KometModSystem", true)!)!;
            Check(fix.ExecuteOrder() < kometSystem.ExecuteOrder(), "fix runs before actual Komet StartPre");
            foreach (string id in new[] { "komet", "primitivesurvival" })
            { enabled.Remove(id); fix.StartPre(api); Check(!Installed(), "no patch without " + id); enabled.Add(id); }
            side = EnumAppSide.Server; fix.StartPre(api); Check(!Installed(), "server StartPre is unchanged"); side = EnumAppSide.Client;
            km.Info.Version = "3.0.0-pre.4"; fix.StartPre(api); Check(!Installed(), "unknown Komet version is untouched"); km.Info.Version = "3.0.0-pre.3";
            fix.StartPre(api);
            Check(Installed(), "installs against actual Komet 3.0.0-pre.3");
            fix.StartPre(api);
            Check(Harmony.GetPatchInfo(all)!.Postfixes.Count(p => p.owner == owner) == 1, "repeated StartPre does not duplicate patch");
            Check(!Listed().Contains("primitivesurvival") && Listed().Contains("other-shader"), "filters only PS from cached scan and UI list");
            hold.Invoke(null, [api, logger]);
            Check(!off.Contains("primitivesurvival") && off.Contains("other-shader"), "PS loads while other shaders retain holdback behavior");
            StartPrimitive();
            Check(registrations == 1, "actual PS Start registers collectible behavior exactly once");
            object behavior = createBehavior.Invoke(registry, [new Item(), "fishDepletionProbe"])!;
            Check(behavior.GetType() == primitive.GetType("PrimitiveSurvival.ModSystem.fishDepletionProbe"), "real game registry constructs original PS behavior");
            hold.Invoke(null, [api, logger]);
            Check(!off.Contains("primitivesurvival"), "repeated holdback still preserves PS");
            fix.Dispose(); fix.Dispose();
            Check(!Installed() && Listed().Contains("primitivesurvival"), "dispose restores original scan results without mutating cache");
            release.Invoke(null, null);
            fix.StartPre(api); hold.Invoke(null, [api, logger]);
            Check(Installed() && !off.Contains("primitivesurvival"), "next session reinstalls protection");
            Check(errors.Count == 0, "no warnings or errors: " + string.Join("; ", errors));
            Console.WriteLine($"Komet / Primitive Survival: {count} checks passed.");
        }
        finally
        {
            fix.Dispose(); release.Invoke(null, null);
            AccessTools.Field(state, "_saved").SetValue(null, null);
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }
}
