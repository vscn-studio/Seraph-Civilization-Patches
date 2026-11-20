using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

// Use the user's real Logging and CarryOn assemblies without adding them to
// either the repository or the release package. World/network effects are
// substituted; inventory serialization, placement, pickup and unlocking run
// through the actual mod methods and Harmony hooks.
static class LoggingChecks
{
    private static EntityPlayer player = null!;
    private static IPlayerInventoryManager inventoryManager = null!;
    private static InventoryGeneric hands = null!;
    private static object? capturedSpawn;
    private static bool allowSpawn = true;

    public static void Run(string root, string modDirectory)
    {
        modDirectory = Path.GetFullPath(modDirectory);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string file = Path.Combine(modDirectory, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        };
        Assembly.LoadFrom(Path.Combine(modDirectory, "CarryOnLib.dll"));
        Assembly logging = Assembly.LoadFrom(Path.Combine(modDirectory, "LoggingMod.dll"));
        Assembly carryOn = Assembly.LoadFrom(Path.Combine(modDirectory, "CarryOn.dll"));
        Type T(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);
        Type compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.LoggingCarryOnCompatibility", true)!;
        Type carriedType = T("CarryOn.API.Common.Models.CarriedBlock");
        Type slotType = T("CarryOn.API.Common.Models.CarrySlot");
        Type placementType = T("CarryOn.Common.Services.CarryPlacementService");
        Type pickupType = T("CarryOn.Common.Services.CarryPickupService");
        Type managerType = T("CarryOn.Common.Services.CarryManager");
        Type stateType = T("CarryOn.Common.Services.CarryStateService");
        Type serviceType = T("CarryOn.Server.Logic.CarriedBlockEntityService");
        object slot = Enum.Parse(slotType, "Hands");
        var harmony = new Harmony("vsscpmod.tests.logging");
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", (a ?? []).Select(v => v is object[] values ? string.Join(" ", values) : v))); return null; });
        int count = 0;
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((m, a) => m.Name switch
        {
            "HasTranslation" => true, "Get" => a![0], _ => null
        });
        Lang.ChangeLanguage("en");
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name + "\n" + string.Join("\n", messages));
            count++;
            Console.WriteLine("PASS: " + name);
        }
        bool loggingEnabled = true, carryEnabled = true, placed = false;
        EnumAppSide side = EnumAppSide.Server;
        int removals = 0;
        var air = new Block { BlockId = 0, Code = new AssetLocation("game:air"), Replaceable = 10000 };
        var logs = new Block { BlockId = 1, Code = new AssetLocation("game:log-placed-ebony-ud") };
        var trunk = (Block)Activator.CreateInstance(logging.GetType("LoggingMod.BlockTreeTrunk", true)!)!;
        trunk.BlockId = 2;
        trunk.Code = new AssetLocation("loggingmod:treetrunk-ebony-xxl-yes-north");
        trunk.Variant = new(new Dictionary<string, string> { ["size"] = "xxl", ["branches"] = "yes", ["side"] = "north" });
        trunk.Attributes = JsonObject.FromJson("{}");
        var position = new BlockPos(10, 100, 20);
        var be = (BlockEntity)Activator.CreateInstance(logging.GetType("LoggingMod.BETreeTrunk", true)!)!;
        be.Pos = position.Copy();
        AccessTools.Property(typeof(BlockEntity), "Block").SetValue(be, trunk);
        Type ownerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Server.ServerPlayer", true)!;
        var owner = (IServerPlayer)RuntimeHelpers.GetUninitializedObject(ownerType);
        ICoreAPI api = null!;
        object? carrySystem = null;
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name switch
        {
            "IsModEnabled" => a![0] as string == "loggingmod" ? loggingEnabled : carryEnabled,
            "GetModSystem" => a?.FirstOrDefault() as string == "CarryOn.CarrySystem"
                || m.IsGenericMethod && m.GetGenericArguments()[0].FullName == "CarryOn.CarrySystem" ? carrySystem : null,
            _ => null
        });
        var accessor = Proxy.Create<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetBlock" => placed && a![0] is BlockPos p && p.Equals(position) ? trunk : air,
            "GetBlockEntity" => be,
            "IsValidPos" => true,
            "SetBlock" => SetBlock((int)a![0]!),
            _ => null
        });
        object? SetBlock(int id)
        {
            placed = id != 0;
            if (id == 0) removals++;
            return null;
        }
        var world = Proxy.Create<IServerWorldAccessor>((m, a) => m.Name switch
        {
            "get_Api" => api, "get_Side" => side, "get_BlockAccessor" => accessor,
            "get_Logger" => logger, "PlayerByUid" => owner,
            "get_Claims" => Proxy.Create<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? true : null),
            "GetBlock" => a![0] is int id ? id == 1 ? logs : id == 2 ? trunk : air
                : a![0] is AssetLocation code && code.Domain == "loggingmod" ? trunk : air,
            _ => null
        });
        var registry = Proxy.Create<IClassRegistryAPI>((_, _) => null);
        api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => side, "get_ModLoader" => loader, "get_Logger" => logger, "get_ClassRegistry" => registry,
            _ => null
        });
        var serverApi = Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => EnumAppSide.Server, "get_ModLoader" => loader, "get_Logger" => logger,
            _ => null
        });
        hands = new InventoryGeneric(2, "loggingprobe", "probe", null);
        inventoryManager = Proxy.Create<IPlayerInventoryManager>((m, _) => m.Name switch
        {
            "get_ActiveHotbarSlot" => hands[0], "GetHotbarInventory" => hands,
            _ => null
        });
        player = new EntityPlayer { Api = api, World = world };
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(trunk, api);
        MethodInfo Hook(string name) => typeof(LoggingChecks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        harmony.Patch(AccessTools.PropertyGetter(ownerType, "Entity"), prefix: new HarmonyMethod(Hook(nameof(PlayerEntity))));
        harmony.Patch(AccessTools.PropertyGetter(ownerType, "InventoryManager"), prefix: new HarmonyMethod(Hook(nameof(PlayerInventory))));
        harmony.Patch(AccessTools.PropertyGetter(ownerType, "PlayerUID"), prefix: new HarmonyMethod(Hook(nameof(PlayerUid))));
        harmony.Patch(AccessTools.PropertyGetter(ownerType, "PlayerName"), prefix: new HarmonyMethod(Hook(nameof(PlayerName))));
        harmony.Patch(AccessTools.PropertyGetter(ownerType, "WorldData"), prefix: new HarmonyMethod(Hook(nameof(WorldData))));
        harmony.Patch(AccessTools.PropertyGetter(typeof(EntityPlayer), "RightHandItemSlot"), prefix: new HarmonyMethod(Hook(nameof(RightHand))));
        harmony.Patch(AccessTools.PropertyGetter(typeof(EntityPlayer), "LeftHandItemSlot"), prefix: new HarmonyMethod(Hook(nameof(LeftHand))));
        harmony.Patch(AccessTools.Method(typeof(Entity), "StartAnimation"), prefix: new HarmonyMethod(Hook(nameof(Skip))));
        harmony.Patch(AccessTools.Method(typeof(Entity), "StopAnimation"), prefix: new HarmonyMethod(Hook(nameof(Skip))));
        harmony.Patch(AccessTools.Method(typeof(Entity), "GetName"), prefix: new HarmonyMethod(Hook(nameof(PlayerName))));
        harmony.Patch(AccessTools.Method(typeof(BlockEntity), "MarkDirty"), prefix: new HarmonyMethod(Hook(nameof(Skip))));

        object config = Activator.CreateInstance(T("CarryOn.Common.Models.CarryOnConfig"))!;
        object provider = MakeProxy(T("CarryOn.Common.Interfaces.IConfigProvider"), (m, _) => m.Name == "get_Config" ? config : null);
        object events = Activator.CreateInstance(T("CarryOn.API.Event.CarryEvents"))!;
        object manager = Activator.CreateInstance(managerType, [api, provider, events])!;
        object services = AccessTools.Property(managerType, "Services").GetValue(manager)!;
        object placement = AccessTools.Property(services.GetType(), "Placement").GetValue(services)!;
        object pickup = AccessTools.Property(services.GetType(), "Pickup").GetValue(services)!;
        object entityService = Activator.CreateInstance(serviceType, [api])!;
        carrySystem = RuntimeHelpers.GetUninitializedObject(T("CarryOn.CarrySystem"));
        AccessTools.Property(carrySystem.GetType(), "CarryEvents").SetValue(carrySystem, events);
        object libSystem = RuntimeHelpers.GetUninitializedObject(T("CarryOn.CarryOnLib.CarryOnLibSystem"));
        AccessTools.Property(libSystem.GetType(), "CarryManager").SetValue(libSystem, manager);
        AccessTools.Property(carrySystem.GetType(), "CarryOnLib").SetValue(carrySystem, libSystem);
        AccessTools.Property(carrySystem.GetType(), "CarriedBlockEntityService").SetValue(carrySystem, entityService);
        MethodInfo restore = AccessTools.Method(placementType, "RestoreBlockEntityData");
        MethodInfo setCarried = AccessTools.Method(managerType, "SetCarried");
        MethodInfo getCarried = AccessTools.Method(managerType, "GetCarried");
        MethodInfo tryPlace = AccessTools.Method(placementType, "TryPlaceDown",
            [typeof(Entity), carriedType, typeof(BlockSelection), typeof(string).MakeByRefType(), typeof(bool), typeof(bool)]);
        MethodInfo takeWorld = AccessTools.Method(pickupType, "GetCarriedFromWorld",
            [typeof(Entity), typeof(BlockPos), slotType, typeof(string).MakeByRefType(), typeof(bool), typeof(bool?), typeof(HashSet<BlockPos>)]);
        MethodInfo spawn = AccessTools.Method(serviceType, "SpawnCarriedBlockEntityWithGravity",
            [carriedType, typeof(string), typeof(Vec3d), typeof(Vec3d), typeof(bool), typeof(float)]);
        harmony.Patch(spawn, prefix: new HarmonyMethod(Hook(nameof(Spawn))));
        var inventoryProperty = be.GetType().GetProperty("Inventory")!;
        var branchesProperty = be.GetType().GetProperty("BranchCount")!;
        object inventory = Activator.CreateInstance(logging.GetType("LoggingMod.TreeTrunkInventory", true)!, [null, null])!;
        inventoryProperty.SetValue(be, inventory);
        int LogCount() => (int)AccessTools.Property(inventoryProperty.GetValue(be)!.GetType(), "LogCount").GetValue(inventoryProperty.GetValue(be))!;
        ITreeAttribute Attrs(int quantity = 36, int branches = 47)
        {
            var tree = new TreeAttribute();
            var slots = new TreeAttribute();
            slots.SetItemstack("0", new ItemStack(logs, quantity));
            tree["slots"] = slots;
            tree.SetInt("qslots", 1);
            tree.SetInt("branchCount", branches);
            tree.SetBool("loadedFromLogs", true);
            return tree;
        }
        ItemStack Stack(ITreeAttribute? attrs = null, Block? block = null) => new(block ?? trunk) { Attributes = attrs ?? new TreeAttribute() };
        object Carried(ItemStack stack, ITreeAttribute? data) => Activator.CreateInstance(carriedType, [slot, stack, data])!;
        ITreeAttribute? Data(object carried) => carriedType.GetProperty("BlockEntityData")!.GetValue(carried) as ITreeAttribute;
        ItemStack Item(object carried) => (ItemStack)carriedType.GetProperty("ItemStack")!.GetValue(carried)!;
        int Quantity(ITreeAttribute? tree) => tree?.GetTreeAttribute("slots")?.GetItemstack("0")?.StackSize ?? 0;
        TreeAttribute DetachData()
        {
            var tree = new TreeAttribute();
            tree.SetString("blockCode", trunk.Code.ToShortString());
            tree.SetString("forBlockCode", trunk.Code.ToShortString());
            // A missing backpack payload is now returned as null by CarryOn.
            // Preserve the malformed empty inventory fixture without serializing a null attribute.
            tree["inventory"] = (IAttribute?)AccessTools.Method(T("CarryOn.Utility.InventoryConverter"), "ConvertBackpackToBlockInventory").Invoke(null, [null]) ?? new TreeAttribute();
            return tree;
        }
        void Place(object carried)
        {
            placed = false;
            object?[] args = [player, carried, new BlockSelection { Position = position.Copy(), Face = BlockFacing.UP }, "", false, false];
            Check(tryPlace.Invoke(placement, args) is true, "real Logging placement and CarryOn finalization succeed");
        }
        IDisposable? fix = null, replacement = null, renderingOnly = null;
        try
        {
            // First prove the exact destructive placement path without our fix.
            Place(Carried(Stack(Attrs()), DetachData()));
            Check(LogCount() == 0, "reproduce 36 logs being cleared by CarryOn's generic detach inventory");
            fix = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [api]);
            Check(fix != null, "all runtime hooks install against Logging 0.3.6 and CarryOn pre.8");
            Check(!messages.Any(m => m.Contains("compatibility unavailable")), "runtime installation reports no warning");
            foreach (var method in new[] { restore, tryPlace, takeWorld, AccessTools.Method(stateType, "SetCarried") })
                Check(Harmony.GetPatchInfo(method)?.Owners.Contains("vsscpmod.logging-carryon") == true,
                    method.DeclaringType!.Name + "." + method.Name + " uses compatibility");

            var detached = Carried(Stack(Attrs()), DetachData());
            setCarried.Invoke(manager, [player, detached, null, true]);
            var saved = getCarried.Invoke(manager, [player, slot])!;
            Check(Quantity(Data(saved)) == 36 && Quantity(Item(saved).Attributes) == 36, "SetCarried stores complete root slots after vehicle detach");
            Place(saved);
            Check(LogCount() == 36 && (int)branchesProperty.GetValue(be)! == 47, "fixed placement retains all 36 logs and 47 branches");
            Check(AccessTools.Property(inventoryProperty.GetValue(be)!.GetType(), "LoadedFromLogs").GetValue(inventoryProperty.GetValue(be)) is true,
                "Logging's loaded-from-logs flag survives placement");

            var complete = Carried(Stack(Attrs(36)), Attrs(12, 5));
            Place(complete);
            Check(LogCount() == 12 && Quantity(Item(complete).Attributes) == 12 && (int)branchesProperty.GetValue(be)! == 5,
                "live BE snapshot takes precedence over an outdated item snapshot");
            var missingItem = Carried(Stack(), Attrs());
            Place(missingItem);
            Check(LogCount() == 36, "existing carried BE data restores an empty picked-item snapshot");
            var noData = Carried(Stack(Attrs()), null);
            Place(noData);
            Check(LogCount() == 36, "placement also repairs null BE data without losing the item inventory");
            Type childType = T("CarryOn.API.Common.Models.AttachedCarriedBlock");
            Array children = Array.CreateInstance(childType, 1);
            children.SetValue(Activator.CreateInstance(childType, [new BlockPos(1, 0, 0), Carried(Stack(block: logs), null), BlockFacing.EAST]), 0);
            var origin = new AssetLocation("loggingmod:treetrunk-ebony-xxl-yes-east");
            var oriented = Activator.CreateInstance(carriedType, [slot, Stack(Attrs()), null, children, origin, .25f])!;
            setCarried.Invoke(manager, [player, oriented, null, true]);
            var orientedSaved = getCarried.Invoke(manager, [player, slot])!;
            Check(Equals(carriedType.GetProperty("OriginalBlockCode")!.GetValue(orientedSaved), origin)
                && Equals(carriedType.GetProperty("OriginalMeshAngle")!.GetValue(orientedSaved), .25f)
                && ((System.Collections.ICollection)carriedType.GetProperty("AttachedBlocks")!.GetValue(orientedSaved)!).Count == 1,
                "adding null BE data preserves orientation and attached children across state serialization");
            var backupStack = Stack();
            backupStack.Attributes["carryonbackup"] = Attrs(8, 3);
            Place(Carried(backupStack, DetachData()));
            Check(LogCount() == 8 && (int)branchesProperty.GetValue(be)! == 3, "a remaining carryonbackup can recover both missing primary copies");

            var valid = Carried(Stack(Attrs()), Attrs());
            setCarried.Invoke(manager, [player, valid, null, true]);
            Check(!ReferenceEquals(Item(valid).Attributes.GetTreeAttribute("slots"), Data(valid)!.GetTreeAttribute("slots")),
                "item inventory and carried BE inventory are independent snapshots");
            var networkTree = (ITreeAttribute)AccessTools.Method(T("CarryOn.Utility.CarriedBlockTreeSerializer"), "Serialize").Invoke(null, [valid])!;
            var wire = new TreeAttribute();
            wire.FromBytes(((TreeAttribute)networkTree).ToBytes());
            var deserialize = AccessTools.Method(T("CarryOn.Utility.CarriedBlockTreeSerializer"), "Deserialize", [typeof(ITreeAttribute), typeof(IWorldAccessor), slotType]);
            var remote = deserialize.Invoke(null, [wire, world, slot])!;
            Check(Item(remote).Attributes.GetTreeAttribute("slots")!.GetItemstack("0").Block == null,
                "network fixture reproduces unresolved nested log stacks");
            side = EnumAppSide.Client;
            Place(remote);
            Check(Item(remote).Attributes.GetTreeAttribute("slots")!.GetItemstack("0").Block == logs && Quantity(Data(remote)) == 36,
                "client resolves nested wood data before optimistic placement");
            side = EnumAppSide.Server;
            Place(remote);
            Check(LogCount() == 36, "server placement after the network round trip keeps the exact quantity");
            restore.Invoke(placement, [world, Carried(Stack(), DetachData()), position, false]);
            Check(LogCount() == 36, "an empty carried payload cannot erase an already-populated placed trunk");

            // Current world has a valid trunk; test the real pickup removal.
            placed = true;
            object?[] take = [player, position, slot, "", false, false, null];
            int before = removals;
            var picked = takeWorld.Invoke(pickup, take)!;
            Check(picked != null && removals == before + 1 && Quantity(Item(picked).Attributes) == 36,
                "normal ground pickup removes one block and carries its exact log count");
            Place(picked!);
            Check(LogCount() == 36, "first placement after normal ground pickup retains the inventory");
            be.FromTreeAttributes(new TreeAttribute(), world);
            placed = true;
            before = removals;
            take = [player, position, slot, "", false, false, null];
            Check(takeWorld.Invoke(pickup, take) == null && removals == before && placed && Equals(take[3], "loggingmod:trunk-empty"),
                "empty ground trunk is rejected before deletion or hand locking");

            var attachable = new AttributeAttachableToEntity();
            Check(!attachable.IsAttachable(player, Stack(Attrs())), "Logging world shape is excluded from humanoid attachments");
            Check(attachable.IsAttachable(new EntityAgent(), Stack(Attrs())), "vehicle attachment remains allowed");
            Check(attachable.IsAttachable(player, Stack(block: logs)), "unrelated player equipment remains allowed");
            var ordinary = Carried(Stack(block: logs), DetachData());
            ITreeAttribute ordinaryData = Data(ordinary)!;
            byte[] original = ((TreeAttribute)ordinaryData).ToBytes();
            restore.Invoke(placement, [world, ordinary, position, false]);
            Check(ordinaryData.GetTreeAttribute("slots") == null && original.Length <= ((TreeAttribute)ordinaryData).ToBytes().Length,
                "non-Logging inventory is not converted to trunk slots");

            // Release an already-corrupted carried trunk through the real
            // CarryOn state remover, including LockedItemSlot restoration.
            var empty = Carried(Stack(), DetachData());
            setCarried.Invoke(manager, [player, empty, null, true]);
            Type lockedSlot = T("CarryOn.Common.Models.LockedItemSlot");
            Check(lockedSlot.IsInstanceOfType(hands[0]) && lockedSlot.IsInstanceOfType(hands[1]), "fixture reproduces CarryOn's locked hands");
            var commandArgs = new TextCommandCallingArgs { LanguageCode = "en", Caller = new Caller { Player = owner } };
            MethodInfo release = compatibility.GetMethod("ReleaseTrunk", BindingFlags.Instance | BindingFlags.NonPublic)!;
            allowSpawn = false;
            release.Invoke(fix, [serverApi, commandArgs]);
            Check(getCarried.Invoke(manager, [player, slot]) != null && lockedSlot.IsInstanceOfType(hands[0]),
                "failed entity spawn retains carried data and does not silently delete the trunk");
            allowSpawn = true;
            capturedSpawn = null;
            release.Invoke(fix, [serverApi, commandArgs]);
            Check(capturedSpawn != null && Quantity(Data(capturedSpawn)) == 0 && Data(capturedSpawn)!.GetString("forBlockCode") == trunk.Code.ToShortString(),
                "recovery preserves the empty shell's remaining metadata without inventing logs");
            Check(getCarried.Invoke(manager, [player, slot]) == null && !lockedSlot.IsInstanceOfType(hands[0]) && !lockedSlot.IsInstanceOfType(hands[1]),
                "recovery clears CarryOn state and restores both hand slots without death");
            setCarried.Invoke(manager, [player, Carried(Stack(Attrs()), DetachData()), null, true]);
            release.Invoke(fix, [serverApi, commandArgs]);
            Check(Quantity(Data(capturedSpawn!)) == 36 && Quantity(Item(capturedSpawn!).Attributes) == 36,
                "recovery entity also preserves a repairable full inventory");

            replacement = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [api]);
            fix!.Dispose();
            Place(Carried(Stack(Attrs()), DetachData()));
            Check(LogCount() == 36, "disposing old singleplayer instance leaves replacement hooks installed");
            replacement!.Dispose();
            Check(Harmony.GetPatchInfo(restore)?.Owners.Contains("vsscpmod.logging-carryon") != true,
                "disposing active compatibility removes its CarryOn hooks");
            carryEnabled = false;
            renderingOnly = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [api]);
            Check(renderingOnly != null && !attachable.IsAttachable(player, Stack()), "Logging rendering fix works with CarryOn absent");
            renderingOnly!.Dispose();
            loggingEnabled = false;
            Check(compatibility.GetMethod("TryCreate")!.Invoke(null, [api]) == null && attachable.IsAttachable(player, Stack()),
                "without Logging no hooks are installed");
            foreach (string language in new[] { "en", "zh-cn" })
            {
                string text = File.ReadAllText(Path.Combine(root, "assets/vsscpmod/lang", language + ".json"));
                var lang = System.Text.Json.JsonDocument.Parse(text);
                Check(lang.RootElement.TryGetProperty("carryon:place-down-failed-loggingmod:trunk-empty", out _), language + " has the actual CarryOn failure translation key");
            }
            Console.WriteLine($"{count} Logging/CarryOn checks passed.");
        }
        finally
        {
            renderingOnly?.Dispose();
            replacement?.Dispose();
            fix?.Dispose();
            harmony.UnpatchAll(harmony.Id);
        }
    }

    private static object MakeProxy(Type type, System.Func<MethodInfo, object?[]?, object?> callback) =>
        typeof(Proxy).GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1)
            .MakeGenericMethod(type).Invoke(null, [callback])!;
    private static bool PlayerEntity(ref EntityPlayer __result) { __result = player; return false; }
    private static bool PlayerInventory(ref IPlayerInventoryManager __result) { __result = inventoryManager; return false; }
    private static bool PlayerUid(ref string __result) { __result = "loggingprobe"; return false; }
    private static bool WorldData(ref IWorldPlayerData __result)
    { __result = Proxy.Create<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? EnumGameMode.Survival : null); return false; }
    private static bool PlayerName(ref string __result) { __result = "Logging probe"; return false; }
    private static bool RightHand(ref ItemSlot __result) { __result = hands[0]; return false; }
    private static bool LeftHand(ref ItemSlot __result) { __result = hands[1]; return false; }
    private static bool Skip() => false;
    private static bool Spawn(object[] __args, ref object? __result)
    {
        capturedSpawn = __args[0];
        __result = allowSpawn ? RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("CarryOn.Common.Entities.EntityCarriedBlock")) : null;
        return false;
    }
}
