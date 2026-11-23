using System.Reflection;
using System.Runtime.Loader;

string root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? ".");
string game = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "E:/vintagestory/Vintagestory");
string suite = args.ElementAtOrDefault(2) ?? "core";
string extra = args.ElementAtOrDefault(3) ?? "";
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (var directory in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string file = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
switch (suite)
{
    case "core":
        CoexistenceChecks.Run(root, game);
        InformationChecks.Run();
        SeaweedPatchChecks.Run(root);
        FenceReinforcementChecks.Run(game);
        InventoryDiagnosticsChecks.Run();
        break;
    case "quarry": QuarryChecks.Run(root); break;
    case "candela": CandelaChecks.Run(game); break;
    case "coexistence": CoexistenceChecks.Run(root, game); break;
    case "rkn": RknChecks.Run(root, extra); break;
    case "toolsmith": ToolsmithCraftingChecks.Run(root); break;
    case "cape-render": CapeRenderChecks.Run(extra); break;
    case "client-mesh": ClientMeshRecoveryChecks.Run(extra, args.ElementAtOrDefault(4)); break;
    case "logging": LoggingChecks.Run(root, extra); break;
    case "logging-progress": LoggingProgressChecks.Run(game, extra); break;
    case "canvas-paint": VintageCanvasPaintChecks.Run(extra, args.ElementAtOrDefault(4)); break;
    case "canvas-texture": VintageCanvasTextureChecks.Run(extra); break;
    case "pie": PieBarrelTextureChecks.Run(game); break;
    case "jaunt": JauntHudChecks.Run(extra, game); break;
    case "komet": KometHotkeyChecks.Run(root, game, extra); break;
    case "komet-primitive": KometPrimitiveSurvivalChecks.Run(root, game, extra); break;
    case "komet-font": KometFontChecks.Run(root, game, extra); break;
    case "bodyphys-migration": BodyPhysMigrationChecks.Run(root, game, extra); break;
    case "leveling-survival": LevelingSurvivalChecks.Run(root, extra); break;
    case "billposting": BillpostingChecks.Run(root, game, extra); break;
    case "billposting-interaction": BillpostingInteractionChecks.Run(root, extra); break;
    case "startup-assets": StartupAssetChecks.Run(root, game, extra); break;
    case "startup-recipes": StartupRecipeChecks.Run(root, extra); break;
    case "chromium": ChromiumSulfateChecks.Run(game, extra, args.ElementAtOrDefault(4)); break;
    case "longtermfood-meals": LongTermFoodMealChecks.Run(game, extra); break;
    case "fromgoldencombs": FromGoldenCombsChecks.Run(extra); break;
    default: throw new ArgumentException("Unknown test suite: " + suite);
}
Console.WriteLine("Suite passed: " + suite);
