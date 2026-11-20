using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

// Repair the source patches before the game's JSON patch loader (0.05).
// Recipes are synchronized by the server; shapes are prepared on each client.
public sealed class LongTermFoodMealCompatibilitySystem : ModSystem
{
    public override double ExecuteOrder() => 0.04;
    private static readonly JsonDocumentOptions options = new()
        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("longtermfood")) return;
        try
        {
            if (api.Side == EnumAppSide.Client) PreparePorridge(api);
            if (api.Side != EnumAppSide.Server) return;
            int changed = 0;
            foreach (string recipe in new[] { "jam", "meatystew", "porridge" })
            {
                var asset = api.Assets.TryGet(new AssetLocation($"game:patches/{recipe}.json"));
                if (asset == null || Parse(asset) is not JsonArray patches) continue;
                int count = 0;
                foreach (var patch in patches.OfType<JsonObject>())
                {
                    if (patch["file"]?.GetValue<string>() != $"game:recipes/cooking/{recipe}.json"
                        || patch["op"]?.GetValue<string>() != "addmerge"
                        || patch["value"] is not JsonArray stacks) continue;
                    string? path = patch["path"]?.GetValue<string>();
                    foreach (var stack in stacks.OfType<JsonObject>())
                    {
                        string? code = stack["code"]?.GetValue<string>();
                        string? old = stack["shapeElement"]?.GetValue<string>();
                        string? replacement = null;
                        string? texture = null;
                        if (code is "pemmican:syrup-cane" or "pemmican:syrup-beet" or "pemmican:sugar")
                        {
                            if (recipe == "jam" && old is "game:bowl/fruit" or "game:bowl/topping/fruit")
                            { replacement = "bowl/honey"; texture = "honey"; }
                            else if (recipe != "jam" && old is "game:bowl/topping/fruit" or "bowl/topping/fruit")
                            { replacement = "bowl/topping"; texture = "topping"; }
                            else if (old == "game:bowl/fruit 1/*")
                            { replacement = "bowl/fruit 1/*"; texture = "fruit"; }
                        }
                        if (recipe == "porridge" && old == "bowl/meat stew/*"
                            && code is "game:redmeat-raw" or "game:redmeat-cured" or "game:fish-raw"
                                or "game:fish-cured" or "game:poultry-raw" or "game:poultry-cured"
                            && path is "/ingredients/3/validStacks" or "/ingredients/4/validStacks")
                        {
                            string kind = code.Contains("fish") ? "fish" : code.Contains("poultry") ? "poultry" : "red meat";
                            replacement = "bowl/vsscpmod-" + kind + (path.Contains("/4/") ? " 2" : "") + "/*";
                            texture = "meat";
                            // These are discrete meat pieces, not the stew's liquid surface.
                            stack["textureMapping"] = new JsonArray("meat", kind == "fish" ? "group1" : kind == "poultry" ? "group3" : "group2");
                        }
                        if (replacement == null) continue;
                        stack["shapeElement"] = replacement;
                        if (texture != null && stack["textureMapping"] is JsonArray mapping && mapping.Count == 2)
                            mapping[0] = texture;
                        count++;
                    }
                }
                if (count == 0) continue;
                asset.Data = Encoding.UTF8.GetBytes(patches.ToJsonString());
                changed += count;
            }
            api.Logger.Notification("[vsscpmod] Corrected {0} Long-term food meal rendering entries.", changed);
        }
        catch (Exception e)
        {
            api.Logger.Warning("[vsscpmod] Long-term food meal compatibility unavailable: {0}", e.Message);
        }
    }

    private static JsonNode? Parse(IAsset asset) => JsonNode.Parse(asset.ToText(), documentOptions: options);

    private static void PreparePorridge(ICoreAPI api)
    {
        var porridgeAsset = api.Assets.TryGet(new AssetLocation("game:shapes/block/food/meal/porridge.json"));
        var stewAsset = api.Assets.TryGet(new AssetLocation("game:shapes/block/food/meal/meaty-stew.json"));
        if (porridgeAsset == null || stewAsset == null) throw new InvalidDataException("Missing vanilla meal shapes.");
        var porridge = Parse(porridgeAsset)!;
        var stew = Parse(stewAsset)!;
        var target = ((JsonArray)porridge["elements"]!).OfType<JsonObject>().Single(e => e["name"]?.GetValue<string>() == "bowl")["children"]!.AsArray();
        var source = ((JsonArray)stew["elements"]!).OfType<JsonObject>().Single(e => e["name"]?.GetValue<string>() == "bowl")["children"]!.AsArray();
        foreach (string name in new[] { "red meat", "red meat 2", "fish", "fish 2", "poultry", "poultry 2" })
        {
            string addedName = "vsscpmod-" + name;
            if (target.OfType<JsonObject>().Any(e => e["name"]?.GetValue<string>() == addedName)) continue;
            var piece = source.OfType<JsonObject>().Single(e => e["name"]?.GetValue<string>() == name).DeepClone();
            piece["name"] = addedName;
            target.Add(piece);
        }
        // Only explicitly selected new pieces render. Vanilla porridge selectors stay intact.
        porridge["textures"]!["meat"] = stew["textures"]!["meat"]!.DeepClone();
        porridgeAsset.Data = Encoding.UTF8.GetBytes(porridge.ToJsonString());
    }
}
