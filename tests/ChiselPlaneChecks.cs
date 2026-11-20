using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

static class ChiselPlaneChecks
{
    public static void Run(string dll)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(dll));
        var type = assembly.GetType("chisel.src.ItemPantograph", true)!;
        var tool = (Item)Activator.CreateInstance(type, true)!;
        var method = AccessTools.Method(type, "ModifyChiseledBlock");
        int checks = 0;
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); checks++; }
        var logger = Proxy.Create<ILogger>((m, a) => { if (m.Name == "Warning") throw new Exception(string.Join(" ", a ?? [])); return null; });
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? true : null);
        var stone = new Block { BlockId = 1, Code = new AssetLocation("game:rock-granite") };
        var be = new BlockEntityChisel { Pos = new BlockPos(5, 6, 7), BlockIds = [1, 2] };
        var accessor = Proxy.Create<IBlockAccessor>((m, _) => m.Name switch
        { "GetBlockEntity" => be, "GetBlock" => stone, _ => null });
        var world = Proxy.Create<IWorldAccessor>((m, _) => m.Name switch
        { "get_BlockAccessor" => accessor, "GetBlock" => stone, "get_Side" => EnumAppSide.Server, "get_Logger" => logger, _ => null });
        var api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_Side" => EnumAppSide.Server, "get_ModLoader" => loader, "get_Logger" => logger, _ => null });
        be.Api = api;
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(tool, api);
        void Seed()
        {
            var c = new CuboidWithMaterial(); c.Set(0, 0, 0, 16, 16, 16); c.Material = 0;
            be.VoxelCuboids = [BlockEntityMicroBlock.ToUint(c)];
        }
        BlockSelection Hit(BlockFacing face, double depth)
        {
            var hit = new Vec3d(0.55, 0.55, 0.55); var n = face.Normali;
            if (n.X != 0) hit.X = depth; else if (n.Y != 0) hit.Y = depth; else hit.Z = depth;
            return new BlockSelection { Position = be.Pos.Copy(), Face = face, HitPosition = hit };
        }
        int Apply(BlockSelection hit, string mode = "PLANEALL") => (int)method.Invoke(tool, [hit, mode, false, null])!;
        bool Occupied(int x, int y, int z) => be.GetVoxelMaterialAt(new Vec3i(x, y, z)) != 0;
        Seed(); Apply(Hit(BlockFacing.UP, 0.5 - 1e-8));
        Check(!Occupied(8, 6, 8) && Occupied(8, 7, 8), "Unpatched pantograph must reproduce wrong depth below positive face");
        var compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.ChiselToolsHandPlanerCompatibility", true)!;
        using var owner = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [api]);
        Check(owner != null, "Pantograph compatibility installs");
        var modeSlot = new DummySlot(new ItemStack(tool));
        foreach (int mode in new[] { 12, 13, 14 })
        {
            tool.SetToolMode(modeSlot, null!, null!, mode);
            Check(modeSlot.Itemstack!.Attributes.GetInt("toolMode", -1) == mode
                && modeSlot.Itemstack!.Attributes.GetInt("lastToolMode", -1) == mode,
                "Real pantograph mode setter preserves its distinct mode indices");
        }
        foreach (var face in BlockFacing.ALLFACES)
        foreach (double depth in new[] { 0.25, 0.5, 0.75 })
        foreach (double error in new[] { 0.0, -1e-8, -1.0 / 16384 })
        {
            Seed(); var selection = Hit(face, depth + error); var before = selection.HitPosition.Clone();
            Check(Apply(selection) == 256, "Plane reports 256 voxel operations");
            int layer = (int)(depth * 16) - (face.Normali.X + face.Normali.Y + face.Normali.Z > 0 ? 1 : 0);
            be.ConvertToVoxels(out var voxels, out _);
            for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++)
            {
                int coordinate = face.Normali.X != 0 ? x : face.Normali.Y != 0 ? y : z;
                Check(voxels[x, y, z] == (coordinate != layer), $"Unexpected voxel at {x},{y},{z}, face {face}, depth {depth}, error {error}");
            }
            Check(selection.HitPosition.Equals(before), "Caller selection preserved");
        }
        Seed(); Check(Apply(Hit(BlockFacing.UP, double.NaN)) == 0 && Occupied(8, 8, 8), "Invalid hit must not edit");
        uint Cube(int x1, int y1, int z1, int x2, int y2, int z2, byte material)
        { var c = new CuboidWithMaterial(); c.Set(x1, y1, z1, x2, y2, z2); c.Material = material; return BlockEntityMicroBlock.ToUint(c); }
        be.VoxelCuboids = [Cube(0, 0, 0, 8, 16, 16, 0), Cube(8, 0, 0, 16, 16, 16, 1)];
        Apply(Hit(BlockFacing.UP, 1 - 1e-8), "PLANEMATERIAL");
        be.ConvertToVoxels(out var materialVoxels, out _);
        for (int x = 0; x < 16; x++) for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++)
            Check(materialVoxels[x, y, z] == !(x >= 8 && y == 15), "Material mode removes only targeted material on one plane");
        be.VoxelCuboids = [Cube(0, 0, 0, 16, 15, 16, 0), Cube(7, 15, 7, 10, 16, 10, 1), Cube(1, 15, 1, 3, 16, 3, 1)];
        Apply(Hit(BlockFacing.UP, 1 - 1e-8), "PLANEISLAND");
        Check(!Occupied(8, 15, 8) && Occupied(1, 15, 1) && Occupied(8, 14, 8), "Island mode preserves disconnected island and lower plane");
        be.VoxelCuboids = [Cube(0, 0, 0, 16, 8, 16, 0)];
        Check((int)method.Invoke(tool, [Hit(BlockFacing.UP, 0.5 - 1e-8), "PLANEALL", true, null])! == 256, "Adding a plane remains supported");
        Check(Occupied(8, 8, 8) && !Occupied(8, 9, 8), "Added plane lies outside the hit surface");
        var standaloneType = assembly.GetType("chisel.src.ItemHandPlaner", true)!;
        var standalone = (Item)Activator.CreateInstance(standaloneType, true)!;
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(standalone, api);
        Seed();
        AccessTools.Method(standaloneType, "ModifyChiseledBlock").Invoke(standalone,
            [Hit(BlockFacing.UP, 0.5 - 1e-8), Enum.ToObject(standaloneType.GetNestedType("enModes")!, 0), false, null]);
        Check(!Occupied(8, 7, 8) && Occupied(8, 6, 8), "Independent planer uses same corrected plane selection");
        Seed(); var edge = Hit(BlockFacing.UP, 1); edge.HitPosition.X = 1; edge.HitPosition.Z = 1;
        Apply(edge, "PLANEISLAND");
        Check(!Occupied(15, 15, 15) && Occupied(15, 14, 15), "Corner hit clamps tangential coordinates to valid voxels");
        Seed(); Check(Apply(Hit(BlockFacing.UP, 0.53)) == 0 && Occupied(8, 8, 8), "Non-surface hit is rejected");
        owner!.Dispose();
        Check(Harmony.GetPatchInfo(method)?.Owners.Contains("vsscpmod.chiseltools-hand-planer") != true, "Pantograph unpatches on disposal");
        Seed(); Apply(Hit(BlockFacing.UP, 0.5 - 1e-8));
        Check(!Occupied(8, 6, 8) && Occupied(8, 7, 8), "Unpatch restores original faulty depth selection");
        Console.WriteLine($"{checks} actual pantograph voxel checks passed.");
    }
}
