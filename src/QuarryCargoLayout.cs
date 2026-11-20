using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Reservations are derived from real inventory stacks. Nothing extra is serialized or dropped.
internal sealed class QuarryCargoLayout
{
    private static readonly ConditionalWeakTable<EntityBehaviorAttachable, QuarryCargoLayout> layouts = new();
    private readonly EntityBehaviorAttachable behavior;
    private readonly WearableSlotConfig[] slots;
    private readonly Dictionary<int, Cell> cells = new();
    private sealed record Cell(int Region, int X, int Z, int Width, int Depth);
    internal sealed record Footprint(int[] Indices, int X, int Z, int Width, int Depth, string Size)
    {
        internal string Model => $"quarrycargo/grid/{Size}-{X + 1}-{Z + 1}-{Width}-{Depth}";
    }
    private QuarryCargoLayout(EntityBehaviorAttachable behavior, WearableSlotConfig[] slots)
    {
        this.behavior = behavior;
        this.slots = slots;
        string code = behavior.entity.Code.Path;
        int region = 0;
        void Region(int width, int depth, params string[] parents)
        {
            for (int n = 0; n < parents.Length; n++)
            {
                int i = Array.FindIndex(slots, s => s.StepParentTo != null
                    && s.StepParentTo.TryGetValue("", out var p) && p.ElementName == parents[n]);
                if (i >= 0 && behavior.Inventory[i] is QuarryShipCargoSlot)
                    cells[i] = new(region, n % width, n / width, width, depth);
            }
            region++;
        }
        if (QuarryShipCargoBehavior.IsCart(behavior.entity))
        {
            if (code.StartsWith("cart-basiccart-")) Region(3, 2,
                "RightStorage1", "RightStorage2", "RightStorage3", "LeftStorage1", "LeftStorage2", "LeftStorage3");
            else if (code.StartsWith("cart-slimcart-")) Region(3, 1, "RightStorage1", "RightStorage2", "RightStorage3");
        }
        else if (code.StartsWith("boat-reedlongboat-") || code.StartsWith("boat-sailedreedboat-"))
            Region(3, 1, "BackStorage", "MiddleStorage", "FrontStorage");
        else if (code.StartsWith("boat-punter-")) Region(3, 1, "BackSeat", "MiddleSeat", "FrontSeat");
        else if (code.StartsWith("boat-catboat-")) Region(4, 1, "Storage4", "Storage3", "Storage2", "Storage1");
        else if (code.StartsWith("boat-drakkar-"))
        {
            Region(3, 1, "Storage3", "Storage2", "Storage1");
            Region(3, 1, "Storage6", "Storage5", "Storage4");
            Region(3, 1, "Storage9", "Storage8", "Storage7");
            Region(3, 1, "Storage12", "Storage11", "Storage10");
        }
        else if (code.StartsWith("boat-largejunker-"))
        {
            Region(3, 2, "Storage5", "Storage3", "Storage1", "Storage6", "Storage4", "Storage2");
            Region(3, 2, "Storage10", "Storage9", "Storage12", "Storage7", "Storage8", "Storage11");
        }
        // Unknown/isolated attachment points are safe for one small slab only.
        for (int i = 0; i < slots.Length; i++)
            if (behavior.Inventory[i] is QuarryShipCargoSlot && !cells.ContainsKey(i))
                cells[i] = new(region++, 0, 0, 1, 1);
    }
    internal static void Register(EntityBehaviorAttachable behavior, WearableSlotConfig[] slots)
    {
        layouts.Remove(behavior);
        layouts.Add(behavior, new(behavior, slots));
    }
    internal static bool TryGet(EntityBehaviorAttachable behavior, out QuarryCargoLayout layout) => layouts.TryGetValue(behavior, out layout!);
    private static bool HasLayout(EntityBehaviorAttachable behavior) => layouts.TryGetValue(behavior, out _);
    internal static IEnumerable<CodeInstruction> AccurateRearOccupancy(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        // Replace only vanilla's reverse rear-slot check for adapted vehicles. CanLoad
        // already validated every actual reservation; keep native ownership/transfers.
        int predicate = code.FindIndex(i => i.opcode == OpCodes.Ldftn
            && i.operand is System.Reflection.MethodInfo method && method.Name == "<TryAttach>b__0");
        int comparison = predicate < 0 ? -1 : code.FindIndex(predicate, i => i.opcode == OpCodes.Ble_S || i.opcode == OpCodes.Ble);
        if (predicate < 3 || comparison < 2
            || code[predicate - 3].opcode != OpCodes.Ldarg_0
            || !code[predicate - 2].LoadsField(AccessTools.Field(typeof(EntityBehaviorAttachable), "wearableSlots"))
            || !code[comparison - 2].Calls(AccessTools.PropertyGetter(typeof(IAttachableToEntity), nameof(IAttachableToEntity.RequiresBehindSlots)))
            || !code[comparison - 1].LoadsConstant(0)
            || code[comparison].operand is not Label target
            || !code.Any(i => i.labels.Contains(target)))
            throw new InvalidOperationException("Vehicle rear occupancy patch does not match this game version.");
        int start = predicate - 3;
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(code[start].labels);
        code[start].labels.Clear();
        code.InsertRange(start, [first, new(OpCodes.Call, AccessTools.Method(typeof(QuarryCargoLayout), nameof(HasLayout))),
            new(OpCodes.Brtrue, code[comparison].operand)]);
        return code;
    }
    internal Footprint? Plan(int index, ItemStack stack)
    {
        if (!cells.TryGetValue(index, out var cell)) return null;
        string size = QuarryShipCargoBehavior.Size(stack);
        if (size is not ("small" or "medium" or "large" or "huge" or "giant")) return null;
        int w = size == "small" ? 1 : size == "medium" && cell.Depth > 1 ? 1 : 2;
        int d = size == "small" || size == "medium" && cell.Depth == 1 ? 1 : 2;
        if (w > cell.Width || d > cell.Depth) return null;
        int x = Math.Min(cell.X, cell.Width - w), z = Math.Min(cell.Z, cell.Depth - d);
        int[] indices = cells.Where(p => p.Value.Region == cell.Region && p.Value.X >= x && p.Value.X < x + w
            && p.Value.Z >= z && p.Value.Z < z + d).Select(p => p.Key).ToArray();
        return indices.Length == w * d ? new(indices, x - cell.X, z - cell.Z, w, d, size) : null;
    }
    private int[] OccupiedBy(int index, ItemStack stack)
    {
        if (QuarryShipCargoBehavior.IsSlab(stack)) return Plan(index, stack)?.Indices ?? [index];
        int behind = IAttachableToEntity.FromCollectible(stack.Collectible)?.RequiresBehindSlots ?? 0;
        return new[] { index }.Concat((slots[index].BehindSlots ?? []).Take(behind)
            .Select(code => Array.FindIndex(slots, s => s.Code == code)).Where(i => i >= 0)).ToArray();
    }
    internal int OwnerAt(int index)
    {
        if (index < 0 || index >= behavior.Inventory.Count || !behavior.Inventory[index].Empty) return index;
        for (int i = 0; i < behavior.Inventory.Count; i++)
            if (!behavior.Inventory[i].Empty && OccupiedBy(i, behavior.Inventory[i].Itemstack!).Contains(index)) return i;
        return index;
    }
    private bool MatchesSeat(int index, SeatConfig seat) => slots[index].AttachmentPointCode is { } ap
        && (ap == seat.APName || ap == seat.SelectionBox)
        || seat.SeatId != null && slots[index].ProvidesSeatId == seat.SeatId;
    private bool HasPassenger(int index) => behavior.entity.GetBehavior<EntityBehaviorSeatable>()?.Seats?
        .Any(s => s.Passenger != null && s.Config != null && MatchesSeat(index, s.Config)) == true;
    private bool ProvidesSeat(int index, ItemStack stack)
    {
        if (QuarryShipCargoBehavior.IsSlab(stack)) return false;
        var attributes = stack.ItemAttributes;
        return attributes != null && (attributes["isSaddle"].AsBool(false)
            || attributes["attachableToEntity"]["seatConfig"].Exists
            || attributes["attachableToEntity"]["seatConfigBySlotCode"][slots[index].Code].Exists);
    }
    private static bool CargoBlocksSeat(Entity? entity, SeatConfig? seat)
    {
        if (seat == null || entity?.GetBehavior<EntityBehaviorAttachable>() is not { } attachable
            || !TryGet(attachable, out var layout)) return false;
        for (int i = 0; i < attachable.Inventory.Count; i++)
        {
            var stack = attachable.Inventory[i].Itemstack;
            if (stack == null) continue;
            // Cart seats use RiderN as APName and the cargo AP as SelectionBox.
            // Seat-providing items allow their anchor, never their reserved rear cells.
            // Scan all real stacks so overlapping legacy cargo cannot hide a blocker.
            if (layout.OccupiedBy(i, stack).Any(index => layout.MatchesSeat(index, seat)
                && (index != i || !layout.ProvidesSeat(i, stack)))) return true;
        }
        return false;
    }
    internal static bool BeforeSit(EntityBehaviorSeatable __instance, IMountableSeat seat, ref bool __result)
    {
        if (!CargoBlocksSeat(__instance.entity, seat.Config)) return true;
        __result = false;
        return false;
    }
    internal static bool BeforeMount(IMountableSeat onmount, ref bool __result)
    {
        // Direct mounting (including carried creatures) can bypass CanSitOn.
        if (!CargoBlocksSeat(onmount?.Entity, onmount?.Config)) return true;
        __result = false;
        return false;
    }
    internal bool CanLoad(int index, ItemStack stack)
    {
        if (index < 0 || index >= slots.Length || behavior.Inventory.PutLocked) return false;
        bool slab = QuarryShipCargoBehavior.IsSlab(stack);
        var plan = slab ? Plan(index, stack) : null;
        if (slab && plan == null) return false;
        int[] wanted = plan?.Indices ?? OccupiedBy(index, stack);
        if (!slab && wanted.Length != 1 + (IAttachableToEntity.FromCollectible(stack.Collectible)?.RequiresBehindSlots ?? 0)) return false;
        foreach (int target in wanted)
        {
            if (target != index && !behavior.Inventory[target].Empty) return false;
            if (HasPassenger(target)) return false;
            for (int i = 0; i < behavior.Inventory.Count; i++)
                if (i != index && !behavior.Inventory[i].Empty && OccupiedBy(i, behavior.Inventory[i].Itemstack!).Contains(target)) return false;
        }
        return true;
    }
    internal static void ResolveSelection(EntityBehaviorAttachable __instance, ref int __result)
    {
        if (TryGet(__instance, out var layout)) __result = layout.OwnerAt(__result);
    }
    internal static void ResolveAttachmentPoint(EntityBehaviorAttachable __instance, string apCode, ref ItemSlot __result)
    {
        if (!TryGet(__instance, out var layout)) return;
        int index = Array.FindIndex(layout.slots, s => s.AttachmentPointCode == apCode);
        if (index >= 0) __result = __instance.Inventory[layout.OwnerAt(index)];
    }
    internal static bool BeforeRemove(EntityBehaviorAttachable __instance, int selectionBoxIndex, ref bool __result)
    {
        if (!TryGet(__instance, out var layout)) return true;
        int index = __instance.GetSlotIndexFromSelectionBoxIndex(selectionBoxIndex);
        if (index < 0 || index >= __instance.Inventory.Count || __instance.Inventory[index].Empty) return true;
        if (!layout.OccupiedBy(index, __instance.Inventory[index].Itemstack!).Any(layout.HasPassenger)) return true;
        __result = false;
        return false;
    }
    internal static bool BeforeAttach(EntityBehaviorAttachable __instance, ItemSlot itemslot, int selectionBoxIndex, ref bool __result)
    {
        if (itemslot.Empty || !TryGet(__instance, out var layout)) return true;
        int index = __instance.GetSlotIndexFromSelectionBoxIndex(selectionBoxIndex);
        if (layout.CanLoad(index, itemslot.Itemstack)) return true;
        if (__instance.entity.Api is ICoreClientAPI client)
            client.TriggerIngameError(__instance, "quarrycargospace", Lang.Get("vsscpmod:quarry-cargo-no-space"));
        __result = false;
        return false;
    }
    internal static void BeforeShape(EntityBehaviorContainer __instance, ItemStack stack, string slotCode, ref IAttachableToEntity iatta)
    {
        if (__instance is not EntityBehaviorAttachable attachable || !QuarryShipCargoBehavior.IsSlab(stack)
            || !TryGet(attachable, out var layout)) return;
        int index = Array.FindIndex(layout.slots, s => s.Code == slotCode);
        var plan = layout.Plan(index, stack);
        // Old invalid loads retain their compact model and remain unloadable.
        if (plan != null && plan.Indices.All(i => i == index || attachable.Inventory[i].Empty)) iatta = new CargoShape(iatta, plan.Model);
    }
    private sealed class CargoShape(IAttachableToEntity original, string model) : IAttachableToEntity
    {
        public int RequiresBehindSlots { get => original.RequiresBehindSlots; set => original.RequiresBehindSlots = value; }
        public bool IsAttachable(Entity entity, ItemStack stack) => original.IsAttachable(entity, stack);
        public string? GetCategoryCode(ItemStack stack) => original.GetCategoryCode(stack);
        public CompositeShape GetAttachedShape(ItemStack stack, string slotCode) => new() { Base = new("vsscpmod:" + model) };
        public string[]? GetDisableElements(ItemStack stack) => original.GetDisableElements(stack);
        public string[]? GetKeepElements(ItemStack stack) => original.GetKeepElements(stack);
        public string? GetTexturePrefixCode(ItemStack stack) => original.GetTexturePrefixCode(stack);
        public void CollectTextures(ItemStack stack, Shape shape, string prefix, Dictionary<string, CompositeTexture> textures) => original.CollectTextures(stack, shape, prefix, textures);
    }
}
