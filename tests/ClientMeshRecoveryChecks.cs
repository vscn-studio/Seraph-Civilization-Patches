using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

static class ClientMeshRecoveryChecks
{
    private sealed class ProbeMesh : MeshRef { public override bool Initialized => true; }
    private static MeshData? generated;
    private static int generations;
    private static bool Generate(ref MeshData? __result) { generations++; __result = generated; return false; }
    private static bool SkipRenderHost() => false;
    private static MultiTextureMeshRef Mesh() => new([new ProbeMesh()], [1]);

    public static void Run(string dll, string? deadDll)
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
        }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(dll));
        var behaviorType = assembly.GetType("Toolsmith.Client.Behaviors.ModularPartRenderingFromAttributes", true)!;
        var configField = AccessTools.Field(assembly.GetType("Toolsmith.ToolsmithModSystem"), "ClientConfig");
        configField.SetValue(null, Activator.CreateInstance(configField.FieldType));
        var before = AccessTools.DeclaredMethod(behaviorType, "OnBeforeRender");
        var gen = AccessTools.DeclaredMethod(behaviorType, "GenMesh", [typeof(ItemSlot), typeof(ITextureAtlasAPI), typeof(Vintagestory.API.MathTools.BlockPos)]);
        var host = new Harmony("vsscpmod.tests.mesh-host");
        host.Patch(gen, prefix: new HarmonyMethod(typeof(ClientMeshRecoveryChecks), nameof(Generate)));
        var cache = new Dictionary<int, MultiTextureMeshRef>();
        var objectCache = new Dictionary<string, object> { ["ToolsmithModularPartRenderingMeshRefs"] = cache };
        var logger = Proxy.Create<ILogger>((_, _) => null);
        var enabled = new HashSet<string> { "toolsmith" };
        MethodInfo? deadHide = null;
        if (deadDll != null)
        {
            var dead = Assembly.LoadFrom(Path.GetFullPath(deadDll));
            deadHide = AccessTools.DeclaredMethod(dead.GetType("Dead.Patches.HiddenBodyRenderPatch", true), "Postfix");
            // Register Dead's actual postfix before our compatibility, matching game startup order.
            // Only skip the engine's lighting/world host; call the real Harmony render wrapper.
            host.Patch(AccessTools.Method(typeof(EntityShapeRenderer), "BeforeRender"),
                prefix: new HarmonyMethod(typeof(ClientMeshRecoveryChecks), nameof(SkipRenderHost)),
                postfix: new HarmonyMethod(deadHide));
            enabled.Add("dead");
        }
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" ? enabled.Contains((string)a![0]!) : null);
        MultiTextureMeshRef uploaded = Mesh();
        var render = Proxy.Create<IRenderAPI>((m, _) => m.Name == "UploadMultiTextureMesh" ? uploaded : null);
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_ObjectCache" => objectCache, "get_Logger" => logger,
            "get_Render" => render, _ => null
        });
        var item = new Item { Code = new("game:axe-iron"), ItemId = 123 };
        var behavior = (CollectibleBehavior)Activator.CreateInstance(behaviorType, [item])!;
        AccessTools.Field(behaviorType, "api").SetValue(behavior, api);
        AccessTools.Field(behaviorType, "capi").SetValue(behavior, api);
        var fixType = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.ClientMeshRecovery", true)!;
        IDisposable Install() => (IDisposable)fixType.GetMethod("TryCreate")!.Invoke(null, [api])!;
        ItemStack Stack(int id = 0)
        {
            var s = new ItemStack(item);
            s.TempAttributes.SetInt("toolsmithMeshrefID", id);
            s.Attributes.SetString("preserved", "value");
            return s;
        }
        ItemRenderInfo Draw(ItemStack stack, MultiTextureMeshRef model)
        {
            var info = new ItemRenderInfo { ModelRef = model };
            before.Invoke(behavior, [api, stack, EnumItemRenderTarget.Gui, info]);
            return info;
        }
        IDisposable? owner = null, replacement = null;
        try
        {
            var defaultMesh = Mesh();
            cache[1] = Mesh(); cache[1].Dispose();
            var stale = Stack(1);
            Check(ReferenceEquals(Draw(stale, defaultMesh).ModelRef, cache[1]), "real Toolsmith reproduces disposed cache reuse before patch");
            if (deadHide != null) CheckDead(deadHide, api, false, Check);
            owner = Install();
            Check(owner != null && Harmony.GetPatchInfo(before)?.Owners.Contains("vsscpmod.client-mesh-recovery.toolsmith") == true,
                "client compatibility patches real Toolsmith entry point");
            if (deadHide != null) CheckDead(deadHide, api, true, Check);
            generated = new MeshData { VerticesCount = 4, IndicesCount = 6 };
            Check(ReferenceEquals(Draw(stale, defaultMesh).ModelRef, uploaded) && stale.TempAttributes.GetInt("toolsmithMeshrefID") == 2,
                "disposed cache entry is rebuilt using a new ID");
            int calls = generations;
            Draw(stale, defaultMesh);
            Check(generations == calls, "healthy multipart cache is reused without regeneration");
            var missing = Stack(999);
            uploaded = Mesh();
            Check(ReferenceEquals(Draw(missing, defaultMesh).ModelRef, uploaded), "missing ID preserves valid generation path");
            generated = new MeshData();
            var empty = Stack();
            int oldCount = cache.Count;
            Check(ReferenceEquals(Draw(empty, defaultMesh).ModelRef, defaultMesh) && cache.Count == oldCount,
                "empty generation falls back without caching borrowed engine mesh");
            calls = generations;
            Draw(empty, defaultMesh);
            Check(generations == calls, "failed tool does not regenerate on every frame");
            Check(empty.Attributes.GetString("preserved") == "value" && !defaultMesh.Disposed,
                "persistent item data and default GPU mesh remain intact");
            generated = null;
            Check(ReferenceEquals(Draw(Stack(888), defaultMesh).ModelRef, defaultMesh), "null generation on cache miss retains original model");
            generated = new MeshData { VerticesCount = 4, IndicesCount = 6 };
            uploaded = Mesh(); uploaded.meshrefs[0].Dispose();
            Check(ReferenceEquals(Draw(Stack(), defaultMesh).ModelRef, defaultMesh) && uploaded.Disposed,
                "invalid upload falls back and releases its owned mesh");
            var snapshot = cache.ToArray(); cache.Clear();
            var survivor = Mesh(); cache[2] = survivor;
            calls = generations;
            Check(ReferenceEquals(Draw(Stack(), defaultMesh).ModelRef, defaultMesh) && ReferenceEquals(cache[2], survivor)
                && calls == generations, "sparse third-party cache cannot overwrite another tool's ID");
            cache.Clear(); foreach (var pair in snapshot) cache.Add(pair.Key, pair.Value);
            AccessTools.Field(configField.FieldType, "DisableMultiPartRendering").SetValue(configField.GetValue(null), true);
            calls = generations;
            Check(ReferenceEquals(Draw(Stack(), defaultMesh).ModelRef, defaultMesh) && calls == generations,
                "Toolsmith disable-multipart option remains respected");
            AccessTools.Field(configField.FieldType, "DisableMultiPartRendering").SetValue(configField.GetValue(null), false);
            CheckPlayers(fixType, api, Check);
            replacement = Install(); owner!.Dispose(); owner = null;
            Check(Harmony.GetPatchInfo(before)?.Owners.Contains("vsscpmod.client-mesh-recovery.toolsmith") == true, "replacement client retains patches");
            replacement.Dispose(); replacement = null;
            if (deadHide != null)
            {
                Check(Harmony.GetPatchInfo(deadHide)?.Owners.Contains("vsscpmod.client-mesh-recovery.dead") != true,
                    "last disposal removes Dead visibility compatibility");
                CheckDead(deadHide, api, false, Check);
            }
            Check(Harmony.GetPatchInfo(before)?.Owners.Contains("vsscpmod.client-mesh-recovery.toolsmith") != true
                && Harmony.GetPatchInfo(AccessTools.Method(typeof(EntityPlayerShapeRenderer), "BeforeRender"))?.Owners.Contains("vsscpmod.client-mesh-recovery") != true,
                "last disposal removes tool and player hooks");
            enabled.Clear(); owner = Install();
            Check(Harmony.GetPatchInfo(before)?.Owners.Contains("vsscpmod.client-mesh-recovery.toolsmith") != true,
                "Toolsmith absence leaves third-party code untouched");
            if (deadHide != null) Check(Harmony.GetPatchInfo(deadHide)?.Owners.Contains("vsscpmod.client-mesh-recovery.dead") != true,
                "Dead absence leaves third-party code untouched");
        }
        finally { owner?.Dispose(); replacement?.Dispose(); host.UnpatchAll(host.Id); configField.SetValue(null, null); }
    }

    private static void CheckDead(MethodInfo deadHide, ICoreClientAPI api, bool patched, Action<bool, string> check)
    {
        var renderer = (EntityShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityShapeRenderer));
        var entity = new EntityPlayer { Api = api, EntityId = 43 };
        renderer.entity = entity;
        var spectator = AccessTools.Field(typeof(EntityShapeRenderer), "isSpectator");
        bool Hide(bool alive, bool hidden, bool originallySpectator)
        {
            entity.Alive = alive;
            entity.WatchedAttributes.SetBool("deadBodyHidden", hidden);
            spectator.SetValue(renderer, originallySpectator);
            renderer.BeforeRender(0.016f);
            return (bool)spectator.GetValue(renderer)!;
        }
        if (!patched)
        {
            check(Hide(true, true, false), "real Dead hides a living player with stale corpse flag before patch / after disposal");
            return;
        }
        check(!Hide(true, true, false), "real Dead corpse rule no longer hides a living player");
        check(entity.WatchedAttributes.GetBool("deadBodyHidden"), "client fix leaves synchronized corpse flag untouched");
        check(Hide(true, true, true), "real spectator remains hidden even with stale corpse flag");
        check(Hide(false, true, false), "Dead continues hiding genuine dead bodies");
        check(!Hide(false, false, false), "revivable dead body without hidden flag remains visible");
        check(!Hide(true, false, false), "normal living player visibility remains unchanged");
        check(Hide(true, false, true), "normal spectator visibility remains unchanged");
        Hide(false, true, false);
        entity.Alive = true;
        spectator.SetValue(renderer, false); // Native engine recalculates game mode before Dead's postfix each frame.
        renderer.BeforeRender(0.016f);
        check(!(bool)spectator.GetValue(renderer)!, "death-to-revival transition stops applying Dead hiding");
    }

    private static void CheckPlayers(Type fixType, ICoreClientAPI api, Action<bool, string> check)
    {
        // Exercise the recovery hook with real renderer/entity state, without an OpenGL context.
        var renderer = (EntityPlayerShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayerShapeRenderer));
        var entity = new EntityPlayer { Api = api, EntityId = 42 };
        var animator = Proxy.Create<IAnimator>((_, _) => null);
        entity.TpAnimManager.Animator = animator;
        AccessTools.Field(typeof(EntityPlayerShapeRenderer), "entityPlayer").SetValue(renderer, entity);
        AccessTools.Field(typeof(EntityShapeRenderer), "loaded").SetValue(renderer, true);
        var fresh = AccessTools.Field(typeof(Entity), "shapeFresh");
        fresh.SetValue(entity, true);
        var hook = AccessTools.Method(fixType, "CheckPlayer");
        void Tick() => hook.Invoke(null, [renderer]);
        Tick(); check(entity.ShapeFresh, "initial missing player mesh receives a grace period");
        object table = AccessTools.Field(fixType, "players").GetValue(null)!;
        object state = table.GetType().GetMethod("GetOrCreateValue")!.Invoke(table, [renderer])!;
        void Set(string name, object value) => AccessTools.Field(state.GetType(), name).SetValue(state, value);
        void Ready() { Set("NextCheck", 0L); Set("MissingSince", Environment.TickCount64 - 20000); fresh.SetValue(entity, true); }
        AccessTools.Field(typeof(EntityShapeRenderer), "loaded").SetValue(renderer, false);
        renderer.TesselateShape(_ => { });
        check((long)AccessTools.Field(state.GetType(), "PendingSince").GetValue(state)! > 0,
            "real asynchronous tessellation entry is tracked by installed Harmony patch");
        AccessTools.Field(typeof(EntityShapeRenderer), "loaded").SetValue(renderer, true);
        Ready(); Set("PendingSince", Environment.TickCount64); Tick();
        check(entity.ShapeFresh, "pending asynchronous upload is allowed to complete");
        Set("PendingSince", 0L);
        for (int i = 0; i < 3; i++) { Ready(); Tick(); check(!entity.ShapeFresh, "missing player mesh requests rebuild " + (i + 1)); }
        Ready(); Tick(); check(entity.ShapeFresh, "persistent player failure stops after three retries");
        var good = Mesh();
        AccessTools.Field(typeof(EntityPlayerShapeRenderer), "thirdPersonMeshRef").SetValue(renderer, good);
        Ready(); Tick();
        check(entity.ShapeFresh && !good.Disposed && (int)AccessTools.Field(state.GetType(), "Attempts").GetValue(state)! == 0,
            "healthy player mesh is preserved and clears retry budget");
        good.Dispose(); Ready(); Tick(); check(!entity.ShapeFresh, "disposed player mesh can recover after previous success");
        AccessTools.Field(typeof(EntityPlayerShapeRenderer), "thirdPersonMeshRef").SetValue(renderer, Mesh());
        entity.TpAnimManager.Animator = null;
        Set("Attempts", 0); Ready(); Tick();
        check(!entity.ShapeFresh, "valid mesh with missing animator requests native rebuild instead of staying invisible");
        // Force a diagnostic interval and verify changes are recorded without depending on a real world.
        Set("FirstSeen", Environment.TickCount64 - 10000); Ready(); Tick();
        check((int)AccessTools.Field(state.GetType(), "Diagnostics").GetValue(state)! == 1,
            "player render state is recorded after loading grace period");
        Ready(); Tick();
        check((int)AccessTools.Field(state.GetType(), "Diagnostics").GetValue(state)! == 1,
            "unchanged render state does not flood the log");
    }
}
