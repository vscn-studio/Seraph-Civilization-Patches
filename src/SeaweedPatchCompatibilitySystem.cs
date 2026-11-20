using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

/// <summary>Make Ashes' append safe before the vanilla JSON patch loader runs.</summary>
public sealed class SeaweedPatchCompatibilitySystem : ModSystem
{
    // Vanilla applies every mod's JSON patches at 0.05. Fixing the block asset
    // afterwards cannot prevent an earlier /drops/- path-not-found error.
    public override double ExecuteOrder() => 0.04;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server || !api.ModLoader.IsModEnabled("ashes")) return;
        IAsset? asset = api.Assets.TryGet(new AssetLocation("ashes:patches/survival-blocktypes-plant-aquatic-seaweed.json"));
        if (asset == null) return;

        try
        {
            if (JsonNode.Parse(asset.ToText(), documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonArray patches) return;
            bool changed = false;
            foreach (JsonNode? node in patches)
            {
                if (node is not JsonObject patch
                    || patch["file"]?.GetValue<string>() != "game:blocktypes/plant/aquatic/seaweed.json"
                    || !string.Equals(patch["op"]?.GetValue<string>(), "addmerge", StringComparison.OrdinalIgnoreCase)
                    || patch["path"]?.GetValue<string>() != "/drops/-"
                    || patch["value"] is not JsonObject drop
                    || drop["code"]?.GetValue<string>() != "ashes:plantdebris-seaweed") continue;

                // Array addmerge preserves existing drops and creates the array
                // when Wild Farming has removed it. Retain all patch conditions.
                patch["value"] = new JsonArray(drop.DeepClone());
                patch["path"] = "/drops";
                changed = true;
            }
            if (!changed) return;
            asset.Data = Encoding.UTF8.GetBytes(patches.ToJsonString());
            api.Logger.Notification("[vsscpmod] Ashes seaweed patch now tolerates missing drops before JSON patch loading.");
        }
        catch (Exception error)
        {
            api.Logger.Warning("[vsscpmod] Could not prepare Ashes seaweed patch: {0}", error.Message);
        }
    }
}
