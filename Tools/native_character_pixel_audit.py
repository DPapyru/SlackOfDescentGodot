import argparse
import json
from pathlib import Path
from PIL import Image


def read(path):
    with Image.open(path) as image:
        return image.size, image.convert("RGBA").tobytes()


def compare(expected, actual):
    size_a, rgba_a = read(expected)
    size_b, rgba_b = read(actual)
    if size_a != size_b:
        return {"expected": expected.name, "actual": actual.name, "rgba_equal": False,
                "expected_size": size_a, "actual_size": size_b, "differing_pixels": None, "difference_rect": None}
    positions = [index // 4 for index in range(0, len(rgba_a), 4) if rgba_a[index:index + 4] != rgba_b[index:index + 4]]
    rect = None
    if positions:
        xs, ys = [p % size_a[0] for p in positions], [p // size_a[0] for p in positions]
        rect = {"x": min(xs), "y": min(ys), "width": max(xs) - min(xs) + 1, "height": max(ys) - min(ys) + 1}
    return {"expected": expected.name, "actual": actual.name, "rgba_equal": not positions,
            "width": size_a[0], "height": size_a[1], "differing_pixels": len(positions), "difference_rect": rect}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    root = args.directory
    metadata = json.loads((root / "ui-audit.json").read_text())
    comparisons = [compare(root / "source.png", root / "pasted.png")]
    comparisons += [compare(root / f"before-{i}.png", root / f"loaded-{i}.png") for i in range(metadata["frames"])]
    undo_size, undo_data = read(root / "undo.png")
    undo_diff = sum(undo_data[index:index + 4] != bytes(4) for index in range(0, len(undo_data), 4))
    differences = sum(not row["rgba_equal"] for row in comparisons)
    result = {"rgba_equal": differences == 0 and undo_diff == 0,
              "compared_frames": metadata["frames"], "differing_frames": differences,
              "copy_rect": metadata["copyRect"], "paste_rect": metadata["pasteRect"],
              "undo_rect_size": undo_size, "undo_nonzero_pixels": undo_diff, "comparisons": comparisons}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["rgba_equal"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
