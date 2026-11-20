using System.Reflection;
using HarmonyLib;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using SeraphCivilizationPatches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

static class QuarryDragChecks
{
    public static void Run(string root, ICoreAPI originalApi, IPlayer owner, System.Func<int, int, ItemStack> makeSlab)
    {
        int count = 0;
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((m, a) => m.Name switch {
            "HasTranslation" => true, "Get" => a![0], _ => null });
        Lang.ChangeLanguage("en");
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        Type fix = typeof(EntityQuarryDrag).Assembly.GetType("SeraphCivilizationPatches.QuarryDragCompatibility")!;
        var patcher = new Harmony("vsscpmod.tests.quarrydrag");
        var player = owner.Entity;
        var oldWorld = player.World; var oldApi = player.Api;
        var oldPos = player.Pos.Copy(); var oldControls = new EntityControls(); oldControls.SetFrom(player.Controls);
        var oldWalk = player.Controls.WalkVector.Clone();
        var quarry = Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/quarry/StoneQuarry.dll"));
        var nativeSlab = (Block)Activator.CreateInstance(quarry.GetType("StoneQuarry.BlockStoneSlab")!)!;
        nativeSlab.BlockId = 77; nativeSlab.Code = new("stonequarry:stoneslab-medium-west"); nativeSlab.MaxStackSize = 16;
        nativeSlab.Variant = new(new Dictionary<string, string> { ["side"] = "west", ["size"] = "medium" });
        var multi = new BlockBehaviorMultiblock(nativeSlab);
        multi.Initialize(new JsonObject(JObject.Parse("""{"sizex":1,"sizey":1,"sizez":2,"cposition":{"x":0,"y":0,"z":1}}""")));
        nativeSlab.BlockBehaviors = [multi]; nativeSlab.CollectibleBehaviors = [multi];
        var proxy = new BlockMultiblock { BlockId = 78, Code = new("game:multiblock-monolithic-0-0-n1"), OffsetInv = new(0, 0, 1) };
        var air = new Block { Code = new("game:air"), Replaceable = 10000, BlockId = 0, SideSolid = new(0), CollisionBoxes = [], SelectionBoxes = [] };
        var floor = new Block { Code = new("game:stone"), BlockId = 79, CollisionBoxes = [new(0, 0, 0, 1, 1, 1)], SelectionBoxes = [new(0, 0, 0, 1, 1, 1)] };
        var grass = new BlockPlant { Code = new("game:tallgrass-medium-free"), BlockId = 83,
            Replaceable = 6000, RandomDrawOffset = 2, CollisionBoxes = [], SelectionBoxes = [new(0, 0, 0, 1, .25f, 1)] };
        var snowyGrass = new BlockPlant { Code = new("game:tallgrass-medium-snow3"), BlockId = 84,
            Replaceable = 6000, RandomDrawOffset = 2, CollisionBoxes = [new(0, 0, 0, 1, .125f, 1)], SelectionBoxes = [new(0, 0, 0, 1, .375f, 1)] };
        var rope = new ItemRope { Code = new("game:rope"), ItemId = 80, MaxStackSize = 64 };
        var chain = new Item { Code = new("vsrestraints:ironchain"), ItemId = 81, MaxStackSize = 64 };
        var wheel = new Item { Code = new("cartwrightscaravan:cartsmisc-wheelsmall"), ItemId = 82, MaxStackSize = 64 };
        var solidWheel = new Item { Code = new("cartwrightscaravan:cartsmisc-wheelsolid"), ItemId = 85, MaxStackSize = 64 };
        var spokedWheel = new Item { Code = new("cartwrightscaravan:cartsmisc-wheelspoked"), ItemId = 86, MaxStackSize = 64 };
        Item[] wheels = [wheel, solidWheel, spokedWheel];
        var map = new Dictionary<BlockPos, Block>();
        var entities = new Dictionary<long, Entity>();
        var received = new List<ItemStack>();
        BlockEntity? be = null;
        ItemStack expectedContent = null!;
        long now = 0, nextId = 100;
        bool access = true, loaded = true, spawnFails = false, spawnThrows = false, connected = true;
        var chunk = Proxy.Create<IWorldChunk>((_, _) => null);
        var properties = new EntityProperties { Code = new("vsscpmod:quarrydrag"), Class = "VsscpmodQuarryDrag",
            Client = new([], new()) { Behaviors = [] }, Server = new([], new()) { Behaviors = [] } };
        ICoreAPI api = null!; IWorldAccessor world = null!;
        var blocks = Proxy.Create<IBlockAccessor>((m, a) => m.Name switch {
            "GetBlock" => a!.Length > 1 && a[1] is int layer && layer == BlockLayersAccess.Fluid ? air : Get((BlockPos)a[0]!),
            "WalkBlocks" => Walk(a!), "GetChunkAtBlockPos" => loaded ? chunk : null,
            "GetBlockRaw" => Get(new((int)a![0]!, (int)a[1]!, (int)a[2]!)),
            "GetBlockEntity" => be?.Pos == (BlockPos)a![0]! ? be : null,
            "SetBlock" => Set((int)a![0]!, (BlockPos)a[1]!), _ => null });
        var registry = Proxy.Create<IClassRegistryAPI>((m, a) => m.Name == "CreateEntity"
            ? new EntityQuarryDrag { Code = properties.Code } : m.Invoke(originalApi.ClassRegistry, a));
        var claims = Proxy.Create<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? access : null);
        world = Proxy.Create<IWorldAccessor>((m, a) => m.Name switch {
            "get_Api" => api, "get_BlockAccessor" => blocks, "get_ClassRegistry" => registry,
            "get_Claims" => claims, "get_ElapsedMilliseconds" => now,
            "PlayerByUid" => connected ? owner : null,
            "GetEntityType" => properties, "GetEntityById" => entities.GetValueOrDefault((long)a![0]!),
            "SpawnEntity" => Spawn((EntityQuarryDrag)a![0]!),
            "GetEntitiesAround" => entities.Values.Where(e => e.Alive).ToArray(),
            "GetBlock" => a![0] is int id ? id switch { 77 => nativeSlab, 78 => proxy, 79 => floor, _ => originalApi.World.GetBlock(id) }
                : ((AssetLocation)a[0]!).Path.StartsWith("multiblock-") ? proxy
                : ((AssetLocation)a[0]!).Path == "rock-granite" ? originalApi.World.GetBlock(3)
                : ((AssetLocation)a[0]!).Domain == "stonequarry" ? nativeSlab : originalApi.World.GetBlock((AssetLocation)a[0]!),
            "GetItem" => a![0] is int iid ? iid == 80 ? rope : iid == 81 ? chain : wheels.FirstOrDefault(w => w.ItemId == iid) ?? originalApi.World.GetItem(iid)
                : (AssetLocation)a[0]! == rope.Code ? rope : (AssetLocation)a[0]! == chain.Code ? chain : wheels.FirstOrDefault(w => w.Code == (AssetLocation)a[0]!),
            _ => m.Invoke(originalApi.World, a) });
        api = Proxy.Create<ICoreAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_ClassRegistry" => registry, _ => m.Invoke(originalApi, a) });
        foreach (var col in new CollectibleObject[] { nativeSlab, rope, chain, wheel, solidWheel, spokedWheel }) AccessTools.Field(typeof(CollectibleObject), "api").SetValue(col, api);
        using var hooks = (IDisposable)AccessTools.Method(fix, "TryCreate").Invoke(null, [api])!;
        try
        {
            // Replace engine-only initialization/networking; exercise the real
            // dispatcher, native Quarry serialization, block placement and entity saves.
            patcher.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.Initialize)), prefix: Hook(nameof(Initialize)));
            patcher.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.OnGameTick)), prefix: Hook(nameof(Tick)));
            patcher.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.StartAnimation)), prefix: Hook(nameof(Skip)));
            patcher.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.StopAnimation)), prefix: Hook(nameof(Skip)));
            patcher.Patch(AccessTools.Method(owner.GetType(), "SendIngameError"), prefix: Hook(nameof(Skip)));
            patcher.Patch(AccessTools.PropertyGetter(owner.GetType(), nameof(IServerPlayer.ConnectionState)), prefix: Hook(nameof(Connection)));
            Given = received;
            Right = new DummySlot(); Left = new DummySlot();
            patcher.Patch(AccessTools.PropertyGetter(typeof(EntityPlayer), nameof(EntityPlayer.RightHandItemSlot)), prefix: Hook(nameof(HandRight)));
            patcher.Patch(AccessTools.PropertyGetter(typeof(EntityPlayer), nameof(EntityPlayer.LeftHandItemSlot)), prefix: Hook(nameof(HandLeft)));
            patcher.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.TryGiveItemStack)), prefix: Hook(nameof(Give)));
            player.World = world; player.Api = api;
            player.WatchedAttributes.SetString("playerUID", "quarry-drag-test");
            player.Pos.SetPos(10.5, 20, 11);
            var groundType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Common.InventoryPlayerGround")!;
            var ground = (InventoryBase)Activator.CreateInstance(groundType, "ground", "quarry-drag-test", api)!;
            var source = new DummySlot(makeSlab(64, 1));
            foreach (int amount in new[] { 0, 1, 64, 224 })
            {
                source.Itemstack = makeSlab(amount, 1);
                var handling = EnumHandling.PassThrough;
                source.Itemstack.Collectible.OnHeldDropped(world, owner, source, 1, ref handling);
                Check(handling == EnumHandling.PreventDefault && source.StackSize == 1, "Q drop rejected without removing slab: " + amount);
                var op = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, 1) { ActingPlayer = owner };
                Check(source.TryPutInto(ground[0], ref op) == 0 && source.StackSize == 1 && !ground[0].CanHold(source),
                    "native ground transfer and admission rejected before source mutation: " + amount);
            }
            var container = new ItemStack(new Item { Code = new("game:bag"), ItemId = 90 });
            container.Attributes.GetOrAddTreeAttribute("contents").SetItemstack("0", makeSlab(64, 1));
            Check(!ground[0].CanTakeFrom(new DummySlot(container)), "nested slab cannot bypass ground admission inside a container");
            source.Itemstack = makeSlab(64, 1); player.Alive = false;
            var deathHandling = EnumHandling.PassThrough;
            source.Itemstack.Collectible.OnHeldDropped(world, owner, source, 1, ref deathHandling);
            Check(deathHandling == EnumHandling.PassThrough && ground[0].CanTakeFrom(source), "native death drop remains possible");
            player.Alive = true;

            bool Free(ItemStack stack) => (bool)AccessTools.Method(fix, "FreeBinding").Invoke(null, [stack])!;
            var usedRope = new ItemStack(rope); usedRope.Attributes.SetInt("clothId", 1);
            var usedChain = new ItemStack(chain); usedChain.Attributes.SetString("vschain-token", "live");
            Check(!Free(new(rope)) && Free(new(chain)) && !Free(usedRope) && !Free(usedChain), "rope no longer binds slabs; free chains accepted and live tethers preserved");

            foreach (bool replaceTool in new[] { false, true })
            {
                ResetSlab();
                var held = new DummySlot(new ItemStack(chain));
                var selection = new BlockSelection { Position = new(10, 20, 10), Face = BlockFacing.UP };
                object[] start = [held, player, selection, null!, EnumHandInteract.HeldItemInteract, true, EnumHandHandling.NotHandled];
                AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStart)).Invoke(chain, start);
                if (replaceTool) held.Itemstack = new(rope);
                else AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseCancel)).Invoke(chain,
                    [.2f, held, player, selection, null, EnumItemUseCancelReason.ReleasedMouse]);
                now += 2100;
                AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStep)).Invoke(chain, [2.1f, held, player, selection, null]);
                Check(entities.Count == 0 && held.StackSize == 1 && Get(selection.Position) == nativeSlab,
                    "cancellation or switching binding tool preserves slab and material: switched=" + replaceTool);
            }

            foreach (bool snowCovered in new[] { false, true })
            {
                const bool useChain = true;
                ResetSlab();
                var held = new DummySlot(new ItemStack(useChain ? chain : rope, 2));
                held.Itemstack!.Attributes.SetInt("vschain-length", 12);
                var material = held.Itemstack.Clone(); material.StackSize = 1;
                var selection = new BlockSelection { Position = new(10, 20, 9), Face = BlockFacing.UP };
                Check(Get(selection.Position) == proxy && (BlockPos?)AccessTools.Method(fix, "SlabPosition").Invoke(null, [world, selection]) == new BlockPos(10, 20, 10), "native proxy resolves to slab controller");
                Check(player.Alive && player.MountedOn == null && (bool)AccessTools.Method(fix, "Access").Invoke(null, [world, owner, new[] { new BlockPos(10, 20, 10), selection.Position }, true])!, "binding access is allowed");
                var handling = EnumHandHandling.NotHandled;
                held.Itemstack.Collectible.OnHeldUseStart(held, player, selection, null, EnumHandInteract.HeldItemInteract, true, ref handling);
                Check(handling == EnumHandHandling.PreventDefault, "proxy selection starts custom binding before native tether: chain=" + useChain);
                now += 500;
                var state = (EnumHandInteract)AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStep)).Invoke(held.Itemstack.Collectible, [.5f, held, player, selection, null])!;
                Check(state == EnumHandInteract.HeldItemInteract && entities.Count == 0 && held.StackSize == 2, "short hold never binds or consumes material: " + state);
                now += 1600;
                AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStep)).Invoke(held.Itemstack.Collectible, [2.1f, held, player, selection, null]);
                var drag = entities.Values.OfType<EntityQuarryDrag>().Single();
                Check(Get(new(10, 20, 10)).Id == 0 && Get(new(10, 20, 9)).Id == 0 && held.StackSize == 1,
                    "one bound entity replaces controller and proxy; consumes exactly one material");
                Check(drag.Slab!.Attributes.Equals(world, expectedContent.Attributes) && drag.Slab.Block == nativeSlab
                    && drag.Width == 1 && drag.Depth == 2 && drag.MeshOffset.Z == 0,
                    "all rocks retained; west orientation and offset footprint survive binding");
                Check(drag.HolderUid == player.PlayerUID && player.Stats.GetBlended("walkspeed") < 1,
                    "binding starts pulling and applies movement cost");
                var savedMaterial = drag.WatchedAttributes.GetItemstack("binding")!;
                Check(savedMaterial.StackSize == 1 && savedMaterial.Attributes.Equals(world, material.Attributes), "binding attributes and extended chain length remain intact");
                AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.OnHeldUseStop)).Invoke(held.Itemstack!.Collectible, [2.1f, held, player, selection, null, EnumHandInteract.HeldItemInteract]);
                player.Pos.SetPos(14, 20, 10); drag.OnGameTick(.05f);
                Check(drag.Pos.Motion.X > 0 && drag.Pos.Motion.X <= .018 && drag.Pos.Motion.Y == 0, "pull uses bounded horizontal motion with no lift or teleport");
                connected = false; drag.OnGameTick(.05f); connected = true;
                Check(drag.HolderUid == "" && drag.Pos.Motion.X == 0, "disconnect automatically releases pulling");
                drag.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Online = false; drag.OnGameTick(.05f); Online = true;
                Check(drag.HolderUid == "", "offline player record still in world does not retain pulling");
                drag.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);

                using var stream = new MemoryStream();
                drag.ToBytes(new BinaryWriter(stream), false); stream.Position = 0;
                var restored = new EntityQuarryDrag(); restored.FromBytes(new BinaryReader(stream), false);
                restored.Initialize(properties, api, 0);
                Check(restored.HolderUid == "" && restored.Slab!.Attributes.Equals(world, drag.Slab.Attributes)
                    && restored.WatchedAttributes.GetItemstack("binding")!.Attributes.Equals(world, material.Attributes),
                    "native entity binary save/load preserves rocks and binding; clears stale holder");
                drag.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact); // release original
                entities.Clear(); entities.Add(restored.EntityId, restored);
                restored.OnGround = true; player.Controls.Sneak = true;
                var plant = snowCovered ? snowyGrass : grass;
                map[new(10, 20, 9)] = plant; map[new(10, 20, 10)] = plant;
                var supplier = Proxy.Create<IWorldIntersectionSupplier>((m, a) => m.Name switch {
                    "GetBlock" => Get((BlockPos)a![0]!), "get_blockAccessor" => blocks,
                    "GetBlockIntersectionBoxes" => Get((BlockPos)a![0]!) is BlockPlant p
                        ? p.GetSelectionBoxes(blocks, (BlockPos)a[0]!)
                        : Get((BlockPos)a![0]!).SelectionBoxes ?? Get((BlockPos)a[0]!).CollisionBoxes,
                    "GetEntitiesAround" => a![3] is ActionConsumable<Entity> filter && !filter(restored) ? Array.Empty<Entity>() : new Entity[] { restored },
                    "IsValidPos" => true, "get_MapSize" => new Vec3i(100, 100, 100), _ => null });
                var game = (Vintagestory.Common.GameMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientMain));
                game.interesectionTester = new AABBIntersectionTest(supplier);
                AccessTools.Field(typeof(Vintagestory.Common.GameMain), "entitySelTmp").SetValue(game, new EntitySelection());
                BlockSelection selectedBlock = null!; EntitySelection selectedEntity = null!;
                foreach (var origin in new Vec3d[] { new(12, 20.1, 10.5), new(9, 20.1, 10.5), new(10.5, 20.1, 8), new(10.5, 20.1, 12) })
                {
                    var ray = Ray.FromPositions(origin, new(10.5, 20.1, 10));
                    game.RayTraceForSelection(supplier, ray, ref selectedBlock, ref selectedEntity);
                    Check(selectedBlock == null && selectedEntity?.Entity == restored,
                        "native ray picking reaches slab through randomly offset grass on every side: snow=" + snowCovered + " origin=" + origin);
                }
                var eastRay = Ray.FromPositions(new(13.5, 20.1, 10.5), new(10.5, 20.1, 10.5));
                var raw = new AABBIntersectionTest(supplier); raw.LoadRayAndPos(eastRay);
                var grassHit = raw.GetSelectedBlock((float)eastRay.Length)!;
                double grassDistance = eastRay.origin.SquareDistanceTo(grassHit.HitPosition.AddCopy(grassHit.Position.X, grassHit.Position.InternalY, grassHit.Position.Z));
                int selectionIndex = 0;
                restored.IntersectsRay(eastRay, raw, out _, ref selectionIndex);
                Check(grassDistance <= eastRay.origin.SquareDistanceTo(raw.hitPosition),
                    "fixture reproduces grass winning the native block-versus-entity distance comparison");
                game.RayTraceForSelection(supplier, eastRay, ref selectedBlock, ref selectedEntity, null, e => false);
                Check(selectedBlock?.Block == plant && selectedEntity == null, "grass selection fix preserves entity filters");
                map[new(12, 20, 10)] = plant;
                game.RayTraceForSelection(supplier, eastRay, ref selectedBlock, ref selectedEntity);
                Check(selectedBlock?.Position == new BlockPos(12, 20, 10) && selectedEntity == null, "plants outside the slab keep their original selection");
                map[new(12, 20, 10)] = floor;
                game.RayTraceForSelection(supplier, eastRay, ref selectedBlock, ref selectedEntity);
                Check(selectedBlock?.Position == new BlockPos(12, 20, 10) && selectedEntity == null, "solid walls still block slab selection");
                map.Remove(new(12, 20, 10)); map.Remove(new(10, 20, 9)); map.Remove(new(10, 20, 10));
                access = false; restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(restored.Alive && received.Count == 0 && Get(new(10, 20, 10)).Id == 0, "claim denial preserves bound slab and material");
                access = true; player.Pos.SetPos(12, 20, 10);
                restored.OnGround = false; restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(restored.Alive && received.Count == 0, "airborne load cannot become a floating placed slab");
                restored.OnGround = true;
                map[new(10, 19, 9)] = air;
                restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(restored.Alive && received.Count == 0, "missing support beneath one multiblock cell rejects placement");
                map.Remove(new(10, 19, 9));
                map[new(10, 20, 9)] = floor;
                restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(restored.Alive && received.Count == 0, "obstructed footprint rejects placement without consuming contents");
                map.Remove(new(10, 20, 9));
                map[new(10, 20, 9)] = plant;
                map[new(10, 20, 10)] = plant;
                if (snowCovered) restored.Pos.Y += .125;
                restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(!restored.Alive && Get(new(10, 20, 10)) == nativeSlab && received.Count == 1,
                    "Shift interaction replaces grass in controller and proxy cells and returns exactly one binding: snow=" + snowCovered);
                var after = nativeSlab.OnPickBlock(world, new(10, 20, 10));
                Check(after.Attributes.Equals(world, expectedContent.Attributes) && received[0].Attributes.Equals(world, material.Attributes),
                    "native Quarry block entity roundtrip keeps complete inventory and exact original binding");
                player.Controls.Sneak = false;
            }
            object[] helpArgs = [nativeSlab, world, Array.Empty<WorldInteraction>()];
            AccessTools.Method(fix, "Help").Invoke(null, helpArgs);
            var help = (WorldInteraction[])helpArgs[2];
            Check(help.Length == 1 && help[0].ActionLangCode == "vsscpmod:quarry-drag-bind"
                && help[0].Itemstacks.Single().Collectible == chain, "placed slab only advertises chain binding");

            ResetSlab();
            var controller = new BlockPos(10, 20, 10);
            var target = new BlockSelection { Position = new(10, 20, 9), Face = BlockFacing.UP };
            foreach (var wheelItem in wheels)
            {
                var slot = new DummySlot(new ItemStack(wheelItem, 4));
                var handling = EnumHandHandling.NotHandled;
                wheelItem.OnHeldUseStart(slot, player, target, null, EnumHandInteract.HeldItemInteract, true, ref handling);
                Check(handling == EnumHandHandling.NotHandled && entities.Count == 0 && slot.StackSize == 4
                    && Get(controller) == nativeSlab && Get(target.Position) == proxy,
                    "holding wheels does not convert a placed slab or consume wheels: " + wheelItem.Code);
            }
            var binding = new DummySlot(new ItemStack(chain));
            AccessTools.Method(fix, "Bind").Invoke(null, [player, binding, controller]);
            var load = entities.Values.OfType<EntityQuarryDrag>().Single();
            foreach (var wheelItem in wheels)
            {
                Right.Itemstack = new ItemStack(wheelItem, 4);
                load.OnInteract(player, Right, new(), EnumInteractMode.Interact);
                Check(Right.StackSize == 4 && load.WatchedAttributes.GetItemstack("wheels") == null
                    && load.HolderUid == player.PlayerUID && load.Chain,
                    "interacting with wheels cannot modify a chain-bound slab: " + wheelItem.Code);
            }
            Right.Itemstack = null;
            // Exercise the actual native terrain collision entrypoint and its patch.
            var walker = new EntityAgent { World = world, Api = api, CollisionBox = new(-.25f, 0, -.25f, .25f, 1.8f, .25f) };
            AccessTools.Property(typeof(Entity), "Properties").SetValue(walker, properties);
            Vec3d Collide(Entity e, double x, double y, double z, double mx, double my, double mz)
            {
                e.Pos.SetPos(x, y, z); e.Pos.Motion.Set(mx, my, mz);
                var next = new Vec3d(); new CollisionTester().ApplyTerrainCollision(e, e.Pos, 1, ref next, 0);
                return next;
            }
            var solidBounds = load.CollisionBox.ToDouble().Translate(load.Pos.XYZ);
            var side = Collide(walker, solidBounds.X1 - 1, 20, 10, 2, 0, 0);
            Check(Math.Abs(side.X - (solidBounds.X1 - .25)) < .001 && walker.CollidedHorizontally, "native physics prevents walking through slab from west");
            side = Collide(walker, solidBounds.X2 + 1, 20, 10, -2, 0, 0);
            Check(Math.Abs(side.X - (solidBounds.X2 + .25)) < .001 && walker.CollidedHorizontally, "native physics prevents walking through slab from east");
            side = Collide(walker, 10.5, 20, solidBounds.Z1 - 1, 0, 0, 3);
            Check(Math.Abs(side.Z - (solidBounds.Z1 - .25)) < .001 && walker.CollidedHorizontally, "native physics respects full slab depth");
            var landing = Collide(walker, 10.5, 23, 10, 0, -4, 0);
            Check(Math.Abs(landing.Y - (load.Pos.Y + load.CollisionBox.Y2)) < .001 && walker.CollidedVertically,
                "native physics lands on slab instead of falling through it");
            walker.Controls.NoClip = true; side = Collide(walker, 9, 20, 10, 2, 0, 0);
            Check(side.X > 10.9, "creative noclip remains available"); walker.Controls.NoClip = false;
            walker.Pos.Dimension = 1; side = Collide(walker, 9, 20, 10, 2, 0, 0);
            Check(side.X > 10.9, "slab collision does not cross dimensions"); walker.Pos.Dimension = 0;
            walker.EntityId = 999; walker.Alive = true; walker.Pos.SetPos(solidBounds.X2 + .6, 20, 10); entities.Add(walker.EntityId, walker);
            var heldPos = load.Pos.Copy(); side = Collide(load, 10.5, 20, 10, 2, 0, 0); load.Pos.SetFrom(heldPos);
            Check(side.X < heldPos.X + .36 && load.CollidedHorizontally, "moving slab is also stopped by stationary people");
            entities.Remove(walker.EntityId);

            load.OnEntityDespawn(new() { Reason = EnumDespawnReason.Unload });

            // Recreate the watched attributes written by the old release, then save and load
            // with the game's entity serializer before checking how a player can recover them.
            foreach (var wheelItem in wheels)
            foreach (var materialItem in new Item?[] { null, chain, rope })
            {
                ResetSlab();
                var oldLoad = new EntityQuarryDrag { Code = properties.Code };
                oldLoad.Pos.SetPos(10.5, 20, 10);
                oldLoad.WatchedAttributes.SetItemstack("slab", new ItemStack(nativeSlab) { Attributes = expectedContent.Attributes.Clone() });
                var oldWheels = new ItemStack(wheelItem, 4);
                oldWheels.Attributes.SetString("wheel-owner", "fixture");
                oldLoad.WatchedAttributes.SetItemstack("wheels", oldWheels);
                ItemStack? oldBinding = materialItem == null ? null : new ItemStack(materialItem);
                if (oldBinding != null)
                {
                    oldBinding.Attributes.SetInt("vschain-length", 12);
                    oldLoad.WatchedAttributes.SetItemstack("binding", oldBinding);
                }
                oldLoad.WatchedAttributes.SetString("holder", player.PlayerUID);
                using var stream = new MemoryStream();
                oldLoad.ToBytes(new BinaryWriter(stream), false); stream.Position = 0;
                var restored = new EntityQuarryDrag(); restored.FromBytes(new BinaryReader(stream), false);
                Spawn(restored); Set(0, controller);
                restored.OnGround = true;
                player.Pos.SetPos(12, 20, 10);
                Check(restored.HolderUid == "" && restored.CollisionBox.Y2 == load.CollisionBox.Y2
                    && restored.GetInfoText().Contains("vsscpmod:quarry-drag-legacy-recover"),
                    "old wheels load without an active holder or raised collision and show recovery help");
                player.Controls.RightMouseDown = true; player.Controls.Forward = true;
                restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                player.Pos.SetPos(14, 20, 10); restored.OnGameTick(.05f);
                Check(oldBinding == null
                    ? restored.HolderUid == "" && restored.Pos.Motion.X == 0
                    : restored.HolderUid == player.PlayerUID && restored.Pos.Motion.X > 0 && restored.Pos.Motion.Y == 0,
                    "legacy loads only move by pulling with a saved binding");
                player.Controls.RightMouseDown = false; player.Controls.Forward = false;
                player.Pos.SetPos(12, 20, 10); player.Controls.Sneak = true;
                map[target.Position.Copy()] = floor;
                restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(restored.Alive && received.Count == 0
                    && restored.WatchedAttributes.GetItemstack("wheels")?.StackSize == 4,
                    "blocked placement keeps the old slab and its materials in the world");
                map.Remove(target.Position);
                restored.OnInteract(player, new DummySlot(), new(), EnumInteractMode.Interact);
                Check(!restored.Alive && Get(controller) == nativeSlab && Get(target.Position) == proxy
                    && nativeSlab.OnPickBlock(world, controller).Attributes.Equals(world, expectedContent.Attributes)
                    && received.Count == (oldBinding == null ? 1 : 2)
                    && received.Any(item => item.Collectible == wheelItem && item.StackSize == 4 && item.Attributes.Equals(world, oldWheels.Attributes))
                    && (oldBinding == null || received.Any(item => item.Collectible == materialItem && item.StackSize == 1
                        && item.Attributes.Equals(world, oldBinding.Attributes))),
                    "placing a legacy slab restores rocks and refunds its exact wheel stack and binding: " + wheelItem.Code);
                player.Controls.Sneak = false;
            }

            // Verify the geometry of the remaining iron-chain renderer against game assets.
            var mods = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MVL/Modpack/测试服务器/Mods");
            {
                var tessType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.NoObf.ShapeTesselator", true)!;
                var tess = (ITesselatorAPI)Activator.CreateInstance(tessType, new object?[] { null, null, null, null })!;
                var textures = Proxy.Create<ITexPositionSource>((m, _) => m.Name switch {
                    "get_AtlasSize" => new Size2i(1024, 1024),
                    "get_Item" => new TextureAtlasPosition { x2 = 1, y2 = 1, atlasTextureId = 1 }, _ => null });
                Shape ReadShape(ZipArchive archive, string entry)
                {
                    using var input = new StreamReader(archive.GetEntry(entry)!.Open());
                    return JObject.Parse(input.ReadToEnd()).ToObject<Shape>()!;
                }
                using var chainZip = ZipFile.OpenRead(Directory.EnumerateFiles(mods, "VS Restraints-*.zip").Single());
                var chainShape = ReadShape(chainZip, "assets/vsrestraints/shapes/ironchain.json");
                var ring = chainShape.Elements.Single(e => e.Name == "Chain1");
                chainShape.Elements = [ring]; ring.From = ring.To = ring.RotationOrigin = [0, 0, 0];
                ring.RotationX = ring.RotationY = ring.RotationZ = 0;
                tess.TesselateShape("quarry-native-link-check", chainShape, out var template, textures);
                var center = Enumerable.Range(0, 3).Select(axis =>
                    (Enumerable.Range(0, template.VerticesCount).Min(i => template.xyz[3 * i + axis])
                    + Enumerable.Range(0, template.VerticesCount).Max(i => template.xyz[3 * i + axis])) / 2).ToArray();
                template.MatrixTransform(new Matrixf().Scale(.5f, .5f, .5f).Translate(-center[0], -center[1], -center[2]).Values);
                double clearance = (double)AccessTools.Method(typeof(QuarryDragRenderer), "ChainClearance").Invoke(null, [template])!;
                Check(clearance > .015 && clearance < .05, "chain seating uses the native half-scale link thickness");
                using var quarryZip = ZipFile.OpenRead(Directory.EnumerateFiles(mods, "Stone Quarry-*.zip").Single());
                var layoutType = typeof(QuarryDragRenderer).Assembly.GetType("SeraphCivilizationPatches.QuarrySlabLayout")!;
                int FaceCount(ShapeElement element) => (element.FacesResolved?.Count(f => f != null) ?? 0)
                    + (element.Children?.Sum(FaceCount) ?? 0);
                foreach (string size in new[] { "small", "medium", "large", "huge", "giant" })
                foreach (var (facing, degrees) in new[] { ("north", 0f), ("east", 270f), ("south", 180f), ("west", 90f) })
                {
                    var slabShape = ReadShape(quarryZip, $"assets/stonequarry/shapes/block/stoneslab/{size}.json");
                    int facesBefore = slabShape.Elements.Sum(FaceCount);
                    double width = size == "small" || size == "medium" && facing is "east" or "west" ? 1 : 2;
                    double depth = size == "small" || size == "medium" && facing is "north" or "south" ? 1 : 2;
                    double cx = size == "small" ? 0 : size == "medium" ? facing == "south" ? 1 : 0 : facing is "south" or "west" ? 1 : 0;
                    double cz = size == "small" ? 0 : size == "medium" ? facing == "west" ? 1 : 0 : facing is "north" or "west" ? 1 : 0;
                    var offset = new Vec3d(cx - width / 2, 0, cz - depth / 2);
                    var layout = AccessTools.Method(layoutType, "Measure").Invoke(null, [slabShape, new Vec3f(0, degrees, 0), offset, tess])!;
                    var stoneBounds = (Cuboidd)AccessTools.Property(layoutType, "Stone").GetValue(layout)!;
                    var segments = new List<(Vec3d From, Vec3d To)>();
                    AccessTools.Method(typeof(QuarryDragRenderer), "BuildWrapping").Invoke(null,
                        [stoneBounds, clearance, (Action<Vec3d, Vec3d>)((a, b) => segments.Add((a, b)))]);
                    Check(segments.Take(8).All(s => {
                        var midpoint = s.From.AddCopy(s.To).Mul(.5);
                        return Math.Abs(stoneBounds.ShortestDistanceFrom(midpoint) - clearance) < .001;
                    }), $"both chain loops sit against actual stone surfaces: {size}-{facing}");
                    Check(segments.Count == 44 && Enumerable.Range(0, 4).All(face => {
                        int start = 8 + face * 9;
                        bool OnLoopCorner(Vec3d point) => segments.Take(8).Any(s => s.From.DistanceTo(point) < .001 || s.To.DistanceTo(point) < .001);
                        return OnLoopCorner(segments[start + 1].From) && OnLoopCorner(segments[start + 8].To)
                            && stoneBounds.ShortestDistanceFrom(segments[start + 4].To) > clearance * 2;
                    }), $"crossed chains arch locally with both ends against the loops: {size}-{facing}");
                }
            }

            foreach (bool throws in new[] { false, true })
            {
                ResetSlab(); spawnFails = !throws; spawnThrows = throws;
                var held = new DummySlot(new ItemStack(chain));
                AccessTools.Method(fix, "Bind").Invoke(null, [player, held, new BlockPos(10, 20, 10)]);
                Check(held.StackSize == 1 && Get(new(10, 20, 10)) == nativeSlab && entities.Count == 0,
                    "spawn failure leaves native slab and binding untouched: throws=" + throws);
                spawnFails = spawnThrows = false;
            }
            ResetSlab(); loaded = false;
            var blocked = new DummySlot(new ItemStack(chain));
            AccessTools.Method(fix, "Bind").Invoke(null, [player, blocked, new BlockPos(10, 20, 10)]);
            Check(entities.Count == 0 && blocked.StackSize == 1, "unloaded footprint rejects conversion without consuming material");
            Console.WriteLine($"Quarry drag: {count} checks passed.");
        }
        finally
        {
            foreach (var e in entities.Values.OfType<EntityQuarryDrag>()) e.OnEntityDespawn(new() { Reason = EnumDespawnReason.Unload });
            player.Stats.Remove("walkspeed", "vsscpmod-quarrydrag");
            Online = true;
            player.World = oldWorld; player.Api = oldApi; player.Pos.SetFrom(oldPos); player.Controls.SetFrom(oldControls); player.Controls.WalkVector.Set(oldWalk);
            patcher.UnpatchAll(patcher.Id);
        }

        object? Walk(object?[] a)
        {
            var min = (BlockPos)a[0]!; var max = (BlockPos)a[1]!;
            var visit = (Action<Block, int, int, int>)a[2]!;
            for (int x = min.X; x <= max.X; x++)
            for (int y = min.Y; y <= max.Y; y++)
            for (int z = min.Z; z <= max.Z; z++) visit(Get(new(x, y, z, min.dimension)), x, y, z);
            return null;
        }
        Block Get(BlockPos pos) => map.GetValueOrDefault(pos) ?? (pos.Y == 19 ? floor : air);
        object? Set(int id, BlockPos pos)
        {
            Block previous = Get(pos);
            map[pos.Copy()] = id == 77 ? nativeSlab : id == 78 ? proxy : air;
            if (previous == nativeSlab && id == 0) { nativeSlab.OnBlockRemoved(world, pos); be = null; }
            if (id == 77)
            {
                be = (BlockEntity)Activator.CreateInstance(quarry.GetType("StoneQuarry.BEStoneSlab")!)!;
                be.Block = nativeSlab; be.Pos = pos.Copy(); be.Initialize(api);
                nativeSlab.OnBlockPlaced(world, pos);
            }
            return null;
        }
        void ResetSlab()
        {
            entities.Clear(); received.Clear(); map.Clear();
            Set(77, new(10, 20, 10));
            var content = makeSlab(64, 1);
            AccessTools.Method(be!.GetType(), "ContentFromAttributes").Invoke(be, [content.Attributes.Clone(), world]);
            expectedContent = nativeSlab.OnPickBlock(world, new(10, 20, 10)).Clone();
            player.Pos.SetPos(10.5, 20, 11); now += 100;
        }
        object? Spawn(EntityQuarryDrag entity)
        {
            if (spawnThrows) throw new InvalidOperationException("test spawn failure");
            if (spawnFails) return null;
            entity.EntityId = nextId++; entity.Initialize(properties, api, 0); entities.Add(entity.EntityId, entity);
            return null;
        }
    }
    private static ItemSlot Right = new DummySlot(), Left = new DummySlot();
    private static bool HandRight(ref ItemSlot __result) { __result = Right; return false; }
    private static bool HandLeft(ref ItemSlot __result) { __result = Left; return false; }
    private static List<ItemStack> Given = [];
    private static bool Online = true;
    private static bool Connection(ref EnumClientState __result) { __result = Online ? EnumClientState.Playing : EnumClientState.Offline; return false; }
    private static HarmonyMethod Hook(string method) => new(typeof(QuarryDragChecks), method);
    private static bool Skip() => false;
    private static bool Give(ItemStack itemstack, ref bool __result) { Given.Add(itemstack.Clone()); __result = true; return false; }
    private static bool Tick(Entity __instance) => __instance is not EntityQuarryDrag;
    private static bool Initialize(Entity __instance, EntityProperties properties, ICoreAPI api)
    {
        if (__instance is not EntityQuarryDrag) return true;
        __instance.Api = api; __instance.World = api.World;
        AccessTools.Property(typeof(Entity), "Properties").SetValue(__instance, properties);
        return false;
    }
}
