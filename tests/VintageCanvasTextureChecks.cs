using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

// Run the actual public methods, their queued closures and disposal; substitute
// only bitmap/GPU services and tessellation, since this runner has no GL context.
static class VintageCanvasTextureChecks
{
    private static bool Bitmap() => false;

    public static void Run(string dll)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(dll));
        var texture = assembly.GetType("VintageCanvas.src.Utility.TextureUtil", true)!;
        var compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.VintageCanvasTextureCompatibility", true)!;
        var create = compatibility.GetMethod("TryCreate")!;
        int count = 0, uploads = 0, renders = 0, deletions = 0, tesselations = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            count++; Console.WriteLine("PASS: " + name);
        }
        var library = Assembly.Load("VintagestoryLib");
        var clientType = library.GetType("Vintagestory.Client.NoObf.ClientMain", true)!;
        var client = (IClientWorldAccessor)RuntimeHelpers.GetUninitializedObject(clientType);
        var platformType = library.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows", true)!;
        AccessTools.Field(clientType, "Platform").SetValue(client, RuntimeHelpers.GetUninitializedObject(platformType));
        var testHarmony = new Harmony("vsscpmod.tests.vintagecanvas-texture");
        testHarmony.Patch(AccessTools.DeclaredMethod(platformType, "CreateBitmapFromPixels"),
            prefix: new HarmonyMethod(typeof(VintageCanvasTextureChecks), nameof(Bitmap)));
        var tasks = new Queue<Action>();
        var textures = new List<LoadedTexture>();
        TextureAtlasPosition? position = new() { atlasNumber = 1, atlasTextureId = 43, x1 = .25f, y1 = .5f };
        LoadedTexture? uploaded = null;
        int destination = 0;
        float targetX = 0, targetY = 0;
        var atlas = Proxy.Create<IBlockTextureAtlasAPI>((m, a) =>
        {
            switch (m.Name)
            {
                case "get_AtlasTextures": return textures;
                case "get_Size": return new Size2i(1024, 1024);
                case "GetOrInsertTexture": a![1] = 9; a[2] = position; return true;
                case "RenderTextureIntoAtlas":
                    renders++; destination = (int)a![0]!; targetX = (float)a[6]!; targetY = (float)a[7]!; break;
            }
            return null;
        });
        var events = Proxy.Create<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "EnqueueMainThreadTask") tasks.Enqueue((Action)a![0]!);
            return null;
        });
        var render = Proxy.Create<IRenderAPI>((m, a) =>
        {
            if (m.Name == "LoadTexture") { uploads++; uploaded = (LoadedTexture)a![1]!; uploaded.TextureId = 99; }
            return null;
        });
        var gui = Proxy.Create<IGuiAPI>((m, _) => { if (m.Name == "DeleteTexture") deletions++; return null; });
        var mesh = new MeshData();
        var tesselator = Proxy.Create<ITesselatorAPI>((m, a) =>
        {
            if (m.Name == "TesselateShape") { tesselations++; a![3] = mesh; }
            return null;
        });
        bool enabled = false;
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? enabled : null);
        var logger = Proxy.Create<ILogger>((m, a) =>
        {
            if (m.Name == "Warning") throw new Exception("Unexpected compatibility warning: " + a![0]);
            return null;
        });
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => client, "get_BlockTextureAtlas" => atlas, "get_Event" => events,
            "get_Render" => render, "get_Gui" => gui, "get_Tesselator" => tesselator,
            "get_ModLoader" => loader, "get_Logger" => logger, _ => null
        });
        object? Schedule(bool withMesh)
        {
            object?[] arguments = withMesh
                ? [new int[4], 2, new AssetLocation("vintagecanvas:test"), null,
                    new AssetLocation("vintagecanvas:canvas"), new CompositeShape(), api]
                : [new int[4], 2, new AssetLocation("vintagecanvas:test"), api];
            return AccessTools.Method(texture, withMesh ? "SwapPaintingTextureMesh" : "SwapPaintingTexture").Invoke(null, arguments);
        }
        bool OutOfRange(Action task)
        {
            try { task(); return false; } catch (ArgumentOutOfRangeException) { uploaded?.Dispose(); return true; }
        }
        IDisposable? owner = null, second = null;
        try
        {
            foreach (bool withMesh in new[] { false, true })
            {
                Schedule(withMesh);
                Check(OutOfRange(tasks.Dequeue()), "real " + withMesh + " queued update reproduces the logged atlas list exception");
            }
            Check(create.Invoke(null, [api]) == null, "disabled mod leaves texture callbacks unpatched");
            // Schedule before installation to verify the callback is guarded at execution time.
            Schedule(true);
            enabled = true; owner = (IDisposable?)create.Invoke(null, [api]);
            Check(owner != null, "texture compatibility installs against actual VintageCanvas 1.4.0");
            int oldUploads = uploads;
            tasks.Dequeue()();
            Check(uploads == oldUploads, "already queued stale task is skipped before allocating GPU resources");
            foreach (bool withMesh in new[] { false, true })
            {
                textures.Clear();
                position = new TextureAtlasPosition { atlasNumber = 1, atlasTextureId = 43, x1 = .25f, y1 = .5f };
                textures.Add(new LoadedTexture(api, 42, 1024, 1024));
                textures.Add(new LoadedTexture(api, 43, 1024, 1024));
                int oldRenders = renders, oldDeletes = deletions;
                oldUploads = uploads;
                var result = Schedule(withMesh);
                Check(uploads == oldUploads && tasks.Count == 1 && (withMesh ? ReferenceEquals(result, mesh) : ReferenceEquals(result, position)),
                    withMesh + " preserves deferred upload and the original position/mesh return");
                tasks.Dequeue()();
                Check(uploads == oldUploads + 1 && renders == oldRenders + 1 && destination == 43
                    && targetX == 256 && targetY == 512 && deletions == oldDeletes + 1 && uploaded!.Disposed,
                    withMesh + " live atlas receives the original upload, UV position and temporary texture disposal");
                foreach (string state in new[] { "cleared", "shortened", "replaced", "disposed", "zero", "null-position" })
                {
                    position = new TextureAtlasPosition { atlasNumber = 1, atlasTextureId = 43 };
                    textures.Clear();
                    textures.Add(new LoadedTexture(api));
                    textures.Add(new LoadedTexture(api, 43, 1024, 1024));
                    if (state == "null-position") position = null;
                    Schedule(withMesh);
                    switch (state)
                    {
                        case "cleared": textures.Clear(); break;
                        case "shortened": textures.RemoveAt(1); break;
                        case "replaced": textures[1].TextureId = 44; break;
                        case "disposed": textures[1].Dispose(); break;
                        case "zero": textures[1].TextureId = 0; position!.atlasTextureId = 0; break;
                    }
                    oldUploads = uploads; oldRenders = renders;
                    tasks.Dequeue()();
                    Check(uploads == oldUploads && renders == oldRenders, withMesh + " safely skips " + state + " atlas at execution time");
                }
            }
            Check(tesselations > 0, "mesh tessellation still runs in the original method");
            second = (IDisposable?)create.Invoke(null, [api]);
            owner!.Dispose();
            Schedule(false); tasks.Dequeue()();
            Check(true, "disposing one owner keeps the other owner's guard installed");
            second!.Dispose();
            position = new TextureAtlasPosition { atlasNumber = 1, atlasTextureId = 43 };
            textures.Clear(); Schedule(false);
            Check(OutOfRange(tasks.Dequeue()), "last disposal removes texture patches and restores the original callback");
            Console.WriteLine($"{count} VintageCanvas texture checks passed.");
        }
        finally
        {
            second?.Dispose(); owner?.Dispose(); testHarmony.UnpatchAll(testHarmony.Id);
            foreach (var loaded in textures) loaded.Dispose();
        }
    }
}
