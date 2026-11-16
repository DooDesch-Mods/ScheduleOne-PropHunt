"""
Cut the phone screen out of tests/out shots and lay them out as one labelled grid, for review.

    python tests/montage.py <out.png> <shot> [<shot> ...]     shots are names in tests/out, with or without .png

The crop is the phone's screen in a 1920x1080 shot with the phone held landscape and centred, which is where
Sideload puts it when the app is open.
"""
import os
import sys

from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
CROP = (530, 276, 1390, 746)   # left, top, right, bottom of the phone screen at 1920x1080
COLS = 2
LABEL_H = 22


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    target, names = sys.argv[1], sys.argv[2:]
    tiles = []
    for n in names:
        path = n if os.path.isfile(n) else os.path.join(OUT, n if n.endswith(".png") else n + ".png")
        if not os.path.isfile(path):
            print("[montage] missing: " + path)
            continue
        img = Image.open(path).convert("RGB")
        if img.size != (1920, 1080):
            img = img.resize((1920, 1080))
        tiles.append((os.path.splitext(os.path.basename(path))[0], img.crop(CROP)))
    if not tiles:
        raise SystemExit("[montage] nothing to lay out")

    w, h = CROP[2] - CROP[0], CROP[3] - CROP[1]
    rows = (len(tiles) + COLS - 1) // COLS
    sheet = Image.new("RGB", (COLS * w, rows * (h + LABEL_H)), (20, 20, 20))
    draw = ImageDraw.Draw(sheet)
    for i, (name, tile) in enumerate(tiles):
        x, y = (i % COLS) * w, (i // COLS) * (h + LABEL_H)
        draw.text((x + 6, y + 4), name, fill=(230, 230, 230))
        sheet.paste(tile, (x, y + LABEL_H))
    sheet.save(target)
    print("[montage] %d shots -> %s" % (len(tiles), target))


if __name__ == "__main__":
    main()
