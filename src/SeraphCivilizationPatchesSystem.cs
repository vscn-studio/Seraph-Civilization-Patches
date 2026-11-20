using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

public sealed class SeraphCivilizationPatchesSystem : ModSystem
{
    private CraftingCountRenderer? renderer;
    private RknFloatingCountCompatibility? rknFloatingCounts;
    private ImmersiveMapWaypointRenderer? mapWaypointRenderer;
    private CoordinateDisplayCompatibility? coordinateDisplay;
    private RknCraftingCompatibility? rknCrafting;
    private ToolsmithCraftingCompatibility? toolsmithCrafting;
    private InformationRestrictions? serverInformation;
    private InformationRestrictions? clientInformation;
    private LoggingCarryOnCompatibility? loggingCarryOn;
    private LoggingProgressCompatibility? loggingProgress;
    private FenceReinforcementCompatibility? fenceReinforcement;
    private InventoryInputDiagnostics? inventoryDiagnostics;
    private JauntStaminaHudCompatibility? jauntStaminaHud;
    private BillpostingFontCompatibility? billpostingFont;
    private BillpostingInteractionCompatibility? billpostingInteraction;
    private KometHotkeyCompatibility? kometHotkeys;
    private KometFontCompatibility? kometFonts;
    private BodyPhysMigration? bodyPhysMigration;
    private QuarryCarryCompatibility? quarryCarry;
    private QuarryDragCompatibility? quarryDrag;
    private QuarryLoadShaders? quarryShaders;
    private QuarryShipCompatibility? quarryShip;
    private ChromiumSulfateCookingCompatibility? chromiumSulfate;
    private PieBarrelTextureCompatibility? pieBarrelTexture;
    private EnvelopeEngravingCompatibility? envelopeEngraving;
    private CandelaClickUpTorchesCompatibility? candelaClickUpTorches;
    private VintageCanvasPaintCompatibility? vintageCanvasPaint;
    private VintageCanvasTextureCompatibility? vintageCanvasTexture;
    private LevelingSurvivalCompatibility? levelingSurvival;
    private LevelingSkillPanel? levelingPanel;
    private ClientMeshRecovery? clientMeshRecovery;
    private CapeRenderCompatibility? capeRenderCompatibility;
    private CreativeTabSelector? creativeTabSelector;
    private ChiselToolsHandPlanerCompatibility? chiselToolsHandPlaner;

    public override double ExecuteOrder() => 1.4;

    internal static bool UseIndependentMaps(IModLoader loader)
        => loader.IsModEnabled("immersivemaps") && !loader.IsModEnabled("vsscsmod");

    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockBehaviorClass("VsscpmodQuarryShipCargo", typeof(QuarryShipCargoBehavior));
        api.RegisterEntity("VsscpmodQuarryDrag", typeof(EntityQuarryDrag));
        ModCommands.Root(api);
        fenceReinforcement = FenceReinforcementCompatibility.TryCreate(api);
        loggingCarryOn = LoggingCarryOnCompatibility.TryCreate(api);
        quarryCarry = QuarryCarryCompatibility.TryCreate(api);
        quarryDrag = QuarryDragCompatibility.TryCreate(api);
        quarryShip = QuarryShipCompatibility.TryCreate(api);
        chromiumSulfate = ChromiumSulfateCookingCompatibility.TryCreate(api);
        vintageCanvasPaint = VintageCanvasPaintCompatibility.TryCreate(api);
        candelaClickUpTorches = CandelaClickUpTorchesCompatibility.TryCreate(api);
        billpostingInteraction = BillpostingInteractionCompatibility.TryCreate(api);
        levelingSurvival = LevelingSurvivalCompatibility.TryCreate(api);
        chiselToolsHandPlaner = ChiselToolsHandPlanerCompatibility.TryCreate(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        bodyPhysMigration = BodyPhysMigration.TryCreate(api);
        loggingCarryOn?.RegisterRecoveryCommand(api);
        serverInformation = new InformationRestrictions(api);
        rknCrafting = RknCraftingCompatibility.TryCreate(api);
        toolsmithCrafting = ToolsmithCraftingCompatibility.TryCreate(api);
        rknCrafting?.RegisterRecoveryCommand(api);
        api.Logger.Notification("[vsscpmod] 1.0.0 loaded. Information restrictions are configurable with /vsscpmod info reload; RKN crafting support is optional.");
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.RegisterEntityRendererClass("VsscpmodQuarryDrag", typeof(QuarryDragRenderer));
        quarryShaders = new QuarryLoadShaders(api);
        creativeTabSelector = CreativeTabSelector.TryCreate(api);
        clientMeshRecovery = ClientMeshRecovery.TryCreate(api);
        capeRenderCompatibility = CapeRenderCompatibility.TryCreate(api);
        levelingPanel = LevelingSkillPanel.TryCreate(api);
        vintageCanvasTexture = VintageCanvasTextureCompatibility.TryCreate(api);
        loggingProgress = LoggingProgressCompatibility.TryCreate(api);
        envelopeEngraving = EnvelopeEngravingCompatibility.TryCreate(api);
        pieBarrelTexture = PieBarrelTextureCompatibility.TryCreate(api);
        kometHotkeys = KometHotkeyCompatibility.TryCreate(api);
        kometFonts = KometFontCompatibility.TryCreate(api);
        jauntStaminaHud = JauntStaminaHudCompatibility.TryCreate(api);
        billpostingFont = BillpostingFontCompatibility.TryCreate(api);
        inventoryDiagnostics = new InventoryInputDiagnostics(api);
        clientInformation = new InformationRestrictions(api);
        coordinateDisplay = CoordinateDisplayCompatibility.TryCreate(api);
        rknCrafting = RknCraftingCompatibility.TryCreate(api);
        toolsmithCrafting = ToolsmithCraftingCompatibility.TryCreate(api);
        if (api.ModLoader.IsModEnabled("rkncrafting"))
        {
            renderer = new CraftingCountRenderer(api);
            api.Event.RegisterRenderer(renderer, EnumRenderStage.Ortho, "vsscpmod-crafting-counts");
            rknFloatingCounts = RknFloatingCountCompatibility.TryCreate(api, renderer);
            api.Logger.Notification("[vsscpmod] Floating crafting stack counts enabled.");
        }
        if (UseIndependentMaps(api.ModLoader))
        {
            mapWaypointRenderer = new ImmersiveMapWaypointRenderer(api);
            api.Event.RegisterRenderer(mapWaypointRenderer, EnumRenderStage.Ortho, "vsscpmod-immersive-map-waypoints");
            api.Logger.Notification("[vsscpmod] Immersive Maps waypoint strip enabled.");
        }
    }

    public override void Dispose()
    {
        quarryShaders?.Dispose();
        quarryShaders = null;
        creativeTabSelector?.Dispose();
        creativeTabSelector = null;
        chiselToolsHandPlaner?.Dispose();
        chiselToolsHandPlaner = null;
        capeRenderCompatibility?.Dispose();
        capeRenderCompatibility = null;
        clientMeshRecovery?.Dispose();
        clientMeshRecovery = null;
        levelingPanel?.Dispose();
        levelingPanel = null;
        levelingSurvival?.Dispose();
        levelingSurvival = null;
        bodyPhysMigration?.Dispose();
        bodyPhysMigration = null;
        vintageCanvasTexture?.Dispose();
        vintageCanvasTexture = null;
        vintageCanvasPaint?.Dispose();
        vintageCanvasPaint = null;
        candelaClickUpTorches?.Dispose();
        candelaClickUpTorches = null;
        envelopeEngraving?.Dispose();
        envelopeEngraving = null;
        pieBarrelTexture?.Dispose();
        pieBarrelTexture = null;
        chromiumSulfate?.Dispose();
        chromiumSulfate = null;
        toolsmithCrafting?.Dispose();
        toolsmithCrafting = null;
        quarryShip?.Dispose();
        quarryShip = null;
        quarryCarry?.Dispose();
        quarryCarry = null;
        quarryDrag?.Dispose();
        quarryDrag = null;
        kometHotkeys?.Dispose();
        kometHotkeys = null;
        kometFonts?.Dispose();
        kometFonts = null;
        jauntStaminaHud?.Dispose();
        jauntStaminaHud = null;
        billpostingFont?.Dispose();
        billpostingFont = null;
        billpostingInteraction?.Dispose();
        billpostingInteraction = null;
        inventoryDiagnostics?.Dispose();
        inventoryDiagnostics = null;
        loggingCarryOn?.Dispose();
        loggingCarryOn = null;
        loggingProgress?.Dispose();
        loggingProgress = null;
        fenceReinforcement?.Dispose();
        fenceReinforcement = null;
        clientInformation?.Dispose();
        clientInformation = null;
        serverInformation?.Dispose();
        serverInformation = null;
        coordinateDisplay?.Dispose();
        coordinateDisplay = null;
        rknCrafting?.Dispose();
        rknCrafting = null;
        rknFloatingCounts?.Dispose();
        rknFloatingCounts = null;
        renderer?.Dispose();
        renderer = null;
        mapWaypointRenderer?.Dispose();
        mapWaypointRenderer = null;
        base.Dispose();
    }
}
