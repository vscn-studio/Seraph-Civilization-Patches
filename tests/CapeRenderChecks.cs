using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

static class CapeRenderChecks
{
    private sealed class ProbeMesh : MeshRef { public override bool Initialized => true; }
    private static bool fail;
    private static int calls;
    private static bool Generate(ref MeshData __result)
    {
        calls++;
        if (fail) throw new InvalidOperationException("test cape generation failure");
        __result = new MeshData { VerticesCount = 4, IndicesCount = 6 };
        return false;
    }
    private static MultiTextureMeshRef Mesh() => new([new ProbeMesh()], [1]);

    public static void Run(string dll)
    {
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
        }
        var arl = Assembly.LoadFrom(Path.GetFullPath(dll));
        var gameLibrary = Assembly.Load("VintagestoryLib");
        var atlasType = gameLibrary.GetType("Vintagestory.Client.NoObf.TextureAtlasManager", true)!;
        var atlasTextures = AccessTools.Field(atlasType, "AtlasTextures");
        var regenMipMaps = AccessTools.DeclaredMethod(atlasType, "RegenMipMaps", [typeof(int)]);
        var atlasManager = RuntimeHelpers.GetUninitializedObject(atlasType);
        atlasTextures.SetValue(atlasManager, new List<LoadedTexture>());
        var clientType = gameLibrary.GetType("Vintagestory.Client.NoObf.ClientMain", true)!;
        var client = RuntimeHelpers.GetUninitializedObject(clientType);
        AccessTools.Field(atlasType, "game").SetValue(atlasManager, client);
        AccessTools.Field(clientType, "Platform").SetValue(client,
            RuntimeHelpers.GetUninitializedObject(gameLibrary.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows", true)!));
        bool OriginalAtlasCrash()
        {
            try { regenMipMaps.Invoke(atlasManager, [0]); return false; }
            catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { return true; }
        }
        Check(OriginalAtlasCrash(), "empty game atlas reproduces the logged mipmap index crash");
        var type = arl.GetType("AttributeRenderingLibrary.CollectibleBehaviorWearable", true)!;
        var parent = type.BaseType!;
        var draw = AccessTools.DeclaredMethod(parent, "OnBeforeRender");
        var gen = AccessTools.DeclaredMethod(parent, "genFullBodyMesh");
        var host = new Harmony("vsscpmod.tests.cape-host");
        host.Patch(gen, prefix: new HarmonyMethod(typeof(CapeRenderChecks), nameof(Generate)));
        var enabled = new HashSet<string> { "capes", "attributerenderinglibrary" };
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" ? enabled.Contains((string)a![0]!) : null);
        var logger = Proxy.Create<ILogger>((_, _) => null);
        var cache = new Dictionary<string, object>();
        var uploaded = Mesh();
        var renderer = Proxy.Create<IRenderAPI>((m, _) => m.Name == "UploadMultiTextureMesh" ? uploaded : null);
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_ObjectCache" => cache, "get_Logger" => logger,
            "get_Render" => renderer, _ => null
        });
        var fix = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.CapeRenderCompatibility", true)!;
        IDisposable? owner = null;
        try
        {
            owner = (IDisposable)fix.GetMethod("TryCreate")!.Invoke(null, [api])!;
            Check(Harmony.GetPatchInfo(draw)?.Owners.Contains("vsscpmod.cape-render") == true, "patch installed on real ARL wearable renderer");
            regenMipMaps.Invoke(atlasManager, [0]);
            regenMipMaps.Invoke(atlasManager, [-1]);
            Check(true, "stale and out-of-range atlas mipmap requests are skipped safely");
            var cape = new Item { Code = new AssetLocation("capes:cape") };
            var behavior = Activator.CreateInstance(type, [cape])!;
            var stack = new ItemStack(cape);
            stack.Attributes.GetOrAddTreeAttribute("types").SetString("layer1", "color_plain");
            stack.Attributes.SetFloat("condition", 0);
            var slot = new DummySlot(stack);
            var original = Mesh();
            ItemRenderInfo Draw(ItemStack s, ItemSlot sl)
            {
                var info = new ItemRenderInfo { ModelRef = original, InSlot = sl };
                object[] args = [api, s, EnumItemRenderTarget.Gui, info];
                draw.Invoke(behavior, args);
                return (ItemRenderInfo)args[3];
            }
            fail = true;
            Check(ReferenceEquals(Draw(stack, slot).ModelRef, original), "exception restores original cape mesh");
            int previous = calls;
            Check(ReferenceEquals(Draw(stack, slot).ModelRef, original) && calls == previous, "failed cape is throttled between retries");
            Check(stack.Attributes.GetTreeAttribute("types").GetString("layer1") == "color_plain" && stack.Attributes.GetFloat("condition") == 0,
                "recovery preserves saved cape attributes");
            owner.Dispose(); owner = (IDisposable)fix.GetMethod("TryCreate")!.Invoke(null, [api])!;
            fail = false;
            Check(ReferenceEquals(Draw(stack, slot).ModelRef, uploaded), "normal cape generation uses ARL mesh");
            var other = new Item { Code = new AssetLocation("game:test") };
            var otherBehavior = Activator.CreateInstance(type, [other])!;
            var otherStack = new ItemStack(other);
            fail = true;
            bool propagated = false;
            try { draw.Invoke(otherBehavior, [api, otherStack, EnumItemRenderTarget.Gui, new ItemRenderInfo { ModelRef = original, InSlot = new DummySlot(otherStack) }]); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { propagated = true; }
            Check(propagated, "other items retain original exception behavior");
            owner.Dispose(); owner = null;
            Check(Harmony.GetPatchInfo(draw)?.Owners.Contains("vsscpmod.cape-render") != true, "dispose removes renderer patch");
            Check(OriginalAtlasCrash(), "dispose also removes atlas mipmap protection");
            enabled.Clear();
            Check(fix.GetMethod("TryCreate")!.Invoke(null, [api]) == null, "missing mods are skipped");
        }
        finally { owner?.Dispose(); host.UnpatchAll(host.Id); }
    }
}
