using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

static class FenceReinforcementChecks
{
    private sealed class NeighbourProbe(Block block) : BlockBehavior(block)
    {
        public int Updates;
        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neighbour, ref EnumHandling handling)
            => Updates++;
    }

    public static void Run(string game)
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        var type = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.FenceReinforcementCompatibility")!;
        var method = AccessTools.DeclaredMethod(typeof(BlockFence), nameof(BlockFence.OnNeighbourBlockChange));
        var pos = new BlockPos(10, 10, 10);
        int key = (10 << 16) | (10 << 8) | 10;
        byte[] data = [];
        var reinforcement = new ModSystemBlockReinforcement();
        var logger = Proxy.Create<ILogger>((m, a) =>
        {
            if (m.Name == "Warning") throw new Exception("Unexpected warning: " + a![0] + " " + string.Join(", ", (object[])a[1]!));
            return null;
        });
        var chunk = Proxy.Create<IWorldChunk>((m, a) =>
        {
            if (m.Name == "SetModdata") data = (byte[])a![1]!;
            return m.Name == "GetModdata" ? data : null;
        });
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "GetModSystem" ? reinforcement : null);
        var blocks = new Dictionary<string, BlockFence>();
        BlockFence current = null!;
        IWorldAccessor world = null!;
        int mask = 0, placed = 0, exchanged = 0, updates = 0;
        bool missingVariant = false;
        var neighbour = new Block { Attributes = JsonObject.FromJson("{\"fenceConnect\":{\"north\":true,\"east\":true,\"south\":true,\"west\":true}}") };
        var air = new Block { Attributes = JsonObject.FromJson("{\"fenceConnect\":{\"north\":false,\"east\":false,\"south\":false,\"west\":false}}") };
        var accessor = Proxy.Create<IBlockAccessor>((m, a) =>
        {
            switch (m.Name)
            {
                case "GetChunkAtBlockPos": case "GetChunk": return chunk;
                case "GetBlock":
                    if (a![0] is AssetLocation code) return missingVariant ? null : blocks.GetValueOrDefault(code.Path);
                    if (a[0] is int id) return blocks.Values.Single(b => b.BlockId == id);
                    var at = (BlockPos)a[0]!;
                    if (at.Equals(pos)) return current;
                    int direction = at.Z < pos.Z ? 0 : at.X > pos.X ? 1 : at.Z > pos.Z ? 2 : 3;
                    return (mask & (1 << direction)) != 0 ? neighbour : air;
                case "SetBlock":
                    current = blocks.Values.Single(b => b.BlockId == (int)a![0]!);
                    placed++;
                    current.OnBlockPlaced(world!, (BlockPos)a![1]!, a.Length > 2 ? a[2] as ItemStack : null);
                    break;
                case "ExchangeBlock":
                    current = blocks.Values.Single(b => b.BlockId == (int)a![0]!);
                    exchanged++;
                    break;
                case "TriggerNeighbourBlockUpdate": updates++; break;
            }
            return null;
        });
        EnumAppSide side = EnumAppSide.Server;
        ICoreAPI api = null!;
        world = Proxy.Create<IWorldAccessor>((m, _) => m.Name switch
        {
            "get_BlockAccessor" => accessor, "get_Api" => api, "get_Side" => side, "get_Logger" => logger, _ => null
        });
        api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_ModLoader" => loader, "get_Side" => side, "get_Logger" => logger, _ => null
        });
        AccessTools.Field(typeof(ModSystemBlockReinforcement), "api").SetValue(reinforcement, api);
        void Seed() => data = SerializerUtil.Serialize(new Dictionary<int, BlockReinforcement>
        {
            [key] = new() { Strength = 100, PlayerUID = "owner", LastPlayername = "Owner", GroupUid = 7,
                LastGroupname = "Group", Locked = true, LockedByItemCode = "game:padlock" }
        });
        void Setup(Type blockType, string prefix)
        {
            blocks.Clear();
            for (int bits = 0; bits < 16; bits++)
            {
                string shape = string.Concat("nesw".Where((_, i) => (bits & (1 << i)) != 0));
                if (shape.Length == 0) shape = "empty";
                var block = (BlockFence)Activator.CreateInstance(blockType)!;
                block.BlockId = bits + 1;
                block.Code = new("game", prefix + "-" + shape + "-free");
                block.VariantStrict.Add("type", shape);
                block.VariantStrict.Add("cover", "free");
                block.BlockBehaviors = [new BlockBehaviorReinforcable(block), new NeighbourProbe(block)];
                blocks.Add(block.Code.Path, block);
            }
            current = blocks.Values.First();
            mask = 1;
            placed = exchanged = updates = 0;
            Seed();
        }
        Setup(typeof(BlockFence), "woodenfence");
        current.OnNeighbourBlockChange(world, pos, pos.NorthCopy());
        Check(placed == 1 && reinforcement.GetReinforcment(pos) == null,
            "vanilla shape change reproduces deletion through real placement behavior and serialized chunk data");

        var owner = (IDisposable?)type.GetMethod("TryCreate")!.Invoke(null, [api]);
        Check(owner != null && Harmony.GetPatchInfo(method)?.Owners.Contains("vsscpmod.fence-reinforcement") == true,
            "patch installs on actual game method");
        try
        {
            foreach (var (blockType, prefix, asset) in new[] {
                (typeof(BlockFence), "woodenfence", "wood/woodtyped/fence-free.json"),
                (typeof(BlockWattleFence), "wattle", "wood/wattlefence.json"),
                (typeof(BlockFenceStackAware), "drystonefence", "stone/drystonefence-free.json") })
            {
                Check(File.ReadAllText(Path.Combine(game, "assets/survival/blocktypes", asset)).Contains("class: \"" + blockType.Name + "\""),
                    prefix + " uses the tested class in installed game assets");
                foreach (var appSide in new[] { EnumAppSide.Server, EnumAppSide.Client })
                {
                    side = appSide;
                    Setup(blockType, prefix);
                    byte[] original = data.ToArray();
                    // Add/remove neighbours through every connection mask and back.
                    foreach (int bits in Enumerable.Range(0, 16).Concat(Enumerable.Range(0, 16).Reverse()))
                    {
                        mask = bits;
                        int before = exchanged, beforeUpdates = updates;
                        bool changes = current.GetOrientations(world, pos) != current.Variant["type"];
                        current.OnNeighbourBlockChange(world, pos, pos.NorthCopy());
                        Check(current.Variant["type"] == current.GetOrientations(world, pos) && placed == 0
                            && exchanged == before + (changes ? 1 : 0) && updates == beforeUpdates + (changes ? 1 : 0)
                            && data.SequenceEqual(original), $"{prefix} {side} connections {bits}: shape, updates and reinforcement preserved");
                    }
                    var saved = reinforcement.GetReinforcment(pos);
                    Check(saved.Strength == 100 && saved.PlayerUID == "owner" && saved.GroupUid == 7
                        && saved.LastPlayername == "Owner" && saved.LastGroupname == "Group" && saved.Locked
                        && saved.LockedByItemCode == "game:padlock", prefix + " " + side + " ownership and lock survive serialization");
                    var probe = current.BlockBehaviors.OfType<NeighbourProbe>().Single();
                    int prior = probe.Updates;
                    current.OnNeighbourBlockChange(world, pos, pos.NorthCopy());
                    Check(probe.Updates == prior + 1, prefix + " " + side + " unchanged shape retains base behaviors");
                    mask = 15;
                    missingVariant = true;
                    int previous = exchanged;
                    current.OnNeighbourBlockChange(world, pos, pos.NorthCopy());
                    Check(exchanged == previous && data.SequenceEqual(original), prefix + " " + side + " missing variant leaves block intact");
                    missingVariant = false;
                }
            }
            side = EnumAppSide.Server;
            Seed();
            accessor.SetBlock(current.BlockId, pos);
            Check(reinforcement.GetReinforcment(pos) == null, "real placement still clears stale reinforcement");
            mask = 1;
            current.OnNeighbourBlockChange(world, pos, pos.NorthCopy());
            Check(reinforcement.GetReinforcment(pos) == null, "unreinforced connection change does not invent reinforcement");
            Seed();
            var handling = EnumHandling.PassThrough;
            current.BlockBehaviors.OfType<BlockBehaviorReinforcable>().Single()
                .OnBlockExploded(world, pos, pos, default, ref handling);
            Check(handling == EnumHandling.PreventDefault && reinforcement.GetReinforcment(pos).Strength == 98,
                "explosion still consumes reinforcement and prevents breaking");
            reinforcement.ConsumeStrength(pos, 98);
            Check(reinforcement.GetReinforcment(pos) == null, "exhausted reinforcement is still removed");
            var replacement = (IDisposable?)type.GetMethod("TryCreate")!.Invoke(null, [api]);
            owner!.Dispose();
            Check(replacement != null && Harmony.GetPatchInfo(method)?.Owners.Contains("vsscpmod.fence-reinforcement") == true,
                "reinitialization and disposal of old instance preserve replacement patch");
            replacement!.Dispose();
        }
        finally { owner?.Dispose(); }
        Check(Harmony.GetPatchInfo(method)?.Owners.Contains("vsscpmod.fence-reinforcement") != true,
            "disposal removes patch");
        Setup(typeof(BlockFence), "woodenfence");
        current.OnNeighbourBlockChange(world, pos, pos.NorthCopy());
        Check(placed == 1 && reinforcement.GetReinforcment(pos) == null, "unpatch restores vanilla behavior");
        Console.WriteLine($"{checks} fence reinforcement checks passed.");
    }
}
