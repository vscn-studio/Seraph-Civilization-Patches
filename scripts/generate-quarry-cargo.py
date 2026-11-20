"""Generate original grid cargo shapes; no third-party model assets are copied."""
import itertools
import json
from pathlib import Path

target = Path(__file__).resolve().parents[1] / "assets/vsscpmod/shapes/quarrycargo/grid"
target.mkdir(parents=True, exist_ok=True)
for size, dimensions, layers in [
    ("small", [(1, 1)], 1), ("medium", [(1, 2), (2, 1)], 1),
    ("large", [(2, 2)], 1), ("huge", [(2, 2)], 2), ("giant", [(2, 2)], 3),
]:
    for width, depth in dimensions:
        for offset_x, offset_z in itertools.product(range(1-width, 1), range(1-depth, 1)):
            elements = []

            def box(name, low, high, texture):
                elements.append({"name": name, "from": low, "to": high, "faces": {
                    face: {"texture": "#" + texture, "uv": [0, 0, 16, 16]}
                    for face in ["north", "east", "south", "west", "up", "down"]}})

            x0, z0 = offset_x * 16, offset_z * 16
            for n, x in enumerate([x0 + 2, x0 + width * 16 - 4]):
                box(f"Pallet{n}", [x, 0, z0 + 1], [x + 2, 1, z0 + depth * 16 - 1], "wood")
            piece = 0
            for y, z, x in itertools.product(range(layers), range(depth), range(width)):
                box(f"Stone{piece}", [x0 + x*16 + 1, y*16 + 1.1, z0 + z*16 + 1],
                    [x0 + (x+1)*16 - 1, (y+1)*16 - 1, z0 + (z+1)*16 - 1], f"stone{piece}")
                piece += 1
            model = {"textureWidth": 16, "textureHeight": 16, "textures": {}, "elements": elements}
            path = target / f"{size}-{offset_x+1}-{offset_z+1}-{width}-{depth}.json"
            path.write_text(json.dumps(model, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
