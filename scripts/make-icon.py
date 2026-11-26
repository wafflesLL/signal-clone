# Draws the app icon: a blue rounded square with a white chat bubble holding a padlock.
# Usage: python3 scripts/make-icon.py assets && iconutil -c icns assets/icon.iconset -o assets/icon.icns && rm -r assets/icon.iconset
import sys
from PIL import Image, ImageDraw

def icon(size):
    S = 1024  # draw big, scale down for smooth edges
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = 80
    # background with a gentle vertical gradient
    bg = Image.new("RGBA", (S, S))
    for y in range(S):
        t = y / S
        c = (int(58 + (30 - 58) * t), int(118 + (82 - 118) * t), int(240 + (200 - 240) * t), 255)
        ImageDraw.Draw(bg).line([(0, y), (S, y)], fill=c)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([m, m, S - m, S - m], radius=200, fill=255)
    img.paste(bg, (0, 0), mask)
    # chat bubble
    d.rounded_rectangle([230, 250, 794, 690], radius=150, fill="white")
    d.polygon([(330, 650), (290, 800), (470, 680)], fill="white")
    # padlock
    blue = (40, 100, 225, 255)
    d.rounded_rectangle([420, 450, 604, 610], radius=26, fill=blue)
    d.arc([440, 340, 584, 500], start=180, end=360, fill=blue, width=34)
    d.line([(457, 420), (457, 455)], fill=blue, width=34)
    d.line([(567, 420), (567, 455)], fill=blue, width=34)
    d.ellipse([496, 500, 528, 532], fill="white")
    d.rectangle([506, 520, 518, 570], fill="white")
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
base = icon(1024)
base.save(f"{out}/icon.png")
base.save(f"{out}/icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
import os
iconset = f"{out}/icon.iconset"
os.makedirs(iconset, exist_ok=True)
for s in (16, 32, 128, 256, 512):
    icon(s).save(f"{iconset}/icon_{s}x{s}.png")
    icon(s * 2).save(f"{iconset}/icon_{s}x{s}@2x.png")
