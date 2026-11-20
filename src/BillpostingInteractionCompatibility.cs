using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

internal sealed class BillpostingInteractionCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.billposting-interaction";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<BillpostingInteractionCompatibility> owners = [];
    private static FieldInfo config = null!, lockSigned = null!, text = null!, title = null!, renderer = null!;
    private static MethodInfo setText = null!;

    public static BillpostingInteractionCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("billposting")) return null;
        if (owners.Count == 0)
        {
            try
            {
                var entity = AccessTools.TypeByName("billposting.EntityBillpost")
                    ?? throw new TypeLoadException("billposting.EntityBillpost");
                var behavior = entity.Assembly.GetType("billposting.BehaviorBillpostable", true)!;
                config = AccessTools.Field(entity.Assembly.GetType("billposting.billpostingModSystem"), "config");
                lockSigned = AccessTools.Field(config.FieldType, "LockSignedBillboards");
                text = AccessTools.Field(entity, "BillpostText");
                title = AccessTools.Field(entity, "BillpostName");
                renderer = AccessTools.Field(entity, "billpostRenderer");
                setText = AccessTools.Method(renderer.FieldType, "SetNewText", [typeof(string), typeof(int)]);
                harmony.Patch(AccessTools.DeclaredMethod(behavior, "OnHeldInteractStart"),
                    prefix: new HarmonyMethod(typeof(BillpostingInteractionCompatibility), nameof(Place)));
                harmony.Patch(AccessTools.DeclaredMethod(entity, "OnInteract"),
                    prefix: new HarmonyMethod(typeof(BillpostingInteractionCompatibility), nameof(Take))
                    { after = ["vsscsmod.parchment"] });
                harmony.Patch(AccessTools.DeclaredMethod(entity, "Initialize"),
                    postfix: new HarmonyMethod(typeof(BillpostingInteractionCompatibility), nameof(Initialized)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(HarmonyId);
                api.Logger.Warning("[vsscpmod] Billposting interaction compatibility unavailable: {0}", exception.GetBaseException().Message);
                return null;
            }
        }
        var owner = new BillpostingInteractionCompatibility();
        owners.Add(owner);
        api.Logger.Notification("[vsscpmod] Billposting placement data, text updates and safe parchment pickup enabled.");
        return owner;
    }

    private static bool Place(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, bool firstEvent,
        ref EnumHandHandling handHandling, ref EnumHandling handling, string ___entityCode, float ___yOffset)
    {
        if (blockSel == null || byEntity is not EntityPlayer playerEntity || !byEntity.Controls.CtrlKey
            || slot.Empty || !blockSel.Face.IsHorizontal) return true;
        handHandling = EnumHandHandling.PreventDefaultAction;
        handling = EnumHandling.PreventSubsequent;
        // Client prediction must not create an untracked duplicate entity or take paper.
        var world = byEntity.World;
        if (!firstEvent || world.Side != EnumAppSide.Server) return false;
        var player = playerEntity.Player;
        if (!world.Claims.TryAccess(player, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak)) return false;
        var properties = world.GetEntityType(new AssetLocation(___entityCode));
        if (properties == null) return false;
        var posted = world.ClassRegistry.CreateEntity(properties);
        if (posted == null) return false;
        var paper = slot.Itemstack.Clone();
        paper.StackSize = 1;
        posted.Pos.SetPos(blockSel.FullPosition.AddCopy(new Vec3f(0, ___yOffset, 0)));
        posted.Pos.Yaw = blockSel.Face.HorizontalAngleIndex * GameMath.PIHALF;
        foreach (string key in new[] { "text", "title", "signedby", "signedbyuid" })
            posted.WatchedAttributes.SetString(key, paper.Attributes.GetString(key, ""));
        posted.WatchedAttributes.SetItemstack("billpostdropstack", paper.Clone());
        posted.WatchedAttributes.SetBytes("billpostdropstackbytes", paper.ToBytes());
        // Initialize and the first spawn packet must already see the complete text
        // and original stack, including civilization contract attributes.
        world.SpawnEntity(posted);
        slot.TakeOut(1);
        slot.MarkDirty();
        return false;
    }

    private static void Initialized(Entity __instance)
    {
        // The paper extends below the entity origin. Billposting's 0..0.3 box
        // misses its lower half. A box enclosing the sheet works at every wall yaw.
        __instance.SelectionBox = new Cuboidf(-.25f, -.4f, -.25f, .25f, .4f, .25f);
        __instance.OriginSelectionBox = __instance.SelectionBox.Clone();
        __instance.WatchedAttributes.RegisterModifiedListener("title",
            () => title.SetValue(__instance, __instance.WatchedAttributes.GetString("title", "")));
        // Civilization already refreshes text for its live contract updates.
        if (!__instance.Api.ModLoader.IsModEnabled("vsscsmod"))
            __instance.WatchedAttributes.RegisterModifiedListener("text", () => RefreshText(__instance));
    }

    private static void RefreshText(Entity entity)
    {
        string value = entity.WatchedAttributes.GetString("text", "");
        text.SetValue(entity, value);
        if (renderer.GetValue(entity) is not { } target) return;
        var attributes = entity.Properties.Attributes;
        int color = ColorUtil.ToRgba(255, attributes["colorR"].AsInt(), attributes["colorG"].AsInt(), attributes["colorB"].AsInt());
        setText.Invoke(target, [value[..Math.Min(4096, value.Length)], color]);
    }

    private static bool Take(Entity __instance, EntityAgent byEntity, EnumInteractMode mode)
    {
        if (byEntity is not EntityPlayer actor || mode != EnumInteractMode.Interact || !__instance.Alive) return false;
        var player = actor.Player;
        if (player?.InventoryManager.ActiveHotbarSlot.Empty != true) return false;
        var attrs = __instance.WatchedAttributes;
        bool locked = config.GetValue(null) is { } settings && (bool)lockSigned.GetValue(settings)!;
        if (locked && attrs.GetString("signedby", "") != "" && attrs.GetString("signedbyuid", "") != player.PlayerUID
            && !player.HasPrivilege(Privilege.buildblockseverywhere))
        {
            if (__instance.Api is ICoreClientAPI client)
                client.TriggerIngameError(__instance, "lockedparchment",
                    Lang.Get("billposting:billposting-error-parchmentlockedby", attrs.GetString("signedby")));
            return false;
        }
        // Wait for the server's removal packet; a failed or denied pickup must
        // never hide the paper only on the client.
        if (__instance.World.Side != EnumAppSide.Server) return false;
        if (!__instance.World.Claims.TryAccess(player, __instance.Pos.AsBlockPos, EnumBlockAccessFlags.BuildOrBreak)) return false;
        ItemStack? stack;
        try
        {
            byte[]? bytes = attrs.GetBytes("billpostdropstackbytes");
            stack = bytes != null ? new ItemStack(bytes) : attrs.GetItemstack("billpostdropstack")?.Clone();
            if (stack == null || stack.StackSize != 1 || !stack.ResolveBlockOrItem(__instance.World)) return false;
        }
        catch (Exception exception)
        {
            __instance.Api.Logger.Warning("[vsscpmod] Billpost {0} retained: invalid parchment data ({1}).",
                __instance.EntityId, exception.GetBaseException().Message);
            return false;
        }
        if (!actor.TryGiveItemStack(stack)) return false;
        player.InventoryManager.ActiveHotbarSlot.MarkDirty();
        __instance.Die(EnumDespawnReason.Removed);
        return false;
    }

    public void Dispose()
    {
        if (owners.Remove(this) && owners.Count == 0) harmony.UnpatchAll(HarmonyId);
    }
}
