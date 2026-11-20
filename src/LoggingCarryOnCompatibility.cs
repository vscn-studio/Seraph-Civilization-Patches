using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

/// <summary>
/// Logging stores its inventory at the root of the tree, whereas CarryOn's
/// vehicle detach path builds an inventory/slots tree. Keep Logging's two
/// copies (picked item and carried BE data) consistent before either is used.
/// Third-party types are optional runtime dependencies.
/// </summary>
internal sealed class LoggingCarryOnCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.logging-carryon";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly PropertyInfo? stackProperty;
    private readonly PropertyInfo? dataProperty;
    private readonly PropertyInfo? slotProperty;
    private readonly PropertyInfo? childrenProperty;
    private readonly PropertyInfo? originalCodeProperty;
    private readonly PropertyInfo? originalAngleProperty;
    private readonly ConstructorInfo? carriedConstructor;
    private readonly FieldInfo? pickupApiField;
    private readonly MethodInfo? getCarried;
    private readonly MethodInfo? removeCarried;
    private readonly MethodInfo? spawnCarriedEntity;
    private readonly PropertyInfo? managerProperty;
    private readonly PropertyInfo? entityServiceProperty;
    private readonly object? handsSlot;
    private bool disposed;
    private static LoggingCarryOnCompatibility? current;

    private LoggingCarryOnCompatibility(bool carryOnEnabled)
    {
        MethodInfo attachable = RequiredMethod(RequiredType("Vintagestory.GameContent.AttributeAttachableToEntity"),
            "IsAttachable", [typeof(Entity), typeof(ItemStack)]);
        var patches = new List<(MethodInfo Target, string Prefix)> { (attachable, nameof(CheckPlayerAttachment)) };
        if (carryOnEnabled)
        {
            Type carriedType = RequiredType("CarryOn.API.Common.Models.CarriedBlock");
            Type slotType = RequiredType("CarryOn.API.Common.Models.CarrySlot");
            Type pickupType = RequiredType("CarryOn.Common.Services.CarryPickupService");
            Type placementType = RequiredType("CarryOn.Common.Services.CarryPlacementService");
            Type stateType = RequiredType("CarryOn.Common.Services.CarryStateService");
            Type systemType = RequiredType("CarryOn.CarrySystem");
            Type managerType = RequiredType("CarryOn.Common.Services.CarryManager");
            Type entityServiceType = RequiredType("CarryOn.Server.Logic.CarriedBlockEntityService");
            stackProperty = RequiredProperty(carriedType, "ItemStack");
            dataProperty = RequiredProperty(carriedType, "BlockEntityData");
            slotProperty = RequiredProperty(carriedType, "Slot");
            childrenProperty = RequiredProperty(carriedType, "AttachedBlocks");
            originalCodeProperty = RequiredProperty(carriedType, "OriginalBlockCode");
            originalAngleProperty = RequiredProperty(carriedType, "OriginalMeshAngle");
            carriedConstructor = carriedType.GetConstructors().Single(c => c.GetParameters().Length == 6);
            pickupApiField = pickupType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(f => f.FieldType == typeof(ICoreAPI));
            handsSlot = Enum.Parse(slotType, "Hands");
            getCarried = RequiredMethod(managerType, "GetCarried", [typeof(Entity), slotType]);
            removeCarried = RequiredMethod(managerType, "RemoveCarried", [typeof(Entity), slotType, typeof(bool)]);
            managerProperty = RequiredProperty(systemType, "CarryManager");
            entityServiceProperty = RequiredProperty(systemType, "CarriedBlockEntityService");
            spawnCarriedEntity = RequiredMethod(entityServiceType, "SpawnCarriedBlockEntityWithGravity",
                [carriedType, typeof(string), typeof(Vec3d), typeof(Vec3d), typeof(bool), typeof(float)]);
            patches.Add((RequiredMethod(stateType, "SetCarried",
                [typeof(Entity), carriedType, typeof(Nullable<>).MakeGenericType(slotType), typeof(bool)]), nameof(PrepareCarried)));
            patches.Add((RequiredMethod(placementType, "TryPlaceDown",
                [typeof(Entity), carriedType, typeof(BlockSelection), typeof(string).MakeByRefType(), typeof(bool), typeof(bool)]), nameof(PrepareCarried)));
            patches.Add((RequiredMethod(placementType, "RestoreBlockEntityData",
                [typeof(IWorldAccessor), carriedType, typeof(BlockPos), typeof(bool)]), nameof(PrepareRestore)));
            // Intercept the method that actually removes the world block. This
            // covers both TryPickUp and direct callers, including no-player calls.
            Type[] pickupParameters = [typeof(Entity), typeof(BlockPos), slotType, typeof(string).MakeByRefType(), typeof(bool), typeof(bool?)];
            var pickupMethod = pickupType.GetMethod("GetCarriedFromWorld", pickupParameters)
                ?? RequiredMethod(pickupType, "GetCarriedFromWorld", [.. pickupParameters, typeof(HashSet<BlockPos>)]);
            patches.Add((pickupMethod, nameof(CheckPickup)));
        }
        try
        {
            foreach (var patch in patches)
                harmony.Patch(patch.Target, prefix: new HarmonyMethod(typeof(LoggingCarryOnCompatibility), patch.Prefix));
        }
        catch
        {
            harmony.UnpatchAll(HarmonyId);
            throw;
        }
    }

    public static LoggingCarryOnCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("loggingmod")) return null;
        try
        {
            current?.Dispose();
            current = new LoggingCarryOnCompatibility(api.ModLoader.IsModEnabled("carryon"));
            api.Logger.Notification("[vsscpmod] Logging player attachments fixed; CarryOn trunk inventory compatibility: {0}.",
                current.carriedConstructor != null ? "enabled" : "not needed");
            return current;
        }
        catch (Exception exception)
        {
            api.Logger.Warning("[vsscpmod] Logging compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static bool IsTrunk(ItemStack? stack) => IsTrunk(stack?.Block);
    private static bool IsTrunk(Block? block) => block?.Code?.Domain == "loggingmod"
        && block.Code.Path.StartsWith("treetrunk-", StringComparison.Ordinal);

    private static bool CheckPlayerAttachment(Entity toEntity, ItemStack itemStack, ref bool __result)
    {
        if (toEntity is not EntityPlayer || !IsTrunk(itemStack)) return true;
        // The world model has no humanoid step parents. Vehicle attachments
        // and CarryOn's own carried-block renderer use separate paths.
        __result = false;
        return false;
    }

    private static ItemStack? InventoryStack(ITreeAttribute? tree) => tree?.GetTreeAttribute("slots")?.GetItemstack("0");
    private static bool HasContents(ITreeAttribute? tree) => InventoryStack(tree)?.StackSize > 0;

    private static bool CanPlace(ITreeAttribute? tree, IWorldAccessor world)
    {
        ItemStack? logs = InventoryStack(tree);
        if (logs == null || logs.StackSize <= 0) return false;
        // Network deserialization resolves the outer trunk item, but not the
        // nested log stack. Logging's WoodType needs that stack's actual Block.
        if (logs.Block == null) logs.ResolveBlockOrItem(world);
        string? path = logs.Block?.Code?.Path;
        // Match Logging 0.3.6 TreeTrunkInventory.WoodType's accepted formats.
        return path != null && (path.StartsWith("log-placed-", StringComparison.Ordinal)
            ? path["log-placed-".Length..].Split('-').Length >= 2
            : path.StartsWith("log-", StringComparison.Ordinal) && path.Split('-').Length >= 3);
    }

    private static void CopyInventory(ITreeAttribute source, ITreeAttribute destination)
    {
        // Clone nested stacks so later inventory consumption cannot mutate the
        // carried snapshot or a vehicle's backup through shared references.
        destination.SetInt("qslots", 1);
        destination["slots"] = source["slots"].Clone();
        destination.SetInt("branchCount", source.GetInt("branchCount"));
        destination.SetBool("loadedFromLogs", source.GetBool("loadedFromLogs"));
        destination.SetBool("loadedFromFirewood", source.GetBool("loadedFromFirewood"));
    }

    private object RepairCarried(object carried, IWorldAccessor world)
    {
        if (stackProperty?.GetValue(carried) is not ItemStack stack || !IsTrunk(stack)) return carried;
        var data = dataProperty!.GetValue(carried) as ITreeAttribute;
        // BE data is the live-world snapshot when present. An ordinary trunk
        // attached as equipment has only item attributes, with no BE backup.
        ITreeAttribute? source = HasContents(data) ? data : HasContents(stack.Attributes) ? stack.Attributes
            : stack.Attributes["carryonbackup"] is ITreeAttribute backup && HasContents(backup) ? backup : null;
        if (source == null) return carried; // Never invent the missing log count.
        CanPlace(source, world); // Resolve the nested stack on both sides.
        CopyInventory(source, stack.Attributes);
        if (data != null)
        {
            CopyInventory(source, data);
            return carried;
        }
        data = new TreeAttribute();
        CopyInventory(source, data);
        // The property is read-only; preserve slot, children and orientation
        // when adding BE data to a carried block that originally had none.
        return carriedConstructor!.Invoke([slotProperty!.GetValue(carried), stack, data,
            childrenProperty!.GetValue(carried), originalCodeProperty!.GetValue(carried), originalAngleProperty!.GetValue(carried)]);
    }

    private static void PrepareCarried(Entity entity, object[] __args)
    {
        if (current?.carriedConstructor != null && __args[1] != null)
            __args[1] = current.RepairCarried(__args[1], entity.World);
    }

    private static bool PrepareRestore(IWorldAccessor world, object[] __args)
    {
        var active = current;
        if (active?.stackProperty?.GetValue(__args[1]) is not ItemStack stack || !IsTrunk(stack)) return true;
        __args[1] = active.RepairCarried(__args[1], world);
        // A malformed generic inventory tree must never erase a trunk that
        // Logging has already populated during its successful placement.
        return HasContents(active.dataProperty!.GetValue(__args[1]) as ITreeAttribute);
    }

    private static bool CheckPickup(object __instance, BlockPos pos, ref string failureCode, ref object? __result)
    {
        if (current?.pickupApiField?.GetValue(__instance) is not ICoreAPI api) return true;
        IWorldAccessor world = api.World;
        Block block = world.BlockAccessor.GetBlock(pos);
        if (!IsTrunk(block)) return true;
        ItemStack? picked = block.OnPickBlock(world, pos);
        if (CanPlace(picked?.Attributes, world)) return true;
        failureCode = "loggingmod:trunk-empty";
        __result = null;
        return false;
    }

    public void RegisterRecoveryCommand(ICoreServerAPI api)
    {
        if (carriedConstructor == null) return;
        ModCommands.Root(api).BeginSubCommand("trunkrelease")
            .WithDescription("Release your carried Logging trunk as an entity, preserving its remaining data")
            .RequiresPrivilege(Privilege.chat).RequiresPlayer()
            .HandleWith(args => ReleaseTrunk(api, args)).EndSubCommand();
    }

    private TextCommandResult ReleaseTrunk(ICoreServerAPI api, TextCommandCallingArgs args)
    {
        var player = args.Caller.Player;
        object? system = api.ModLoader.GetModSystem("CarryOn.CarrySystem");
        object? manager = system == null ? null : managerProperty!.GetValue(system);
        object? service = system == null ? null : entityServiceProperty!.GetValue(system);
        if (player == null || manager == null || service == null)
            return TextCommandResult.Error(Lang.GetL(args.LanguageCode, "vsscpmod:trunk-release-unavailable"));
        object? carried = getCarried!.Invoke(manager, [player.Entity, handsSlot]);
        if (carried == null || !IsTrunk(stackProperty!.GetValue(carried) as ItemStack))
            return TextCommandResult.Error(Lang.GetL(args.LanguageCode, "vsscpmod:trunk-release-none"));
        carried = RepairCarried(carried, api.World);
        bool recoverable = CanPlace((stackProperty!.GetValue(carried) as ItemStack)?.Attributes, api.World);
        // Call the entity service directly: CarryOn's drop helper falls back
        // to GetDrops when that service is unavailable, losing empty-shell data.
        object? spawned = spawnCarriedEntity!.Invoke(service,
            [carried, player.PlayerUID, player.Entity.Pos.XYZ, null, false, 1f]);
        if (spawned == null)
            return TextCommandResult.Error(Lang.GetL(args.LanguageCode, "vsscpmod:trunk-release-unavailable"));
        removeCarried!.Invoke(manager, [player.Entity, handsSlot, true]);
        api.Logger.Audit("[vsscpmod] Player {0} released Logging trunk as a CarryOn entity; recoverable inventory: {1}.",
            player.PlayerName, recoverable);
        return TextCommandResult.Success(Lang.GetL(args.LanguageCode,
            recoverable ? "vsscpmod:trunk-release-ok" : "vsscpmod:trunk-release-empty"));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // A late disposal of the replaced singleplayer-side instance must not
        // remove the hooks installed by the newer instance.
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
    }

    private static Type RequiredType(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);
    private static MethodInfo RequiredMethod(Type type, string name, Type[] parameters) =>
        AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
    private static PropertyInfo RequiredProperty(Type type, string name) =>
        AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name);
}
