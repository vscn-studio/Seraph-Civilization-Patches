using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

// Actual VintageCanvas 1.4.0 callbacks, pixel editing and serialization; world,
// mouse ray and GPU uploads are substituted because this runner has no renderer.
static class VintageCanvasPaintChecks
{
    private static int rays, uploads, syncs;
    private static EntityPlayer testEntity = null!;
    private static IPlayerInventoryManager testInventory = null!;
    private static bool PlayerEntity(ref EntityPlayer __result) { __result = testEntity; return false; }
    private static bool PlayerInventory(ref IPlayerInventoryManager __result) { __result = testInventory; return false; }
    private static bool Ray(IWorldAccessor world, ref Vec2d __result)
    {
        if (world.Side != EnumAppSide.Client) throw new Exception("Server used a client picking ray");
        rays++; __result = new Vec2d(0, -0.5); return false;
    }
    private static bool Upload() { uploads++; return false; }
    private static bool Sync() { syncs++; return false; }
    private static bool Dirty() => false;

    public static void Run(string dll, string? roofDll = null)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(dll));
        Type T(string name) => assembly.GetType("VintageCanvas." + name, true)!;
        var toolType = T("src.Items.CollectibleBehaviorPaintTool");
        var easelType = T("src.Blocks.BlockEasel");
        var entityType = T("src.Entities.BlockEntityEasel");
        var systemType = T("VintageCanvasModSystem");
        var textureType = T("src.Utility.TextureUtil");
        var target = AccessTools.Method(toolType, "IsPaintTarget");
        var surface = AccessTools.Method(toolType, "isChiselSurface");
        var compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.VintageCanvasPaintCompatibility", true)!;
        var create = compatibility.GetMethod("TryCreate")!;
        int count = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            count++; Console.WriteLine("PASS: " + name);
        }
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", a ?? [])); return null; });
        bool enabled = false;
        EnumAppSide side = EnumAppSide.Server;
        var pos = new BlockPos(20, 100, 30, 1);
        var main = pos.AddCopy(0, -1, 0);
        var plain = new Block { Code = new("game:stone-granite") };
        var air = new Block { Code = new("game:air") };
        var easel = (Block)Activator.CreateInstance(easelType)!;
        easel.Code = new("vintagecanvas:easel-oak-north");
        easel.Attributes = JsonObject.FromJson("{\"uvyoffset\":0,\"canvassize\":32}");
        var proxy = new BlockMultiblock { Code = new("game:multiblock-monolithic-0-p1-0") };
        AccessTools.Field(typeof(BlockMultiblock), "OffsetInv").SetValue(proxy, new Vec3i(0, -1, 0));
        var micro = new BlockMicroBlock { Code = new("game:chiseledblock") };
        var roof = new Block { Code = new("vsroofing:roof-filled-clay") };
        var roofing = roofDll == null ? null : Assembly.LoadFrom(Path.GetFullPath(roofDll));
        var normalRoof = roofing == null ? new BlockEntityGroundStorage() :
            (BlockEntity)Activator.CreateInstance(roofing.GetType("VSRoofing.AutoRoofEntity", true)!)!;
        var chiseledRoof = roofing == null ? new BlockEntityMicroBlock() :
            (BlockEntityMicroBlock)Activator.CreateInstance(roofing.GetType("VSRoofing.AutoRoofEntityChiseled", true)!)!;
        Block? selected = plain, control = easel;
        BlockEntity? selectedEntity = null, controlEntity = null;
        var accessor = Proxy.Create<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetBlock" => ((BlockPos)a![0]!).Equals(main) ? control : selected,
            "GetBlockEntity" => ((BlockPos)a![0]!).Equals(main) ? controlEntity : selectedEntity,
            _ => null
        });
        var held = new ItemSlot(null);
        var item = new Item { Code = new("vintagecanvas:brush-small") };
        item.VariantStrict.Add("size", "small");
        held.Itemstack = new ItemStack(item);
        held.Itemstack.Attributes.SetInt("paintcolor", unchecked((int)0xffff0000));
        held.Itemstack.Attributes.SetInt("paintamount", 100);
        var manager = Proxy.Create<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? held : null);
        EntityPlayer entity = null!;
        var playerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Server.ServerPlayer", true)!;
        var player = (IPlayer)RuntimeHelpers.GetUninitializedObject(playerType);
        IWorldAccessor world = null!;
        ICoreAPI api = null!;
        world = Proxy.Create<IWorldAccessor>((m, _) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Side" => side, "get_Api" => api,
            "PlayerByUid" => player, _ => null
        });
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? enabled : null);
        api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_Logger" => logger, "get_Side" => side, "get_World" => world, _ => null
        });
        entity = new EntityPlayer { World = world, Api = api };
        testEntity = entity; testInventory = manager;
        var testHarmony = new Harmony("vsscpmod.tests.vintagecanvas");
        testHarmony.Patch(AccessTools.PropertyGetter(playerType, "Entity"), prefix: Hook(nameof(PlayerEntity)));
        testHarmony.Patch(AccessTools.PropertyGetter(playerType, "InventoryManager"), prefix: Hook(nameof(PlayerInventory)));
        var easelEntity = (BlockEntity)Activator.CreateInstance(entityType)!;
        easelEntity.Api = api; easelEntity.Pos = pos;
        var canvas = new InventoryGeneric(1, "canvas-test", null!);
        canvas.Api = api;
        canvas[0].Itemstack = new ItemStack(new Item { Code = new("vintagecanvas:canvas") });
        AccessTools.Field(entityType, "canvasinventory").SetValue(easelEntity, canvas);
        int[] pixels = Enumerable.Repeat(unchecked((int)0xff000000), 1024).ToArray();
        var pixelField = AccessTools.Field(entityType, "pixeldata");
        pixelField.SetValue(easelEntity, pixels);
        var configuration = Activator.CreateInstance(T("src.Utility.VintageCanvasConfig"))!;
        AccessTools.Field(configuration.GetType(), "MixBoxBlending").SetValue(configuration, false);
        AccessTools.Field(systemType, "config").SetValue(null, configuration);
        var tool = (CollectibleBehavior)Activator.CreateInstance(toolType, [item])!;
        BlockSelection Selection(Block? block = null) => new() { Position = pos.Copy(), Face = BlockFacing.NORTH,
            HitPosition = new Vec3d(0.5, 0.5, 0.5), Block = block! };
        bool Target(BlockSelection? selection) => (bool)target.Invoke(null, [selection, world])!;
        bool Surface(Block? block) => (bool)surface.Invoke(null, [block])!;
        bool Throws(Action action, string? method = null)
        {
            try { action(); return false; }
            catch (TargetInvocationException e) when (e.InnerException is NullReferenceException)
                { return method == null || e.InnerException.StackTrace?.Contains(method) == true; }
            catch (NullReferenceException e) { return method == null || e.StackTrace?.Contains(method) == true; }
        }
        Check(Throws(() => Target(Selection())), "real 1.4.0 reproduces the logged null Block failure");
        selected = easel; selectedEntity = easelEntity;
        Check(Throws(() => { var h = EnumHandling.PassThrough; tool.OnHeldInteractStep(1, held, entity, Selection(easel), null!, ref h); }, "CanvasAngleRaycast"),
            "populating Block alone still reproduces server-only client ray failure");
        selected = roof; selectedEntity = normalRoof; side = EnumAppSide.Client;
        bool roofCrash = false;
        try { var h = EnumHandling.PassThrough; tool.OnHeldInteractStep(1, held, entity, Selection(roof), null!, ref h); }
        catch (InvalidCastException) { roofCrash = true; }
        Check(roofCrash, "real paint callback reproduces the logged ordinary roof InvalidCastException");
        side = EnumAppSide.Server;
        IDisposable? owner = null, second = null;
        try
        {
            Check(create.Invoke(null, [api]) == null, "disabled VintageCanvas does not install patches");
            enabled = true;
            owner = (IDisposable?)create.Invoke(null, [api]);
            Check(owner != null && !messages.Any(m => m.Contains("unavailable")), "compatibility installs against the actual mod assembly");
            Check(Harmony.GetPatchInfo(target)?.Owners.Contains("vsscpmod.vintagecanvas-paint") == true,
                "actual target method is patched");
            foreach (var appSide in new[] { EnumAppSide.Server, EnumAppSide.Client })
            {
                side = appSide;
                Check(!Target(null) && !Target(new BlockSelection()), side + " null selection/position is rejected");
                foreach (var (block, expected) in new (Block?, bool)[] { (plain, false), (air, false), (null, false),
                    (new Block(), false), (micro, true), (roof, true), (easel, true) })
                {
                    selected = block; selectedEntity = block == micro || block == roof ? chiseledRoof : easelEntity;
                    Check(Target(Selection()) == expected, side + " resolves " + (block?.Code?.ToString() ?? "missing block/code"));
                }
                selected = easel; selectedEntity = null;
                Check(!Target(Selection()), side + " missing easel entity is rejected");
                selectedEntity = new BlockEntityMicroBlock();
                Check(!Target(Selection()), side + " wrong easel entity is rejected");
                selected = proxy; control = easel; controlEntity = easelEntity;
                var selection = Selection(); var originalPosition = selection.Position.Copy();
                Check(Target(selection) && selection.Position.Equals(originalPosition) && selection.Position.dimension == 1,
                    side + " multiblock resolves its main easel without changing position/dimension");
                controlEntity = null;
                Check(!Target(Selection()), side + " orphan multiblock entity is rejected");
                controlEntity = easelEntity; control = plain;
                Check(!Target(Selection()), side + " stale multiblock cannot reference a replaced main block");
                selected = plain;
                Check(!Target(Selection(easel)), side + " stale selected Block is refreshed from the world");
                Check(!Surface(null) && !Surface(new Block()) && !Surface(plain) && Surface(micro) && Surface(roof),
                    side + " chisel surface detection is null safe and preserves supported surfaces");
                foreach (var block in new[] { micro, roof })
                {
                    selected = block;
                    foreach (var invalid in new BlockEntity?[] { null, normalRoof, easelEntity })
                    {
                        selectedEntity = invalid;
                        Check(!Target(Selection()), side + " " + block.Code + " rejects " + (invalid?.GetType().Name ?? "missing entity"));
                        var h = EnumHandling.PassThrough;
                        int oldAmount = held.Itemstack.Attributes.GetInt("paintamount");
                        tool.OnHeldInteractStep(1, held, entity, Selection(block), null!, ref h);
                        tool.OnHeldInteractCancel(1, held, entity, Selection(block), null!, EnumItemUseCancelReason.ReleasedMouse, ref h);
                        Check(held.Itemstack.Attributes.GetInt("paintamount") == oldAmount,
                            side + " invalid fresco interaction/cancel is safe and consumes no paint");
                    }
                    selectedEntity = chiseledRoof;
                    Check(Target(Selection()), side + " " + block.Code + " accepts a compatible chiseled entity");
                }
                var rollerItem = new Item { Code = new("vintagecanvas:roller-red") };
                rollerItem.VariantStrict.Add("paint", "red");
                var rollerSlot = new ItemSlot(null) { Itemstack = new ItemStack(rollerItem) };
                var rollerType = T("src.Items.CollectibleBehaviorDecorTool");
                var roller = (CollectibleBehavior)Activator.CreateInstance(rollerType, [rollerItem])!;
                selected = roof; selectedEntity = normalRoof;
                var rollerHand = EnumHandHandling.NotHandled; var rollerHandling = EnumHandling.PassThrough;
                roller.OnHeldInteractStart(rollerSlot, entity, Selection(roof), null!, true, ref rollerHand, ref rollerHandling);
                Check(rollerHandling == EnumHandling.PreventDefault && rollerSlot.Itemstack.Item == rollerItem,
                    side + " roller rejects ordinary roofs before casting or consuming paint");
            }
            // These substitutes represent only unavailable GPU/mouse services, and
            // deliberately throw if server drawing ever attempts a client picking ray.
            testHarmony.Patch(AccessTools.Method(easelType, "CanvasAngleRaycast"), prefix: Hook(nameof(Ray)));
            testHarmony.Patch(AccessTools.Method(entityType, "UpdateTexture"), prefix: Hook(nameof(Upload)));
            testHarmony.Patch(AccessTools.Method(entityType, "SynchroniseTexture"), prefix: Hook(nameof(Sync)));
            foreach (var appSide in new[] { EnumAppSide.Server, EnumAppSide.Client })
            {
                side = appSide; selected = easel; selectedEntity = easelEntity; control = easel; controlEntity = easelEntity;
                foreach (bool multi in new[] { false, true })
                {
                    selected = multi ? proxy : easel;
                    var selection = Selection();
                    var hand = EnumHandHandling.NotHandled; var handling = EnumHandling.PassThrough;
                    tool.OnHeldInteractStart(held, entity, selection, null!, true, ref hand, ref handling);
                    Check(selection.Block == selected && hand != EnumHandHandling.NotHandled,
                        side + " " + multi + " right-click start hydrates a fresh packet selection");
                    var before = ((int[])pixelField.GetValue(easelEntity)!).ToArray();
                    int oldRays = rays, oldUploads = uploads, oldSyncs = syncs;
                    tool.OnHeldInteractCancel(0, held, entity, Selection(), null!, EnumItemUseCancelReason.ReleasedMouse, ref handling);
                    Check(tool.OnHeldInteractStep(1, held, entity, Selection(), null!, ref handling),
                        side + " " + multi + " continuous right-click accepts a new packet selection");
                    Check(appSide == EnumAppSide.Server ? rays == oldRays && uploads == oldUploads
                        && before.SequenceEqual((int[])pixelField.GetValue(easelEntity)!) : rays == oldRays + 1 && uploads == oldUploads + 1,
                        side + " " + multi + " pixel drawing runs only on the client");
                    tool.OnHeldInteractCancel(1, held, entity, Selection(), null!, EnumItemUseCancelReason.ReleasedMouse, ref handling);
                    Check(appSide == EnumAppSide.Server ? syncs == oldSyncs : syncs > oldSyncs,
                        side + " " + multi + " cancel retains client stroke synchronization");
                    hand = EnumHandHandling.NotHandled; handling = EnumHandling.PassThrough;
                    tool.OnHeldAttackStart(held, entity, Selection(), null!, ref hand, ref handling);
                    Check(hand != EnumHandHandling.NotHandled && tool.OnHeldAttackStep(1, held, entity, Selection(), null!, ref handling),
                        side + " " + multi + " left-click start and step resolve fresh selections");
                    tool.OnHeldAttackCancel(1, held, entity, Selection(), null!, EnumItemUseCancelReason.ReleasedMouse, ref handling);
                    selected = easel; selectedEntity = null;
                    tool.OnHeldInteractStep(2, held, entity, Selection(), null!, ref handling);
                    tool.OnHeldInteractCancel(2, held, entity, Selection(), null!, EnumItemUseCancelReason.ReleasedMouse, ref handling);
                    Check(true, side + " " + multi + " removing the easel during a stroke is safe");
                    selectedEntity = easelEntity;
                }
            }
            Check(((int[])pixelField.GetValue(easelEntity)!).Any(p => p != unchecked((int)0xff000000)),
                "actual ApplyTool/ApplyBrush changes canvas pixels on the client");
            side = EnumAppSide.Client; selected = easel; selectedEntity = easelEntity;
            AccessTools.Field(configuration.GetType(), "PaintDepletion").SetValue(configuration, true);
            foreach (string size in new[] { "small", "medium", "large" })
            {
                item.VariantStrict["size"] = size;
                var handling = EnumHandling.PassThrough;
                tool.OnHeldInteractCancel(0, held, entity, Selection(), null!, EnumItemUseCancelReason.ReleasedMouse, ref handling);
                int amount = held.Itemstack.Attributes.GetInt("paintamount");
                tool.OnHeldInteractStep(3, held, entity, Selection(), null!, ref handling);
                Check(held.Itemstack.Attributes.GetInt("paintamount") < amount, size + " brush retains original paint depletion");
            }
            testHarmony.Patch(AccessTools.Method(typeof(BlockEntity), nameof(BlockEntity.MarkDirty)), prefix: Hook(nameof(Dirty)));
            testHarmony.Patch(AccessTools.Method(typeof(BlockEntityMicroBlock), "MarkMeshDirty"), prefix: Hook(nameof(Dirty)));
            testHarmony.Patch(AccessTools.Method(toolType, "SynchroniseFrescoTexture"), prefix: Hook(nameof(Sync)));
            var frescoData = (Dictionary<string, int[]>)AccessTools.Field(T("src.Utility.FrescoStore"), "Data").GetValue(null)!;
            string frescoKey = "vintagecanvasfresco" + pos + "-" + BlockFacing.NORTH.Index;
            foreach (var block in new[] { micro, roof })
            {
                selected = block; selectedEntity = chiseledRoof; side = EnumAppSide.Client;
                var frescoTool = (CollectibleBehavior)Activator.CreateInstance(toolType, [item])!;
                frescoData[frescoKey] = Enumerable.Repeat(unchecked((int)0xff000000), 1024).ToArray();
                held.Itemstack.Attributes.SetInt("paintamount", 100);
                held.Itemstack.Attributes.RemoveAttribute("timestamp");
                item.VariantStrict["size"] = "small";
                var h = EnumHandling.PassThrough;
                frescoTool.OnHeldInteractStep(1, held, entity, Selection(), null!, ref h);
                Check(frescoData[frescoKey].Any(p => p != unchecked((int)0xff000000))
                    && held.Itemstack.Attributes.GetInt("paintamount") < 100,
                    block.Code + " compatible chiseled entity retains actual fresco painting and paint depletion");
                int oldSyncs = syncs;
                frescoTool.OnHeldInteractCancel(1, held, entity, Selection(), null!, EnumItemUseCancelReason.ReleasedMouse, ref h);
                Check(syncs == oldSyncs + 1, block.Code + " fresco cancel still sends the completed stroke");
                side = EnumAppSide.Server;
                var before = frescoData[frescoKey].ToArray();
                frescoTool.OnHeldInteractStep(2, held, entity, Selection(), null!, ref h);
                Check(before.SequenceEqual(frescoData[frescoKey]), block.Code + " server does not duplicate client fresco edits");
                frescoData.Remove(frescoKey);
            }
            byte[] encoded = (byte[])AccessTools.Method(textureType, "WriteCompressedPixelData").Invoke(null, [pixelField.GetValue(easelEntity)])!;
            int[] decoded = (int[])AccessTools.Method(textureType, "ReadCompressedPixelData").Invoke(null, [encoded])!;
            Check(decoded.SequenceEqual((int[])pixelField.GetValue(easelEntity)!), "paint pixels survive the real network compression format");
            side = EnumAppSide.Server; selected = easel; selectedEntity = easelEntity;
            var networkType = T("src.Utility.CanvasNetworkHandler");
            var network = RuntimeHelpers.GetUninitializedObject(networkType);
            var serverWorld = Proxy.Create<IServerWorldAccessor>((m, _) => m.Name == "get_BlockAccessor" ? accessor : null);
            var serverApi = Proxy.Create<ICoreServerAPI>((m, _) => m.Name == "get_World" ? serverWorld : null);
            AccessTools.Field(networkType, "api").SetValue(network, serverApi);
            var packetType = T("PaintSavePacket");
            var packet = Activator.CreateInstance(packetType)!;
            foreach (var (name, value) in new (string, object)[] { ("PosX", pos.X), ("PosY", pos.Y), ("PosZ", pos.Z),
                ("PixelData", encoded), ("Face", 0) }) AccessTools.Property(packetType, name).SetValue(packet, value);
            AccessTools.Method(networkType, "OnServerReceiveSave").Invoke(network, [null, packet]);
            Check(((int[])pixelField.GetValue(easelEntity)!).SequenceEqual(decoded),
                "original server PaintSavePacket receiver decompresses and saves client pixels");
            second = (IDisposable?)create.Invoke(null, [api]);
            owner!.Dispose();
            Check(Target(Selection()), "disposing one side retains the other side's patch");
            second!.Dispose();
            Check(Harmony.GetPatchInfo(target)?.Owners.Contains("vsscpmod.vintagecanvas-paint") != true
                && Throws(() => Target(Selection())), "last disposal removes patches and restores original behavior");
            Console.WriteLine($"{count} VintageCanvas painting checks passed.");
        }
        finally { second?.Dispose(); owner?.Dispose(); testHarmony.UnpatchAll(testHarmony.Id); }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(VintageCanvasPaintChecks), name);
}
