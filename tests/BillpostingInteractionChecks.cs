using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeraphCivilizationPatches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

static class BillpostingInteractionChecks
{
    // Real game player implements the internal IPlayer range member that
    // DispatchProxy cannot implement on 1.22.7.
    private sealed class Player : ServerPlayer, IServerPlayer
    {
        private Player() : base(null!, null!) { }
        public new EntityPlayer Entity { get; set; } = null!;
        public new IPlayerInventoryManager InventoryManager { get; set; } = null!;
        public new string PlayerUID => "billpost-reader";
        public bool Admin;
        public new bool HasPrivilege(string privilege) => Admin;
        internal static Player Create(EntityPlayer entity, IPlayerInventoryManager inventory)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            player.Entity = entity; player.InventoryManager = inventory; return player;
        }
    }

    private sealed class Actor : EntityPlayer
    {
        public bool Accept = true;
        public readonly List<ItemStack> Received = [];
        public override bool TryGiveItemStack(ItemStack stack)
        {
            if (!Accept) return false;
            Received.Add(stack.Clone()); return true;
        }
    }

    // Replace only host initialization/despawn services. Billposting's actual
    // placement, initialization and interaction bodies run with real ItemStacks.
    private static bool InitializeHost(Entity __instance, EntityProperties properties, ICoreAPI api)
    {
        __instance.Api = api; __instance.World = api.World;
        AccessTools.Property(typeof(Entity), "Properties").SetValue(__instance, properties);
        __instance.Alive = true; return false;
    }
    private static bool DespawnHost(Entity __instance) { __instance.Alive = false; return false; }

    public static void Run(string root, string dll)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(dll));
        var entityType = assembly.GetType("billposting.EntityBillpost", true)!;
        var behaviorType = assembly.GetType("billposting.BehaviorBillpostable", true)!;
        var modType = assembly.GetType("billposting.billpostingModSystem", true)!;
        var configType = assembly.GetType("billposting.BillpostingConfig", true)!;
        var settings = Activator.CreateInstance(configType)!;
        AccessTools.Field(modType, "config").SetValue(null, settings);
        var fixType = typeof(SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.BillpostingInteractionCompatibility", true)!;
        var create = AccessTools.Method(fixType, "TryCreate");
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        bool enabled = true, access = true, civilization = false;
        EnumAppSide side = EnumAppSide.Server;
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", a ?? [])); return null; });
        var slot = new DummySlot();
        var inventory = Proxy.Create<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : null);
        var actor = new Actor();
        var player = Player.Create(actor, inventory);
        actor.WatchedAttributes.SetString("playerUID", player.PlayerUID);
        actor.Controls.CtrlKey = true;
        var paper = new Item { ItemId = 321, Code = new AssetLocation("game:paper-parchment") };
        var behavior = (CollectibleBehavior)Activator.CreateInstance(behaviorType, [paper])!;
        behavior.Initialize(new JsonObject(JObject.Parse("{entityCode:'billposting:billpost',yOffset:-0.3}")));
        var props = new EntityProperties { Code = new AssetLocation("billposting:billpost"), Attributes = new JsonObject(new JObject()) };
        var claims = Proxy.Create<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? access : null);
        var registry = Proxy.Create<IClassRegistryAPI>((m, _) => m.Name == "CreateEntity" ? Activator.CreateInstance(entityType) : null);
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled"
            ? (string)a![0]! == "vsscsmod" ? civilization : enabled && (string)a[0]! == "billposting" : null);
        ICoreAPI api = null!;
        var spawned = new List<Entity>();
        string? spawnText = null;
        bool spawnHasStack = false;
        var world = Proxy.Create<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_Side" => side, "PlayerByUid" => player, "get_ClassRegistry" => registry,
            "get_Claims" => claims, "get_Logger" => logger, "GetEntityType" => props,
            "GetItem" => paper, "SpawnEntity" => Spawn((Entity)a![0]!), _ => null
        });
        object? Spawn(Entity entity)
        {
            spawned.Add(entity);
            spawnText = entity.WatchedAttributes.GetString("text", "");
            spawnHasStack = entity.WatchedAttributes.GetBytes("billpostdropstackbytes") != null;
            entity.Initialize(props, api, 0); return null;
        }
        api = Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => side, "get_ModLoader" => loader, "get_Logger" => logger, _ => null
        });
        actor.World = world; actor.Api = api;
        var selection = new BlockSelection { Position = new BlockPos(10, 20, 30), HitPosition = new Vec3d(.5, .5, 0), Face = BlockFacing.NORTH };
        void Fill()
        {
            slot.Itemstack = new ItemStack(paper, 2);
            slot.Itemstack.Attributes.SetString("text", "中文公告\n第二行 ABC");
            slot.Itemstack.Attributes.SetString("title", "测试公告");
            slot.Itemstack.Attributes.SetString("vscncsAuthorizationId", "preserve-contract");
        }
        void Place(bool first = true)
        {
            EnumHandHandling hand = EnumHandHandling.NotHandled;
            EnumHandling handling = EnumHandling.PassThrough;
            behavior.OnHeldInteractStart(slot, actor, selection, null!, first, ref hand, ref handling);
        }
        var harness = new Harmony("vsscpmod.tests.billposting-host");
        harness.Patch(AccessTools.DeclaredMethod(typeof(Entity), "Initialize"), prefix: new HarmonyMethod(typeof(BillpostingInteractionChecks), nameof(InitializeHost)));
        harness.Patch(AccessTools.DeclaredMethod(typeof(Entity), "Die"), prefix: new HarmonyMethod(typeof(BillpostingInteractionChecks), nameof(DespawnHost)));
        IDisposable? fix = null, second = null, contracts = null;
        try
        {
            Fill(); Place();
            Check(spawnText == "" && !spawnHasStack && (string)AccessTools.Field(entityType, "BillpostText").GetValue(spawned[^1])! == "",
                "original placement initializes blank text and spawns before recovery data exists");
            slot.Itemstack = null; actor.Accept = false;
            spawned[^1].OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(!spawned[^1].Alive && actor.Received.Count == 0, "original pickup deletes parchment even when inventory rejects it");
            enabled = false;
            Check(create.Invoke(null, [api]) == null, "absent Billposting leaves runtime untouched");
            enabled = true;
            fix = (IDisposable?)create.Invoke(null, [api]);
            Check(fix != null && !messages.Any(m => m.Contains("unavailable")), "patch installs on actual Billposting 2.0.3");
            Fill(); Place();
            var posted = spawned[^1];
            Check(spawnText == "中文公告\n第二行 ABC" && spawnHasStack && (string)AccessTools.Field(entityType, "BillpostText").GetValue(posted)! == spawnText,
                "initialization and first spawn receive full text and serialized parchment");
            Check(slot.StackSize == 1, "server consumes exactly one sheet");
            Check(posted.SelectionBox.Contains(0, -.3, 0) && posted.SelectionBox.Contains(.22, .3, 0), "selection includes lower and upper paper corners");
            CheckPaperMesh(root, dll, posted.SelectionBox, Check);
            posted.WatchedAttributes.SetString("text", "更新内容");
            posted.WatchedAttributes.SetString("title", "更新标题");
            Check((string)AccessTools.Field(entityType, "BillpostText").GetValue(posted)! == "更新内容"
                && (string)AccessTools.Field(entityType, "BillpostName").GetValue(posted)! == "更新标题", "watched text and title changes refresh cached display");
            int before = spawned.Count;
            side = EnumAppSide.Client; Fill(); Place();
            Check(spawned.Count == before && slot.StackSize == 2, "client placement creates no ghost and consumes no inventory");
            slot.Itemstack = null;
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(posted.Alive && actor.Received.Count == 0, "client pickup waits for server authority");
            side = EnumAppSide.Server; Fill(); Place(false);
            Check(spawned.Count == before && slot.StackSize == 2, "repeat held event does not duplicate placement");
            access = false; Place();
            Check(spawned.Count == before && slot.StackSize == 2, "claim denial retains held parchment");
            access = true;
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(posted.Alive, "nonempty hand does not take parchment");
            slot.Itemstack = null;
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Attack);
            Check(posted.Alive, "attack does not silently take parchment");
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(posted.Alive, "failed inventory transfer retains paper on wall");
            actor.Accept = true; access = false;
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(posted.Alive && actor.Received.Count == 0, "claim denial prevents pickup");
            access = true;
            posted.WatchedAttributes.SetString("signedby", "Owner");
            posted.WatchedAttributes.SetString("signedbyuid", "another-player");
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(posted.Alive && actor.Received.Count == 0, "signed paper remains locked to other players");
            posted.WatchedAttributes.SetString("signedbyuid", player.PlayerUID);
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(!posted.Alive && actor.Received.Count == 1 && actor.Received[0].StackSize == 1
                && actor.Received[0].Attributes.GetString("vscncsAuthorizationId") == "preserve-contract"
                && actor.Received[0].Attributes.GetString("text") == "中文公告\n第二行 ABC", "owner receives exact original parchment including contract metadata");
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(actor.Received.Count == 1, "repeated pickup cannot duplicate parchment");
            Fill(); Place(); posted = spawned[^1]; slot.Itemstack = null;
            posted.WatchedAttributes.SetBytes("billpostdropstackbytes", [1, 2, 3]);
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(posted.Alive && actor.Received.Count == 1, "corrupt recovery bytes retain entity without crashing");
            posted.WatchedAttributes.RemoveAttribute("billpostdropstackbytes");
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(!posted.Alive && actor.Received.Count == 2, "legacy itemstack attribute can recover a missing byte snapshot");
            Fill(); Place(); posted = spawned[^1]; slot.Itemstack = null;
            posted.WatchedAttributes.SetString("signedby", "Owner");
            posted.WatchedAttributes.SetString("signedbyuid", "another-player");
            player.Admin = true;
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(!posted.Alive && actor.Received.Count == 3, "administrator retains signed-paper pickup privilege");
            player.Admin = false;
            Fill(); Place(); posted = spawned[^1]; slot.Itemstack = null;
            posted.WatchedAttributes.SetString("signedby", "Owner");
            posted.WatchedAttributes.SetString("signedbyuid", "another-player");
            AccessTools.Field(configType, "LockSignedBillboards").SetValue(settings, false);
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(!posted.Alive && actor.Received.Count == 4, "disabled signature locking retains upstream behavior");
            AccessTools.Field(configType, "LockSignedBillboards").SetValue(settings, true);

            string civilizationDll = Path.Combine(root, "tests/dependencies/billposting-civilization/vsscsmod.dll");
            var civilizationAssembly = Assembly.LoadFrom(Path.GetFullPath(civilizationDll));
            var contractType = civilizationAssembly.GetType("SeraphCivilizationSimulation.ParchmentBookCompatibility", true)!;
            contracts = (IDisposable)Activator.CreateInstance(contractType, true)!;
            civilization = true;
            Fill(); Place(); posted = spawned[^1]; slot.Itemstack = null;
            posted.WatchedAttributes.SetString("text", "文明契约更新");
            Check((string)AccessTools.Field(entityType, "BillpostText").GetValue(posted)! == "文明契约更新",
                "actual installed civilization contract listener continues updating display");
            var latest = new ItemStack(posted.WatchedAttributes.GetBytes("billpostdropstackbytes"));
            latest.Attributes.SetString("text", "文明契约更新");
            posted.WatchedAttributes.SetBytes("billpostdropstackbytes", latest.ToBytes());
            posted.OnInteract(actor, slot, new Vec3d(), EnumInteractMode.Interact);
            Check(!posted.Alive && actor.Received[^1].Attributes.GetString("text") == "文明契约更新"
                && actor.Received[^1].Attributes.GetString("vscncsAuthorizationId") == "preserve-contract",
                "pickup with actual civilization hooks reads current serialized contract data");
            contracts.Dispose(); contracts = null; civilization = false;
            second = (IDisposable?)create.Invoke(null, [api]); fix!.Dispose();
            Check(Harmony.GetPatchInfo(AccessTools.Method(entityType, "OnInteract"))!.Owners.Contains("vsscpmod.billposting-interaction"), "one side disposing leaves other side's patches active");
            second!.Dispose();
            Check(Harmony.GetPatchInfo(AccessTools.Method(entityType, "OnInteract"))?.Owners.Contains("vsscpmod.billposting-interaction") != true, "last owner removes patches");
            Console.WriteLine($"{checks} Billposting interaction checks passed.");
        }
        finally { contracts?.Dispose(); second?.Dispose(); fix?.Dispose(); harness.UnpatchAll(harness.Id); }
    }

    private static void CheckPaperMesh(string root, string dll, Cuboidf box, Action<bool, string> check)
    {
        var shapeJson = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(dll)!, "assets/billposting/shapes/billpost.json")));
        check(((string)shapeJson["textures"]!["billpost"]!).StartsWith("C:/Users/"), "original paper shape contains author's missing absolute texture path");
        var patch = JArray.Parse(File.ReadAllText(Path.Combine(root, "assets/vsscpmod/patches/compatibility/billposting-texture.json")));
        shapeJson["textures"]!["billpost"] = patch[0]!["value"]!.DeepClone();
        check((string)shapeJson["textures"]!["billpost"]! == "billposting:billpost"
            && File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "assets/billposting/textures/billpost.png")), "replacement texture resolves to packaged parchment image");
        var tessellatorType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Client.NoObf.ShapeTesselator", true)!;
        var tessellator = (ITesselatorAPI)Activator.CreateInstance(tessellatorType, new object?[] { null, null, null, null })!;
        var source = Proxy.Create<ITexPositionSource>((m, _) => m.Name switch
        {
            "get_AtlasSize" => new Size2i(1024, 1024),
            "get_Item" => new TextureAtlasPosition { x2 = 1, y2 = 1, atlasTextureId = 1 }, _ => null
        });
        tessellator.TesselateShape("billpost-check", shapeJson.ToObject<Shape>()!, out var mesh, source);
        check(mesh.VerticesCount > 0, "real tessellator produces paper mesh");
        bool covered = true, oldMisses = false;
        for (int i = 0; i < mesh.VerticesCount; i++)
        {
            float x = (mesh.xyz[i * 3] - .5f) * .75f;
            float y = mesh.xyz[i * 3 + 1] * .75f;
            float z = (mesh.xyz[i * 3 + 2] - .5f) * .75f;
            oldMisses |= y < 0 || y > .3 || Math.Abs(x) > .15 || Math.Abs(z) > .15;
            foreach (var p in new[] { new Vec3f(x, y, z), new Vec3f(z, y, -x), new Vec3f(-x, y, -z), new Vec3f(-z, y, x) })
                covered &= box.Contains(p.X, p.Y, p.Z);
        }
        check(oldMisses && covered, "all four wall orientations: actual paper vertices fit corrected selection, not original box");
    }
}
