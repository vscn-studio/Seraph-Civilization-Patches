using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Installed exclusively by StartClientSide. Never writes persistent item/player data.
internal sealed class ClientMeshRecovery : IDisposable
{
    private const string HarmonyId = "vsscpmod.client-mesh-recovery";
    private const string CacheKey = "ToolsmithModularPartRenderingMeshRefs";
    private const string StackKey = "toolsmithMeshrefID";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<ClientMeshRecovery> owners = [];
    private static ConditionalWeakTable<ItemStack, RetryState> tools = new();
    private static ConditionalWeakTable<EntityPlayerShapeRenderer, RetryState> players = new();
    private static ConditionalWeakTable<EntityPlayer, RetryState> hiddenPlayers = new();
    private static readonly FieldInfo thirdPerson = AccessTools.Field(typeof(EntityPlayerShapeRenderer), "thirdPersonMeshRef");
    private static readonly FieldInfo loaded = AccessTools.Field(typeof(EntityShapeRenderer), "loaded");
    private static readonly FieldInfo playerEntity = AccessTools.Field(typeof(EntityPlayerShapeRenderer), "entityPlayer");
    private static readonly FieldInfo spectator = AccessTools.Field(typeof(EntityShapeRenderer), "isSpectator");
    private static readonly FieldInfo renderMode = AccessTools.Field(typeof(EntityPlayerShapeRenderer), "renderMode");

    private sealed class RetryState
    {
        public long NextCheck;
        public long MissingSince;
        public long PendingSince;
        public int Attempts;
        public long FirstSeen;
        public string? LastDiagnostic;
        public int Diagnostics;
        public bool ReportedHidden;
    }

    private sealed record ToolState(MultiTextureMeshRef? Original, Dictionary<int, MultiTextureMeshRef> Cache, int NewId);

    internal static bool Usable(MultiTextureMeshRef? mesh) => mesh is { Disposed: false }
        && mesh.meshrefs is { Length: > 0 } refs && mesh.textureids?.Length == refs.Length
        && refs.All(part => part is { Disposed: false, Initialized: true });

    public static ClientMeshRecovery? TryCreate(ICoreClientAPI api)
    {
        if (owners.Count == 0)
        {
            try
            {
                if (thirdPerson == null || loaded == null || playerEntity == null)
                    throw new MissingFieldException("Native player renderer mesh fields");
                harmony.Patch(AccessTools.Method(typeof(EntityPlayerShapeRenderer), nameof(EntityPlayerShapeRenderer.BeforeRender)),
                    postfix: new HarmonyMethod(typeof(ClientMeshRecovery), nameof(CheckPlayer)));
                harmony.Patch(AccessTools.Method(typeof(EntityShapeRenderer), nameof(EntityShapeRenderer.TesselateShape),
                    [typeof(Action<MeshData>), typeof(string[])]),
                    prefix: new HarmonyMethod(typeof(ClientMeshRecovery), nameof(TrackTesselation)));
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(HarmonyId);
                api.Logger.Warning("[vsscpmod] Client player mesh recovery unavailable: {0}", e.GetBaseException().Message);
                return null;
            }
            if (api.ModLoader.IsModEnabled("dead"))
            {
                // Restrict only Dead's corpse-hiding rule; never overwrite the engine's spectator state.
                var deadHarmony = new Harmony(HarmonyId + ".dead");
                try
                {
                    var type = AccessTools.TypeByName("Dead.Patches.HiddenBodyRenderPatch")
                        ?? throw new TypeLoadException("Dead hidden body rendering");
                    deadHarmony.Patch(AccessTools.DeclaredMethod(type, "Postfix", [typeof(EntityShapeRenderer)]),
                        prefix: new HarmonyMethod(typeof(ClientMeshRecovery), nameof(BeforeDeadHide)));
                    api.Logger.Notification("[vsscpmod] Client Dead living-player visibility compatibility enabled.");
                }
                catch (Exception e)
                {
                    deadHarmony.UnpatchAll(deadHarmony.Id);
                    api.Logger.Warning("[vsscpmod] Dead visibility compatibility unavailable: {0}", e.GetBaseException().Message);
                }
            }
            if (api.ModLoader.IsModEnabled("toolsmith"))
            {
                // Keep player recovery even if an unsupported Toolsmith version is installed.
                var toolHarmony = new Harmony(HarmonyId + ".toolsmith");
                try
                {
                    var type = AccessTools.TypeByName("Toolsmith.Client.Behaviors.ModularPartRenderingFromAttributes")
                        ?? throw new TypeLoadException("Toolsmith modular rendering");
                    toolHarmony.Patch(AccessTools.DeclaredMethod(type, "OnBeforeRender"),
                        prefix: new HarmonyMethod(typeof(ClientMeshRecovery), nameof(BeforeTool)),
                        postfix: new HarmonyMethod(typeof(ClientMeshRecovery), nameof(AfterTool)));
                    toolHarmony.Patch(AccessTools.DeclaredMethod(type, "GenMesh", [typeof(ItemSlot), typeof(ITextureAtlasAPI), typeof(Vintagestory.API.MathTools.BlockPos)]),
                        postfix: new HarmonyMethod(typeof(ClientMeshRecovery), nameof(RejectEmptyMesh)));
                }
                catch (Exception e)
                {
                    toolHarmony.UnpatchAll(toolHarmony.Id);
                    api.Logger.Warning("[vsscpmod] Toolsmith mesh recovery unavailable: {0}", e.GetBaseException().Message);
                }
            }
        }
        var owner = new ClientMeshRecovery();
        owners.Add(owner);
        api.Logger.Notification("[vsscpmod] Client-only tool cache and player mesh/animation recovery enabled; player visibility diagnostics active.");
        return owner;
    }

    private static bool BeforeDeadHide(EntityShapeRenderer __0)
    {
        if (__0.entity is not EntityPlayer { Alive: true } entity) return true;
        if (entity.WatchedAttributes.GetBool("deadBodyHidden"))
        {
            var state = hiddenPlayers.GetOrCreateValue(entity);
            if (!state.ReportedHidden)
            {
                state.ReportedHidden = true;
                entity.Api?.Logger.Warning("[vsscpmod] Ignored stale Dead body-hidden flag on living player entity {0}; synchronized attributes unchanged.", entity.EntityId);
            }
        }
        return false;
    }

    private static bool BeforeTool(ICoreClientAPI capi, ItemStack itemstack, ItemRenderInfo renderinfo, out ToolState? __state)
    {
        __state = null;
        var retry = tools.GetOrCreateValue(itemstack);
        if (Environment.TickCount64 < retry.NextCheck) return false;
        if (!capi.ObjectCache.TryGetValue(CacheKey, out var value))
            capi.ObjectCache[CacheKey] = value = new Dictionary<int, MultiTextureMeshRef>();
        if (value is not Dictionary<int, MultiTextureMeshRef> cache) return true;
        int id = itemstack.TempAttributes.GetInt(StackKey);
        if (id != 0 && cache.TryGetValue(id, out var mesh) && Usable(mesh)) return true;
        if (id != 0) itemstack.TempAttributes.SetInt(StackKey, 0);
        // Do not remove old IDs: Toolsmith allocates by Count+1, so holes cause collisions.
        int next = cache.Count + 1;
        if (cache.ContainsKey(next) && itemstack.TempAttributes.GetInt(StackKey) == 0)
        {
            retry.NextCheck = Environment.TickCount64 + 5000;
            if (retry.Attempts++ == 0)
                capi.Logger.Warning("[vsscpmod] Toolsmith cache ID collision for {0}; retaining default model.", itemstack.Collectible.Code);
            return false;
        }
        __state = new ToolState(renderinfo.ModelRef, cache, cache.ContainsKey(next) ? 0 : next);
        return true;
    }

    private static void RejectEmptyMesh(ref MeshData? __result)
    {
        if (__result != null && (__result.VerticesCount == 0 || __result.IndicesCount == 0)) __result = null;
    }

    private static void AfterTool(ICoreClientAPI capi, ItemStack itemstack, ItemRenderInfo renderinfo, ToolState? __state)
    {
        if (__state == null) return;
        int id = itemstack.TempAttributes.GetInt(StackKey);
        bool borrowed = id == __state.NewId && ReferenceEquals(renderinfo.ModelRef, __state.Original);
        if (Usable(renderinfo.ModelRef) && !borrowed) return;
        // Never let Toolsmith own/dispose the engine's default model on unload.
        if (id == __state.NewId && id != 0)
        {
            __state.Cache.Remove(id);
            var rejected = renderinfo.ModelRef;
            if (!borrowed && rejected is { Disposed: false } && rejected.meshrefs != null
                && rejected.meshrefs.All(part => part != null)) rejected.Dispose();
        }
        itemstack.TempAttributes.SetInt(StackKey, 0);
        if (Usable(__state.Original)) renderinfo.ModelRef = __state.Original;
        var retry = tools.GetOrCreateValue(itemstack);
        retry.NextCheck = Environment.TickCount64 + 5000;
        if (retry.Attempts++ == 0)
            capi.Logger.Warning("[vsscpmod] Toolsmith mesh missing/invalid for {0}; retaining default model and retrying after 5 seconds.", itemstack.Collectible.Code);
    }

    private static void TrackTesselation(EntityShapeRenderer __instance, ref Action<MeshData> onMeshDataReady)
    {
        if (__instance.GetType() != typeof(EntityPlayerShapeRenderer)) return;
        var state = players.GetOrCreateValue((EntityPlayerShapeRenderer)__instance);
        state.PendingSince = Environment.TickCount64;
        var callback = onMeshDataReady;
        onMeshDataReady = mesh =>
        {
            try { callback(mesh); }
            finally { state.PendingSince = 0; }
        };
    }

    private static void CheckPlayer(EntityPlayerShapeRenderer __instance)
    {
        var state = players.GetOrCreateValue(__instance);
        long now = Environment.TickCount64;
        if (now < state.NextCheck) return;
        state.NextCheck = now + 1000;
        if (playerEntity.GetValue(__instance) is not EntityPlayer entity) return;
        if (state.FirstSeen == 0) state.FirstSeen = now;
        bool meshReady = Usable((MultiTextureMeshRef?)thirdPerson.GetValue(__instance));
        bool animatorReady = entity.AnimManager?.Animator != null;
        if (state.Diagnostics < 4 && now - state.FirstSeen >= 3000)
        {
            string diagnostic = $"renderer={__instance.GetType().FullName}, loaded={loaded.GetValue(__instance)}, mesh={meshReady}, animator={animatorReady}, fresh={entity.ShapeFresh}, alive={entity.Alive}, deadHidden={entity.WatchedAttributes.GetBool("deadBodyHidden")}, spectator={spectator?.GetValue(__instance)}, mode={renderMode?.GetValue(__instance)}, color={entity.RenderColor:X8}";
            if (state.LastDiagnostic != diagnostic)
            {
                entity.Api?.Logger.Notification("[vsscpmod] Player visibility entity {0}: {1}", entity.EntityId, diagnostic);
                state.LastDiagnostic = diagnostic;
                state.Diagnostics++;
            }
        }
        // Custom renderers may intentionally use another mesh or hide a player.
        if (__instance.GetType() != typeof(EntityPlayerShapeRenderer)) return;
        if (meshReady && animatorReady)
        {
            state.MissingSince = 0;
            state.Attempts = 0;
            return;
        }
        if (loaded.GetValue(__instance) is not true || !entity.ShapeFresh) return;
        if (state.MissingSince == 0) { state.MissingSince = now; return; }
        if (now - state.MissingSince < 3000 || state.Attempts >= 3
            || (state.PendingSince != 0 && now - state.PendingSince < 15000)) return;
        entity.MarkShapeModified();
        state.Attempts++;
        state.NextCheck = now + 5000;
        entity.Api?.Logger.Warning("[vsscpmod] Missing player mesh/animator for entity {0}; client rebuild attempt {1}/3.", entity.EntityId, state.Attempts);
    }

    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        harmony.UnpatchAll(HarmonyId);
        harmony.UnpatchAll(HarmonyId + ".toolsmith");
        harmony.UnpatchAll(HarmonyId + ".dead");
        tools = new();
        players = new();
        hiddenPlayers = new();
    }
}
