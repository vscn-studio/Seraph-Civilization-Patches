using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

// Execute the actual Logging methods; only world services, player access and
// GPU-backed bar registration are substituted. No Logging reference in the mod.
static class LoggingProgressChecks
{
    private static readonly HashSet<IProgressBar> bars = [];
    private static IPlayerInventoryManager inventoryManager = null!;
    private static EntityPlayer entity = null!;

    public static void Run(string game, string modDirectory)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(modDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        var logging = Assembly.LoadFrom(Path.Combine(modDirectory, "LoggingMod.dll"));
        var type = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.LoggingProgressCompatibility", true)!;
        var create = AccessTools.Method(type, "TryCreate");
        var harmony = new Harmony("vsscpmod.tests.logging-progress");
        void Patch(MethodInfo method, string name) => harmony.Patch(method,
            prefix: new HarmonyMethod(typeof(LoggingProgressChecks), name));
        Patch(AccessTools.Method(typeof(ModSystemProgressBar), "AddProgressbar"), nameof(Add));
        Patch(AccessTools.Method(typeof(ModSystemProgressBar), "RemoveProgressbar"), nameof(Remove));
        var playerType = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"))
            .GetType("Vintagestory.Client.NoObf.ClientPlayer", true)!;
        var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(playerType);
        Patch(AccessTools.PropertyGetter(playerType, "InventoryManager"), nameof(Inventory));
        Patch(AccessTools.PropertyGetter(playerType, "Entity"), nameof(Entity));
        Patch(AccessTools.Method(playerType, "TriggerFpAnimation"), nameof(Skip));
        entity = new EntityPlayer();
        var hands = new InventoryGeneric(2, "loggingprogress", "player", null);
        int selectedSlot = 0;
        inventoryManager = Proxy.Create<IPlayerInventoryManager>((m, _) =>
            m.Name == "get_ActiveHotbarSlot" ? hands[selectedSlot] : null);
        var trunk = (Block)Activator.CreateInstance(logging.GetType("LoggingMod.BlockTreeTrunk", true)!)!;
        var air = new Block { Code = new("game:air"), BlockBehaviors = [] };
        var replacement = new Block { Code = new("game:stone-granite"), BlockBehaviors = [] };
        var be = (BlockEntity)Activator.CreateInstance(logging.GetType("LoggingMod.BETreeTrunk", true)!)!;
        var inventory = (InventoryBase)Activator.CreateInstance(logging.GetType("LoggingMod.TreeTrunkInventory", true)!, [null, null])!;
        be.GetType().GetProperty("Inventory")!.SetValue(be, inventory);
        inventory[0].Itemstack = new ItemStack(new Block { Code = new("game:log-placed-oak-ud") }, 10);
        var selection = new BlockSelection { Position = new BlockPos(0, 100, 0) };
        Block target = trunk;
        BlockEntity? targetEntity = be;
        EnumAppSide side = EnumAppSide.Client;
        ICoreClientAPI api = null!;
        IClientPlayer? localPlayer = player;
        var warnings = new List<string>();
        var callbacks = new Dictionary<long, Action<float>>();
        long listenerId = 0;
        var events = Proxy.Create<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "RegisterGameTickListener") { callbacks.Add(++listenerId, (Action<float>)a![0]!); return listenerId; }
            if (m.Name == "UnregisterGameTickListener") callbacks.Remove((long)a![0]!);
            return null;
        });
        var mouse = new MouseButtonState { Right = true };
        var input = Proxy.Create<IInputAPI>((m, _) => m.Name == "get_InWorldMouseButton" ? mouse : null);
        var system = new ModSystemProgressBar();
        bool enabled = true;
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name switch
        { "IsModEnabled" => enabled, "GetModSystem" => system, _ => null });
        var logger = Proxy.Create<ILogger>((m, a) =>
        { if (m.Name is "Warning" or "Error") warnings.Add(string.Join(" ", a ?? [])); return null; });
        var accessor = Proxy.Create<IBlockAccessor>((m, _) => m.Name switch
        { "GetBlock" => target, "GetBlockEntity" => targetEntity, _ => null });
        var world = Proxy.Create<IClientWorldAccessor>((m, _) => m.Name switch
        { "get_Side" => side, "get_Player" => localPlayer, "get_Api" => api, "get_BlockAccessor" => accessor, _ => null });
        api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_ModLoader" => loader, "get_Logger" => logger, "get_Event" => events, "get_Input" => input, _ => null });
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name + "\n" + string.Join("\n", warnings)); count++; Console.WriteLine("PASS: " + name); }
        void Tool(EnumTool tool) => hands[selectedSlot].Itemstack = new ItemStack(new Item { Code = new("probe:" + tool), Tool = tool });
        void Tick() { foreach (var callback in callbacks.Values.ToArray()) callback(.05f); }
        void Cancel() => trunk.OnBlockInteractCancel(.3f, world, player, selection, EnumItemUseCancelReason.ReleasedMouse);
        void Begin(EnumTool tool = EnumTool.Axe)
        {
            side = EnumAppSide.Client; localPlayer = player; target = trunk; targetEntity = be;
            mouse.Right = true; selectedSlot = 0; Tool(tool);
            entity.Controls.HandUse = EnumHandInteract.BlockInteract;
            entity.Controls.HandUsingBlockSel = selection.Clone();
            Check(trunk.OnBlockInteractStep(.3f, world, player, selection), tool + " continues before duration");
        }
        IDisposable? fix = null;
        try
        {
            Begin();
            Check(!trunk.OnBlockInteractStep(.8f, world, player, selection) && bars.Count == 1,
                "original final step strands bar until Stop is dispatched");
            target = air; targetEntity = null;
            target.OnBlockInteractStop(.8f, world, player, selection);
            Check(bars.Count == 1, "original callback on air does not clear Logging bar");
            Cancel();
            Begin(); Tool(EnumTool.Saw); trunk.OnBlockInteractStop(.8f, world, player, selection);
            Check(bars.Count == 1, "original tool switch clears wrong helper"); Cancel();

            enabled = false;
            Check(create.Invoke(null, [api]) == null && callbacks.Count == 0, "Logging absent skips hooks and listener");
            enabled = true;
            fix = (IDisposable?)create.Invoke(null, [api]);
            Check(fix != null && callbacks.Count == 1, "client compatibility installs against real Logging DLL");
            var unrelated = system.AddProgressbar();
            Begin(); Tick();
            Check(bars.Count == 2, "ongoing harvest and unrelated mod bar both remain visible");
            Check(!trunk.OnBlockInteractStep(.8f, world, player, selection) && bars.Count == 1,
                "final step immediately removes only Logging bar, preserving false result");
            trunk.OnBlockInteractStop(.8f, world, player, selection);
            Check(bars.Count == 1 && inventory[0].Itemstack!.StackSize == 10, "client stop is idempotent and consumes no logs");

            Begin(); mouse.Right = false; Tick(); Check(bars.Count == 1, "mouse release clears without block callback");
            Begin(); entity.Controls.HandUse = EnumHandInteract.None; Tick(); Check(bars.Count == 1, "ended hand use clears without block callback");
            Begin(); target = air; targetEntity = null; Tick(); Check(bars.Count == 1, "removed trunk clears without block callback");
            Begin(); target = replacement; Tick(); Check(bars.Count == 1, "replaced target clears");
            Begin(); targetEntity = null; Tick(); Check(bars.Count == 1, "missing block entity clears");
            Begin(); Tool(EnumTool.Saw); trunk.OnBlockInteractStop(.8f, world, player, selection); Tick();
            Check(bars.Count == 1, "axe to saw switch before Stop clears old axe bar");
            Begin(); hands[1].Itemstack = hands[0].Itemstack!.Clone(); selectedSlot = 1; Tick();
            Check(bars.Count == 1, "switch to identical tool in another hotbar slot clears");
            Begin(); hands[0].Itemstack = null; Tick(); Check(bars.Count == 1, "broken or removed tool clears");
            Begin(); entity.Controls.HandUsingBlockSel = null; Tick(); Check(bars.Count == 1, "lost hand selection clears");
            Begin(); entity.Controls.HandUsingBlockSel!.Position.X++; Tick(); Check(bars.Count == 1, "changed hand target clears");
            Begin(); localPlayer = null; Tick(); Check(bars.Count == 1, "disconnect clears");

            foreach (var tool in new[] { EnumTool.Axe, EnumTool.Saw, EnumTool.Knife, EnumTool.Shears })
            {
                be.GetType().GetProperty("BranchCount")!.SetValue(be, 24);
                Begin(tool); Cancel(); Tick(); Check(bars.Count == 1, tool + " normal cancel remains idempotent");
                Begin(tool);
                Check(!trunk.OnBlockInteractStep(2.1f, world, player, selection) && bars.Count == 1,
                    tool + " completed step clears without Stop");
            }

            Begin();
            // BlockMultiblock forwards an adjusted master selection while the
            // player's hand selection still points to the clicked filler block.
            entity.Controls.HandUsingBlockSel!.Position.X++;
            trunk.OnBlockInteractStep(.4f, world, player, selection); Tick();
            Check(bars.Count == 2, "offset multiblock hand selection keeps active bar"); Cancel();

            var axe = AccessTools.Field(trunk.GetType(), "_axeHarvest").GetValue(null)!;
            side = EnumAppSide.Server;
            Check(AccessTools.Method(axe.GetType(), "OnStep").Invoke(axe, [world, player, selection, .8f]) is false
                && bars.Count == 1, "server final step retains result and creates no renderer");
            Check(AccessTools.Method(axe.GetType(), "TryComplete").Invoke(axe, [world, player, selection, .8f]) is true,
                "server completion still authorizes original harvest settlement");
            Check(AccessTools.Method(axe.GetType(), "TryComplete").Invoke(axe, [world, player, selection, .3f]) is false,
                "early server stop still refuses harvest settlement");

            Begin(); side = EnumAppSide.Server; Cancel(); side = EnumAppSide.Client;
            mouse.Right = false; Tick();
            Check(bars.Count == 1, "server callback in shared singleplayer process cannot forget client bar");
            Begin();
            var old = fix;
            fix = (IDisposable?)create.Invoke(null, [api]); old!.Dispose();
            Check(fix != null && bars.Count == 1 && callbacks.Count == 1, "replacement disposes active bar and old listener once");
            Begin(); Tick(); Check(bars.Count == 2, "late old disposal leaves replacement hooks active");
            fix!.Dispose(); fix.Dispose(); fix = null;
            Check(bars.Count == 1 && callbacks.Count == 0, "dispose removes owned bar and listener, preserving unrelated bar");
            Check(Harmony.GetPatchInfo(AccessTools.Method(logging.GetType("LoggingMod.HarvestHoldInteraction"), "OnStep"))?
                .Owners.Contains("vsscpmod.logging-progress") != true, "dispose removes compatibility hooks");
            Check(warnings.Count == 0, "no compatibility warnings");
            system.RemoveProgressbar(unrelated);
            Console.WriteLine($"{count} Logging progress checks passed.");
        }
        finally { fix?.Dispose(); harmony.UnpatchAll(harmony.Id); bars.Clear(); }
    }

    private sealed class Bar : IProgressBar { public float Progress { get; set; } }
    private static bool Add(ref IProgressBar __result) { __result = new Bar(); bars.Add(__result); return false; }
    private static bool Remove(IProgressBar pbr) { bars.Remove(pbr); return false; }
    private static bool Inventory(ref IPlayerInventoryManager __result) { __result = inventoryManager; return false; }
    private static bool Entity(ref EntityPlayer __result) { __result = entity; return false; }
    private static bool Skip() => false;
}
