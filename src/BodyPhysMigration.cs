using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

/// <summary>One-time BodyPhys 2.16.8 nutrition import using SLSS's own release codec.</summary>
internal sealed class BodyPhysMigration : IDisposable
{
    private const string Owner = "vsscpmod.bodyphys-migration";
    internal const string StateKey = "vsslssmod:state";
    internal const string LastGoodKey = "vsslssmod:lastgood";
    internal const string BackupKey = "vsscpmod.bodyphys-backup-v1";
    internal const string ReceiptKey = "vsscpmod.bodyphys-migration-v1";
    private static readonly (string Old, string New)[] Nutrients = [
        ("microIronZinc", "iron"), ("microVitaminA", "a"), ("microVitaminC", "c"),
        ("microCalcium", "calcium"), ("microBComplex", "bcomplex")
    ];
    private static BodyPhysMigration? current;
    private readonly Harmony harmony = new(Owner);
    private readonly ILogger logger;
    private readonly string worldId, backupDirectory;
    private readonly Type saveType;
    private readonly MethodInfo encode, decode, createBody, createFramework, nutrientIndex;
    private readonly object profile;
    private readonly HashSet<string> reportedFailures = [];
    private FieldInfo sessions = null!, disposed = null!, startupFault = null!;

    // All physiology model types remain optional: no compile-time reference or duplicate serializer.
    internal BodyPhysMigration(Type coordinator, string worldId, string backupDirectory, ILogger logger)
    {
        this.worldId = worldId;
        this.backupDirectory = backupDirectory;
        this.logger = logger;
        var application = Assembly.Load(coordinator.Assembly.GetReferencedAssemblies().Single(n => n.Name == "vsslssmod.Application"));
        var core = Assembly.Load(application.GetReferencedAssemblies().Single(n => n.Name == "vsslssmod.Core"));
        saveType = application.GetType("Seraph.Application.ShadowSave", true)!;
        Type codec = application.GetType("Seraph.Application.ShadowSaveCodec", true)!;
        encode = AccessTools.Method(codec, "Encode", [saveType]) ?? throw new MissingMethodException("ShadowSaveCodec.Encode");
        decode = AccessTools.Method(codec, "Decode", [typeof(byte[]), typeof(string), typeof(string)]) ?? throw new MissingMethodException("ShadowSaveCodec.Decode");
        Type bodyProfile = core.GetType("Seraph.Core.State.BodyProfile", true)!;
        profile = bodyProfile.GetProperty("Default")!.GetValue(null)!;
        createBody = AccessTools.Method(core.GetType("Seraph.Core.State.BodyState", true)!, "Create", [bodyProfile]);
        createFramework = AccessTools.Method(core.GetType("Seraph.Core.Framework.FrameworkState", true)!, "Create", [typeof(string)]);
        nutrientIndex = AccessTools.Method(core.GetType("Seraph.Core.Framework.Nutrients", true)!, "IndexOf", [typeof(string)]);
        // Fail installation before touching players if the release model contract has changed.
        var probe = NewSave("migration-contract-check", new TreeAttribute(), out _);
        Validate((byte[])encode.Invoke(null, [probe])!, "migration-contract-check");
    }

    internal static BodyPhysMigration? TryCreate(ICoreServerAPI api)
    {
        if (!api.ModLoader.IsModEnabled("vsslssmod") || api.ModLoader.IsModEnabled("bodyphys")) return null;
        BodyPhysMigration? fix = null;
        try
        {
            if (api.ModLoader.GetMod("vsslssmod")?.Info.Version != "1.0.0")
                throw new NotSupportedException("BodyPhys migration requires SLSS release 1.0.0.");
            Type coordinator = AccessTools.TypeByName("Seraph.Server.ServerCoordinator") ?? throw new TypeLoadException("Seraph.Server.ServerCoordinator");
            // Resolve the world identifier at login, not during StartServerSide (before SaveGameLoaded).
            fix = new BodyPhysMigration(coordinator, "contract-check", Path.Combine(GamePaths.DataPath, "ModData", "vsscpmod-bodyphys-backups"), api.Logger);
            fix.sessions = AccessTools.Field(coordinator, "sessions") ?? throw new MissingFieldException("sessions");
            fix.disposed = AccessTools.Field(coordinator, "disposed") ?? throw new MissingFieldException("disposed");
            fix.startupFault = AccessTools.Field(coordinator, "startupFault") ?? throw new MissingFieldException("startupFault");
            MethodInfo ready = AccessTools.Method(coordinator, "OnReady", [typeof(IServerPlayer)]) ?? throw new MissingMethodException("OnReady");
            current?.Dispose(); current = fix;
            fix.harmony.Patch(ready, prefix: new HarmonyMethod(typeof(BodyPhysMigration), nameof(BeforeReady)));
            api.Logger.Notification("[vsscpmod] BodyPhys five-nutrient migration enabled; legacy player data is backed up before removal.");
            return fix;
        }
        catch (Exception ex)
        {
            fix?.Dispose();
            api.Logger.Warning("[vsscpmod] BodyPhys migration unavailable; old data retained: {0}", ex.GetBaseException().Message);
            return null;
        }
    }

    private static bool BeforeReady(object __instance, IServerPlayer __0, ICoreServerAPI ___api)
    {
        if (current is not { } fix || (bool)fix.disposed.GetValue(__instance)!
            || fix.startupFault.GetValue(__instance) != null || ((IDictionary)fix.sessions.GetValue(__instance)!).Contains(__0.PlayerUID)) return true;
        try
        {
            fix.Migrate(__0, ___api.WorldManager.SaveGame.SavegameIdentifier);
            fix.reportedFailures.Remove(__0.PlayerUID);
            return true;
        }
        catch (Exception ex)
        {
            // Do not let a failed import create a default SLSS save over the intended migration.
            if (fix.reportedFailures.Add(__0.PlayerUID))
                fix.logger.Error("[vsscpmod] BodyPhys migration deferred for {0}; source retained: {1}", __0.PlayerUID, ex.GetBaseException().Message);
            return false;
        }
    }

    internal void Migrate(IServerPlayer player, string? actualWorld = null)
    {
        var attributes = player.Entity?.WatchedAttributes;
        if (attributes?.GetTreeAttribute("bodyPhys") is not { } legacy) return;
        string identity = actualWorld ?? worldId;
        if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(player.PlayerUID)) throw new InvalidDataException("Missing world/player identity.");
        var primary = player.GetModdata(StateKey);
        var lastGood = player.GetModdata(LastGoodKey);
        if (primary != null) Validate(primary, player.PlayerUID, identity);
        if (lastGood != null) Validate(lastGood, player.PlayerUID, identity);
        bool existing = primary != null || lastGood != null;
        // Receipt without a target is not permission to replenish a second time.
        if (!existing && player.GetModdata(ReceiptKey) != null) throw new InvalidDataException("Migration receipt exists but target state is missing.");

        var snapshot = new TreeAttribute();
        snapshot["bodyPhys"] = legacy.Clone();
        var mount = attributes.GetTreeAttribute("mountedOn");
        bool oldMount = mount?.GetString("className") is "latrine" or "washtub";
        if (oldMount) snapshot["mountedOn"] = mount!.Clone();
        byte[] original = snapshot.ToBytes();
        string hash = Convert.ToHexString(SHA256.HashData(original));
        string directory = Path.Combine(backupDirectory, Hash(identity), Hash(player.PlayerUID));
        WriteBackup(Path.Combine(directory, hash + ".bin"), original);
        var mapped = ReadNutrients(legacy);
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, WorldId = identity, PlayerUid = player.PlayerUID,
            SourceSha256 = hash, ExistingTargetPreserved = existing, Nutrients = existing ? new Dictionary<string, double>() : mapped });
        WriteBackup(Path.Combine(directory, hash + (existing ? ".preserve-plan.json" : ".import-plan.json")), receipt);
        // Keep a portable original inside the save as well as the durable server-side file.
        if (player.GetModdata(BackupKey) == null)
        {
            player.SetModdata(BackupKey, original);
            if (player.GetModdata(BackupKey)?.SequenceEqual(original) != true) throw new IOException("Player backup readback differs.");
        }
        if (!existing)
        {
            var save = NewSave(player.PlayerUID, legacy, out _, identity);
            byte[] bytes = (byte[])encode.Invoke(null, [save])!;
            Validate(bytes, player.PlayerUID, identity);
            player.SetModdata(StateKey, bytes);
            byte[] stored = player.GetModdata(StateKey) ?? throw new IOException("Target state was not stored.");
            if (!stored.SequenceEqual(bytes)) throw new IOException("Target state readback differs.");
            Validate(stored, player.PlayerUID, identity);
        }
        player.SetModdata(ReceiptKey, receipt);
        if (player.GetModdata(ReceiptKey)?.SequenceEqual(receipt) != true) throw new IOException("Migration receipt was not stored.");
        attributes.RemoveAttribute("bodyPhys"); attributes.MarkPathDirty("bodyPhys");
        if (oldMount && player.Entity!.MountedOn == null)
        {
            attributes.RemoveAttribute("mountedOn"); attributes.MarkPathDirty("mountedOn");
        }
        logger.Notification("[vsscpmod] BodyPhys player {0}: {1}; old bodyPhys tree archived and cleared. Backup: {2}",
            player.PlayerUID, existing ? "existing SLSS state preserved" : $"{mapped.Count} nutrient reserves imported", directory);
    }

    internal static Dictionary<string, double> ReadNutrients(ITreeAttribute tree)
    {
        var result = new Dictionary<string, double>();
        foreach (var (old, target) in Nutrients)
        {
            // BodyPhys stores displayed percentage points, not its nonlinear internal gain accumulator.
            double? value = tree[old] switch { FloatAttribute f => f.value, DoubleAttribute d => d.value, IntAttribute i => i.value, _ => null };
            if (value is { } number && double.IsFinite(number) && number is >= 0 and <= 100) result.Add(target, number);
        }
        return result;
    }

    private object NewSave(string uid, ITreeAttribute legacy, out Dictionary<string, double> mapped, string? actualWorld = null)
    {
        string identity = actualWorld ?? worldId;
        object save = Activator.CreateInstance(saveType)!;
        object body = createBody.Invoke(null, [profile])!;
        object framework = createFramework.Invoke(null, [identity + ":" + uid])!;
        object nutrition = Get(framework, "Nutrition");
        var reserves = (double[])Get(nutrition, "Reserve");
        mapped = ReadNutrients(legacy);
        foreach (var pair in mapped)
        {
            int index = (int)nutrientIndex.Invoke(null, [pair.Key])!;
            if (index < 0 || index >= reserves.Length) throw new InvalidDataException("Unsupported nutrient layout.");
            reserves[index] = pair.Value;
        }
        Set(save, "WorldId", identity); Set(save, "PlayerUid", uid); Set(save, "SavedAtUtc", DateTime.UtcNow);
        Set(save, "Profile", profile); Set(save, "State", body); Set(save, "Framework", framework);
        return save;
    }

    private void Validate(byte[] bytes, string uid, string? actualWorld = null)
    {
        object result = decode.Invoke(null, [bytes, actualWorld ?? worldId, uid])!;
        if (!(bool)Get(result, "Success")) throw new InvalidDataException("SLSS rejected state: " + Get(result, "Error"));
    }

    private static object Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static void Set(object value, string name, object data) => value.GetType().GetProperty(name)!.SetValue(value, data);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void WriteBackup(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new IOException("Backup verification failed: " + path);
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        harmony.UnpatchAll(Owner); current = null;
    }
}
