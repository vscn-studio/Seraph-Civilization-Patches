using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

/// <summary>Correct Heraldry Capes' array paths before vanilla applies its conditional patches.</summary>
public sealed class CapeRecipePatchCompatibilitySystem : ModSystem
{
    public override double ExecuteOrder() => 0.04;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server || !api.ModLoader.IsModEnabled("capes")) return;
        IAsset? asset = api.Assets.TryGet(new AssetLocation("capes:patches/disable.json"));
        if (asset == null) return;
        try
        {
            if (JsonNode.Parse(asset.ToText(), documentOptions: new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonArray patches) return;
            int changed = 0;
            foreach (JsonNode? node in patches)
            {
                if (node is not JsonObject patch
                    || patch["file"]?.GetValue<string>() != "capes:recipes/grid/cape-to-banner.json"
                    || !string.Equals(patch["op"]?.GetValue<string>(), "addmerge", StringComparison.OrdinalIgnoreCase)
                    || patch["value"]?.ToJsonString() != "false") continue;
                string? path = patch["path"]?.GetValue<string>();
                if (path is not ("/enabled/0" or "/enabled/1")) continue;
                patch["path"] = path == "/enabled/0" ? "/0/enabled" : "/1/enabled";
                changed++;
            }
            if (changed == 0) return;
            asset.Data = Encoding.UTF8.GetBytes(patches.ToJsonString());
            api.Logger.Notification("[vsscpmod] Corrected {0} Heraldry Capes conditional recipe patch paths.", changed);
        }
        catch (Exception error)
        {
            api.Logger.Warning("[vsscpmod] Could not prepare Heraldry Capes recipe patches: {0}", error.Message);
        }
    }
}
