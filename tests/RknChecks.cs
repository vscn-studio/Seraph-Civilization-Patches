using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

static class RknChecks
{
    private static readonly Dictionary<IPlayer, IPlayerInventoryManager> managers = [];
    private static FieldInfo parameters = null!;
    private static int serverStops, clientStops;

    public static void Run(string root, string rknPath)
    {
        int count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        Assembly rkn = Assembly.LoadFrom(Path.GetFullPath(rknPath));
        Type surface = rkn.GetType("RKN.Crafting.Entities.BlockEntityCraftingSurface", true)!;
        Type unfinished = rkn.GetType("RknCrafting.Entities.ItemUnfinishedCraft", true)!;
        Type builtin = rkn.GetType("RknCrafting.Entities.GridCountRenderer", true)!;
        Check(rkn.GetType("RKN.Crafting.ItemUnfinishedCraft") == null,
            "installed RKN reproduces the old tooltip type lookup failure");
        Assembly fix = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly;
        Type compatibility = fix.GetType("SeraphCivilizationPatches.RknCraftingCompatibility", true)!;
        Type replacementType = fix.GetType("SeraphCivilizationPatches.CraftingCountRenderer", true)!;
        Type floating = fix.GetType("SeraphCivilizationPatches.RknFloatingCountCompatibility", true)!;
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", a ?? [])); return null; });
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? true : null);
        var world = Proxy.Create<IWorldAccessor>((m, _) => m.Name == "get_Side" ? EnumAppSide.Client : null);
        ICoreAPI Api(EnumAppSide side) => Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_Side" => side, "get_ModLoader" => loader, "get_Logger" => logger, "get_World" => world, _ => null
        });
        var serverApi = Api(EnumAppSide.Server);
        var clientApi = Api(EnumAppSide.Client);
        IDisposable? server = null, client = null, newerClient = null, suppression = null, newerSuppression = null;
        var harness = new Harmony("vsscpmod.tests.rkn");
        MethodInfo Hook(string name) => typeof(RknChecks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo start = AccessTools.Method(surface, "StartCrafting");
        MethodInfo step = AccessTools.Method(surface, "OnCraftingStep");
        MethodInfo render = AccessTools.Method(builtin, "OnRenderFrame");
        RknDurabilityChecks.Run(rkn, compatibility, false, Check);
        try
        {
            server = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [serverApi]);
            client = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [clientApi]);
            Check(server != null && client != null, "real RKN compatibility installs on server and client");
            Check(!messages.Any(m => m.Contains("compatibility unavailable")), "no compatibility loading warning");
            foreach (string name in new[] { "StartCrafting", "ClientStartedCrafting", "OnTick", "OnCraftingStep" })
                Check(Harmony.GetPatchInfo(AccessTools.Method(surface, name))?.Owners.Contains("vsscpmod.rkn-crafting") == true,
                    "real RKN " + name + " is patched");
            Check(Harmony.GetPatchInfo(AccessTools.Method(unfinished, "GetHeldItemInfo"))?.Owners.Contains("vsscpmod.rkn-crafting") == true,
                "actual unfinished item tooltip is patched");
            Check(Harmony.GetPatchInfo(start)!.Postfixes.Count(p => p.owner == "vsscpmod.rkn-crafting") == 1,
                "integrated server and client share one patch installation");
            RknDurabilityChecks.Run(rkn, compatibility, true, Check);

            parameters = AccessTools.Field(surface, "craftingParams");
            Type ownerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Server.ServerPlayer", true)!;
            harness.Patch(AccessTools.PropertyGetter(ownerType, "InventoryManager"), prefix: new HarmonyMethod(Hook(nameof(Inventory))));
            RknDurabilityChecks.CheckCommand(compatibility, server!, manager =>
            {
                var owner = (IPlayer)RuntimeHelpers.GetUninitializedObject(ownerType);
                managers[owner] = manager;
                return owner;
            }, Check);
            harness.Patch(start, prefix: new HarmonyMethod(Hook(nameof(Start))));
            harness.Patch(AccessTools.Method(surface, "ServerStopCrafting"), prefix: new HarmonyMethod(Hook(nameof(ServerStop))));
            harness.Patch(AccessTools.Method(surface, "CancelCrafting"), prefix: new HarmonyMethod(Hook(nameof(ClientStop))));
            foreach (var api in new[] { serverApi, clientApi })
            {
                var owner = (IPlayer)RuntimeHelpers.GetUninitializedObject(ownerType);
                var inventory = new InventoryGeneric(2, "rknprobe", Guid.NewGuid().ToString(), null);
                int selected = 0;
                managers[owner] = Proxy.Create<IPlayerInventoryManager>((m, _) =>
                    m.Name == "get_ActiveHotbarSlot" ? inventory[selected] : null);
                var entity = (BlockEntity)RuntimeHelpers.GetUninitializedObject(surface);
                entity.Api = api;
                Check(start.Invoke(entity, [owner]) is true, "crafting start records " + api.Side + " snapshot immediately");
                selected = 1;
                Check(step.Invoke(entity, [1f, owner]) is false, "slot change stops " + api.Side + " crafting before the first tick");
            }
            Check(serverStops == 1 && clientStops == 1, "server broadcasts stop and client cancels locally using the correct side");

            var item = (Item)Activator.CreateInstance(unfinished)!;
            var stack = new ItemStack(item);
            var tools = new TreeAttribute();
            tools.SetString("0", "missing:removed-tool");
            stack.Attributes["usedTools"] = tools;
            var description = new StringBuilder();
            item.GetHeldItemInfo(new DummySlot(stack), description, world, false);
            Check(description.ToString().Contains("unavailable"), "missing tool tooltip degrades without crashing");

            newerClient = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [clientApi]);
            client!.Dispose();
            server!.Dispose();
            Check(Harmony.GetPatchInfo(start)?.Owners.Contains("vsscpmod.rkn-crafting") == true,
                "disposing old/client or server instances keeps live client patches");
            newerClient!.Dispose();
            Check(Harmony.GetPatchInfo(start)?.Owners.Contains("vsscpmod.rkn-crafting") != true,
                "last compatibility instance removes patches");
            Check(Harmony.GetPatchInfo(AccessTools.Method(rkn.GetType("RKN.Crafting.RecipeService"), "GetIngredientGridWithTools"))
                ?.Owners.Contains("vsscpmod.rkn-crafting") != true, "last instance removes the durability patch");

            var events = Proxy.Create<IClientEventAPI>((_, _) => null);
            var clientRenderApi = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
            {
                "get_Logger" => logger, "get_Event" => events, _ => null
            });
            var replacement = (IDisposable)Activator.CreateInstance(replacementType, [clientRenderApi])!;
            suppression = (IDisposable?)floating.GetMethod("TryCreate")!.Invoke(null, [clientRenderApi, replacement]);
            Check(suppression != null, "RKN built-in floating count suppression installs independently");
            var builtinProbe = RuntimeHelpers.GetUninitializedObject(builtin);
            // Without the patch this object has no API and the real renderer throws.
            render.Invoke(builtinProbe, [0f, EnumRenderStage.Ortho]);
            Check(true, "real built-in renderer does not run while Seraph counts are active");
            AccessTools.Field(replacementType, "failed").SetValue(replacement, true);
            bool resumed = false;
            try { render.Invoke(builtinProbe, [0f, EnumRenderStage.Ortho]); }
            catch (TargetInvocationException e) when (e.InnerException is NullReferenceException) { resumed = true; }
            Check(resumed, "built-in renderer resumes if Seraph rendering fails");
            AccessTools.Field(replacementType, "failed").SetValue(replacement, false);
            newerSuppression = (IDisposable?)floating.GetMethod("TryCreate")!.Invoke(null, [clientRenderApi, replacement]);
            suppression!.Dispose();
            render.Invoke(builtinProbe, [0f, EnumRenderStage.Ortho]);
            Check(true, "disposing old suppression leaves the replacement active");
            replacement.Dispose();
            Check(floating.GetMethod("RenderBuiltin", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null) is true,
                "disposed Seraph renderer permits built-in rendering");
            newerSuppression!.Dispose();
            Check(Harmony.GetPatchInfo(render)?.Owners.Contains("vsscpmod.rkn-floating-counts") != true,
                "floating count cleanup restores RKN renderer");
            Console.WriteLine($"{count} RKN checks passed.");
        }
        finally
        {
            newerSuppression?.Dispose(); suppression?.Dispose(); newerClient?.Dispose(); client?.Dispose(); server?.Dispose();
            harness.UnpatchAll(harness.Id);
            managers.Clear();
        }
    }

    private static bool Inventory(IPlayer __instance, ref IPlayerInventoryManager __result)
    {
        if (!managers.TryGetValue(__instance, out var manager)) return true;
        __result = manager; return false;
    }
    private static bool Start(object __instance, IPlayer byPlayer, ref bool __result)
    {
        object state = RuntimeHelpers.GetUninitializedObject(parameters.FieldType);
        AccessTools.Field(parameters.FieldType, "Player").SetValue(state, byPlayer);
        parameters.SetValue(__instance, state);
        __result = true; return false;
    }
    private static bool ServerStop(object __instance) { serverStops++; parameters.SetValue(__instance, null); return false; }
    private static bool ClientStop(object __instance) { clientStops++; parameters.SetValue(__instance, null); return false; }
}
