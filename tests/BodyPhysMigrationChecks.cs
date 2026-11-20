using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using SeraphCivilizationPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

static class BodyPhysMigrationChecks
{
    private sealed class TestMod : Mod { }
    private static bool WithoutGameHost() => false;
    private static readonly Dictionary<object, System.Func<string, bool>> WriteFailures = [];
    private static void FailWrite(object __instance, string __0)
    {
        if (WriteFailures.TryGetValue(__instance, out var fail) && fail(__0)) throw new IOException("injected persistence failure");
    }

    public static void Run(string root, string game, string slssZip)
    {
        string inputs = Path.Combine(root, "tests/dependencies/bodyphys-migration");
        string output = Path.Combine(root, "artifacts/bodyphys-migration-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(output);
        using (var zip = ZipFile.OpenRead(slssZip))
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".dll")))
                entry.ExtractToFile(Path.Combine(inputs, entry.Name), true);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string path = Path.Combine(inputs, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        var slss = Assembly.LoadFrom(Path.Combine(inputs, "vsslssmod.dll"));
        var library = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"));
        Type nativePlayerType = library.GetType("Vintagestory.Server.ServerPlayer", true)!;
        Type nativeDataType = library.GetType("Vintagestory.Server.ServerWorldPlayerData", true)!;
        var failurePatch = new Harmony("vsscpmod.tests.bodyphys-io");
        failurePatch.Patch(AccessTools.Method(nativeDataType, "SetModdata", [typeof(string), typeof(byte[])]),
            prefix: new HarmonyMethod(typeof(BodyPhysMigrationChecks), nameof(FailWrite)));
        Type coordinator = slss.GetType("Seraph.Server.ServerCoordinator", true)!;
        Type migration = typeof(SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.BodyPhysMigration", true)!;
        Type codec = Assembly.LoadFrom(Path.Combine(inputs, "vsslssmod.Application.dll")).GetType("Seraph.Application.ShadowSaveCodec", true)!;
        Type nutrients = Assembly.LoadFrom(Path.Combine(inputs, "vsslssmod.Core.dll")).GetType("Seraph.Core.Framework.Nutrients", true)!;
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => {
            string message = a is [string format, object[] values] ? string.Format(format, values) : string.Join(" ", a ?? []);
            messages.Add(m.Name + " " + message); return null;
        });
        var fix = Activator.CreateInstance(migration, BindingFlags.Instance | BindingFlags.NonPublic, null, [coordinator, "test-world", output, logger], null)!;
        var migrate = AccessTools.Method(migration, "Migrate");
        const string stateKey = "vsslssmod:state", lastGood = "vsslssmod:lastgood", backupKey = "vsscpmod.bodyphys-backup-v1", receiptKey = "vsscpmod.bodyphys-migration-v1";
        int count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
        TreeAttribute Old()
        {
            var t = new TreeAttribute();
            t.SetFloat("microIronZinc", 31); t.SetFloat("microVitaminA", 0); t.SetFloat("microVitaminC", 100);
            t.SetFloat("microCalcium", 62.5f); t.SetFloat("microBComplex", 17);
            t.SetFloat("hydration", 2); t.SetFloat("blood", 1); t.SetFloat("glycogen", 0);
            t.SetString("unknown-legacy-field", "preserve in backup"); return t;
        }
        (IServerPlayer Player, EntityPlayer Entity, Dictionary<string, byte[]> Data) Player(string uid, TreeAttribute? old, System.Func<string, bool>? fail = null)
        {
            var entity = new EntityPlayer();
            if (old != null) entity.WatchedAttributes["bodyPhys"] = old;
            entity.WatchedAttributes.SetFloat("intoxication", .4f);
            entity.WatchedAttributes.SetFloat("slowtox:newToxins", .2f);
            var data = new Dictionary<string, byte[]>();
            object nativeData = Activator.CreateInstance(nativeDataType)!;
            AccessTools.Field(nativeDataType, "PlayerUID").SetValue(nativeData, uid);
            AccessTools.Property(nativeDataType, "EntityPlayer").SetValue(nativeData, entity);
            AccessTools.Field(nativeDataType, "ModData").SetValue(nativeData, data);
            var p = (IServerPlayer)RuntimeHelpers.GetUninitializedObject(nativePlayerType);
            AccessTools.Field(nativePlayerType, "worlddata").SetValue(p, nativeData);
            if (fail != null) WriteFailures.Add(nativeData, fail);
            return (p, entity, data);
        }
        object Decode(byte[] data, string uid)
        {
            object result = AccessTools.Method(codec, "Decode").Invoke(null, [data, "test-world", uid])!;
            Check((bool)result.GetType().GetProperty("Success")!.GetValue(result)!, "native SLSS codec accepts migrated save for " + uid);
            return result.GetType().GetProperty("Save")!.GetValue(result)!;
        }
        object Get(object o, string key) => o.GetType().GetProperty(key)!.GetValue(o)!;
        double Nutrient(object save, string key) => ((double[])Get(Get(Get(save, "Framework"), "Nutrition"), "Reserve"))[(int)AccessTools.Method(nutrients, "IndexOf").Invoke(null, [key])!];
        void Run(IServerPlayer p) => migrate.Invoke(fix, [p, null]);
        bool Fails(IServerPlayer p) { try { Run(p); return false; } catch (TargetInvocationException) { return true; } }
        try
        {
            var a = Player("first", Old());
            var mount = new TreeAttribute(); mount.SetString("className", "washtub"); a.Entity.WatchedAttributes["mountedOn"] = mount;
            byte[] oldBytes = ((TreeAttribute)a.Entity.WatchedAttributes["bodyPhys"]).ToBytes();
            Run(a.Player);
            object saved = Decode(a.Data[stateKey], "first");
            Check(new[] { ("iron", 31d), ("a", 0d), ("c", 100d), ("calcium", 62.5d), ("bcomplex", 17d) }.All(x => Nutrient(saved, x.Item1) == x.Item2), "five old percentage reserves retain exact values including zero");
            Check((double)Get(Get(saved, "State"), "BodyWaterL") > 2 && (double)Get(Get(saved, "State"), "GlycogenEnergyKJ") > 0, "unrelated old physiology uses native defaults");
            Check(!a.Entity.WatchedAttributes.HasAttribute("bodyPhys") && !a.Entity.WatchedAttributes.HasAttribute("mountedOn"), "legacy tree and obsolete bath mount cleared after persistence");
            Check(a.Entity.WatchedAttributes.GetFloat("intoxication") == .4f && a.Entity.WatchedAttributes.GetFloat("slowtox:newToxins") == .2f, "SlowTox state preserved");
            var restored = new TreeAttribute(); restored.FromBytes(a.Data[backupKey]);
            Check(((TreeAttribute)restored["bodyPhys"]).ToBytes().SequenceEqual(oldBytes) && restored.GetTreeAttribute("mountedOn").GetString("className") == "washtub", "portable backup retains complete tree and legacy mount");
            Check(Directory.GetFiles(output, "*.bin", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(a.Data[backupKey])), "external backup matches portable backup bytes");
            byte[] firstSave = a.Data[stateKey].ToArray(); Run(a.Player);
            Check(a.Data[stateKey].SequenceEqual(firstSave), "second login does not replenish reserves");
            a.Entity.WatchedAttributes["bodyPhys"] = Old(); Run(a.Player);
            Check(a.Data[stateKey].SequenceEqual(firstSave) && !a.Entity.WatchedAttributes.HasAttribute("bodyPhys"), "valid existing target wins over restored legacy tree");

            var badTree = Old(); badTree.SetFloat("microIronZinc", float.NaN); badTree.SetFloat("microVitaminA", -5); badTree.SetFloat("microVitaminC", 101);
            badTree.RemoveAttribute("microCalcium"); badTree.SetString("microBComplex", "broken");
            var b = Player("invalid-fields", badTree); Run(b.Player);
            object badSave = Decode(b.Data[stateKey], "invalid-fields");
            Check(new[] { "iron", "a", "c", "calcium", "bcomplex" }.All(k => Nutrient(badSave, k) == 50), "invalid or absent values keep new-system defaults");

            var c = Player("corrupt-new", Old()); c.Data[stateKey] = [1, 2, 3];
            Check(Fails(c.Player) && c.Entity.WatchedAttributes.HasAttribute("bodyPhys") && c.Data[stateKey].SequenceEqual(new byte[] { 1, 2, 3 }), "invalid existing SLSS state is never overwritten or cleared");
            a.Entity.WatchedAttributes["bodyPhys"] = Old(); a.Data[lastGood] = [4];
            Check(Fails(a.Player) && a.Entity.WatchedAttributes.HasAttribute("bodyPhys"), "invalid lastgood also blocks cleanup"); a.Data.Remove(lastGood);
            a.Data[lastGood] = a.Data[stateKey]; a.Data.Remove(stateKey); Run(a.Player);
            Check(!a.Data.ContainsKey(stateKey) && a.Data[lastGood].SequenceEqual(firstSave), "valid lastgood alone is preserved without new nutrition");

            var d = Player("write-fails", Old(), key => key == stateKey);
            Check(Fails(d.Player) && d.Entity.WatchedAttributes.HasAttribute("bodyPhys") && d.Data.ContainsKey(backupKey) && !d.Data.ContainsKey(receiptKey), "target write failure preserves old state and backup");
            bool receiptFail = true;
            var e = Player("receipt-fails", Old(), key => key == receiptKey && receiptFail);
            Check(Fails(e.Player) && e.Entity.WatchedAttributes.HasAttribute("bodyPhys") && e.Data.ContainsKey(stateKey), "interruption after target write retains source");
            byte[] interrupted = e.Data[stateKey].ToArray(); receiptFail = false; Run(e.Player);
            Check(e.Data[stateKey].SequenceEqual(interrupted) && !e.Entity.WatchedAttributes.HasAttribute("bodyPhys"), "retry finishes cleanup without applying reserves twice");
            var missingTarget = Player("receipt-only", Old()); missingTarget.Data[receiptKey] = [1];
            Check(Fails(missingTarget.Player) && missingTarget.Entity.WatchedAttributes.HasAttribute("bodyPhys"), "receipt without target fails closed");
            var fresh = Player("fresh", null); Run(fresh.Player);
            Check(fresh.Data.Count == 0, "players without BodyPhys are untouched");
            var horse = Player("horse", Old()); var horseMount = new TreeAttribute(); horseMount.SetString("className", "entity"); horse.Entity.WatchedAttributes["mountedOn"] = horseMount;
            Run(horse.Player); Check(horse.Entity.WatchedAttributes.GetTreeAttribute("mountedOn").GetString("className") == "entity", "unrelated mounts are untouched");
            string blockedRoot = Path.Combine(output, "file-not-directory"); File.WriteAllText(blockedRoot, "block");
            object blocked = Activator.CreateInstance(migration, BindingFlags.Instance | BindingFlags.NonPublic, null, [coordinator, "test-world", blockedRoot, logger], null)!;
            var f = Player("backup-fails", Old()); bool failed = false;
            try { migrate.Invoke(blocked, [f.Player, null]); } catch (TargetInvocationException) { failed = true; }
            Check(failed && f.Data.Count == 0 && f.Entity.WatchedAttributes.HasAttribute("bodyPhys"), "backup I/O failure prevents all state mutation");

            // Verify the actual optional Harmony entry point without starting a game world.
            bool bodyphys = false, slssEnabled = false;
            var mod = new TestMod(); AccessTools.Property(typeof(Mod), "Info").SetValue(mod, new ModInfo { ModID = "vsslssmod", Version = "1.0.0" });
            var loader = Proxy.Create<IModLoader>((m, args) => m.Name switch { "IsModEnabled" => (string)args![0]! == "bodyphys" ? bodyphys : slssEnabled, "GetMod" => mod, _ => null });
            object DynamicProxy(Type type, System.Func<MethodInfo, object?[]?, object?> callback)
                => typeof(Proxy).GetMethod("Create", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)!.MakeGenericMethod(type).Invoke(null, [callback])!;
            Type managerType = typeof(ICoreServerAPI).GetProperty("WorldManager")!.PropertyType;
            Type saveGameType = managerType.GetProperty("SaveGame")!.PropertyType;
            object saveGame = DynamicProxy(saveGameType, (m, _) => m.Name == "get_SavegameIdentifier" ? "test-world" : null);
            object manager = DynamicProxy(managerType, (m, _) => m.Name == "get_SaveGame" ? saveGame : null);
            var api = Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch { "get_ModLoader" => loader, "get_Logger" => logger, "get_WorldManager" => manager, _ => null });
            var create = AccessTools.Method(migration, "TryCreate");
            Check(create.Invoke(null, [api]) == null, "missing SLSS disables migration");
            slssEnabled = bodyphys = true;
            Check(create.Invoke(null, [api]) == null, "live BodyPhys prevents migration"); bodyphys = false;
            mod.Info.Version = "2.0.0";
            Check(create.Invoke(null, [api]) == null, "unknown SLSS format version is not patched"); mod.Info.Version = "1.0.0";
            IDisposable? installed = (IDisposable?)create.Invoke(null, [api]);
            Check(installed != null, "migration installs against deployed SLSS 1.0.0");
            AccessTools.Field(migration, "backupDirectory").SetValue(installed, output);
            var ready = AccessTools.Method(coordinator, "OnReady");
            const string owner = "vsscpmod.bodyphys-migration";
            Check(Harmony.GetPatchInfo(ready)?.Owners.Contains(owner) == true, "real login method is patched before physiology initializes");
            var hostPatch = new Harmony("vsscpmod.tests.bodyphys-host");
            try
            {
                hostPatch.Patch(ready, prefix: new HarmonyMethod(typeof(BodyPhysMigrationChecks), nameof(WithoutGameHost)) { priority = Priority.Last });
                object server = RuntimeHelpers.GetUninitializedObject(coordinator);
                AccessTools.Field(coordinator, "api").SetValue(server, api);
                AccessTools.Field(coordinator, "disposed").SetValue(server, true);
                ready.Invoke(server, [fresh.Player]);
                Check(fresh.Data.Count == 0, "disposed coordinator passes through without touching player");
                AccessTools.Field(coordinator, "disposed").SetValue(server, false);
                FieldInfo sessionsField = AccessTools.Field(coordinator, "sessions");
                sessionsField.SetValue(server, Activator.CreateInstance(sessionsField.FieldType));
                var login = Player("actual-hook", Old());
                ready.Invoke(server, [login.Player]);
                Check(!login.Entity.WatchedAttributes.HasAttribute("bodyPhys") && Nutrient(Decode(login.Data[stateKey], "actual-hook"), "iron") == 31,
                    "actual OnReady prefix resolves current world and imports before the host");
                var badLogin = Player("bad-hook", Old()); badLogin.Data[stateKey] = [0];
                ready.Invoke(server, [badLogin.Player]); ready.Invoke(server, [badLogin.Player]);
                Check(badLogin.Entity.WatchedAttributes.HasAttribute("bodyPhys") && messages.Count(m => m.Contains("migration deferred for bad-hook")) == 1,
                    "hook preserves rejected state and reports repeated failures once");
            }
            finally { hostPatch.UnpatchAll(hostPatch.Id); installed!.Dispose(); }
            Check(Harmony.GetPatchInfo(ready)?.Owners.Contains(owner) != true, "dispose removes login patch");

            // Exercise the native missing-content paths; the migration supplies no replacement assets.
            var world = Proxy.Create<IWorldAccessor>((m, _) => m.Name == "get_Logger" ? logger : null);
            foreach (string code in new[] { "ash", "humwaste", "pastesoap", "seasalt", "lyeportion" })
            {
                var stack = new ItemStack { Class = EnumItemClass.Item, Id = 42, StackSize = 1 };
                Check(!stack.FixMapping([], new() { [42] = new AssetLocation("bodyphys", code) }, world), "native missing-item mapping safely rejects bodyphys:" + code);
            }
            Type serverMain = library.GetType("Vintagestory.Server.ServerMain", true)!;
            FieldInfo registryField = AccessTools.Field(serverMain, "ClassRegistry"), loggerField = AccessTools.Field(serverMain, "Logger");
            object? oldRegistry = registryField.GetValue(null), oldLogger = loggerField.GetValue(null);
            try
            {
                object registry = Activator.CreateInstance(registryField.FieldType)!;
                registryField.SetValue(null, registry);
                object silentLogger = RuntimeHelpers.GetUninitializedObject(library.GetType("Vintagestory.Server.ServerLogger", true)!);
                AccessTools.Field(loggerField.FieldType, "disposed").SetValue(silentLogger, true);
                loggerField.SetValue(null, silentLogger);
                Type chunkType = library.GetType("Vintagestory.Server.ServerChunk", true)!;
                object chunk = RuntimeHelpers.GetUninitializedObject(chunkType);
                AccessTools.Field(chunkType, "packUnpackLock").SetValue(chunk, new object());
                FieldInfo entities = AccessTools.Field(chunkType, "BlockEntities");
                entities.SetValue(chunk, Activator.CreateInstance(entities.FieldType));
                var serialized = new List<byte[]>();
                foreach (string name in new[] { "BlockEntityWellHead", "BlockEntityWaterFilter", "BlockEntityLatrine", "BlockEntityManureHeap", "BlockEntityChimneyExhaust", "BlockEntityWashVessel" })
                {
                    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                    writer.Write(name); var tree = new TreeAttribute(); tree.SetString("blockCode", "bodyphys:wellhead"); tree.ToBytes(writer);
                    writer.Flush(); serialized.Add(stream.ToArray());
                }
                AccessTools.Field(chunkType, "BlockEntitiesSerialized").SetValue(chunk, serialized);
                AccessTools.Method(chunkType, "AfterDeserialization").Invoke(chunk, [world]);
                Check(((IDictionary)entities.GetValue(chunk)!).Count == 0 && AccessTools.Field(chunkType, "BlockEntitiesSerialized").GetValue(chunk) == null,
                    "native chunk loader discards all six unknown BodyPhys block entities without throwing");
                foreach (string seat in new[] { "latrine", "washtub" })
                {
                    var tree = new TreeAttribute(); tree.SetString("className", seat);
                    Check(AccessTools.Method(registryField.FieldType, "GetMountable").Invoke(registry, [world, tree]) == null, "native registry safely rejects removed " + seat + " mount");
                }
            }
            finally { registryField.SetValue(null, oldRegistry); loggerField.SetValue(null, oldLogger); }
            Console.WriteLine($"BodyPhys migration: {count} checks passed. Backups: {output}");
        }
        finally { ((IDisposable)fix).Dispose(); failurePatch.UnpatchAll(failurePatch.Id); WriteFailures.Clear(); AssemblyLoadContext.Default.Resolving -= Resolve; }
    }
}
