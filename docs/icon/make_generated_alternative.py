import math, cairosvg
from PIL import Image

W = 1024
parts = []

def coin_side(cx, y_top, rx, ry, h, grad="sideGrad"):
    # front half of the coin's edge between the top ellipse (at y_top) and the bottom ellipse (at y_top+h)
    return (f'<path d="M{cx-rx},{y_top} L{cx-rx},{y_top+h} '
            f'A{rx},{ry} 0 0 0 {cx+rx},{y_top+h} L{cx+rx},{y_top} '
            f'A{rx},{ry} 0 0 1 {cx-rx},{y_top} Z" fill="url(#{grad})"/>'
            # ambient occlusion where the edge meets the coin below / the ground
            f'<path d="M{cx-rx},{y_top+h-10} L{cx-rx},{y_top+h} A{rx},{ry} 0 0 0 {cx+rx},{y_top+h} L{cx+rx},{y_top+h-10} '
            f'A{rx},{ry} 0 0 1 {cx-rx},{y_top+h-10} Z" fill="url(#aoGrad)"/>'
            # a few reeded-edge ticks
            + "".join(f'<line x1="{cx + rx*math.cos(a):.1f}" y1="{y_top + ry*math.sin(a) + 4:.1f}" '
                      f'x2="{cx + rx*math.cos(a):.1f}" y2="{y_top + ry*math.sin(a) + h - 6:.1f}" '
                      f'stroke="#7A4E08" stroke-opacity="0.35" stroke-width="3"/>'
                      for a in [math.radians(d) for d in range(15, 166, 15)]))

def coin_top(cx, cy, rx, ry, monogram=False):
    s = (f'<ellipse cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}" fill="url(#topGrad)"/>'
         f'<ellipse cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}" fill="none" stroke="url(#rimGrad)" stroke-width="5"/>'
         # inner raised field (the coin's design area) so the rim reads as embossed
         f'<ellipse cx="{cx}" cy="{cy+2}" rx="{rx-26}" ry="{ry-10}" fill="none" stroke="#B8801A" stroke-opacity="0.55" stroke-width="4"/>'
         f'<ellipse cx="{cx}" cy="{cy-2}" rx="{rx-26}" ry="{ry-10}" fill="none" stroke="#FFF0B8" stroke-opacity="0.7" stroke-width="3"/>')
    if monogram:
        # embossed "H": dark copy offset down-right, light copy up-left, main on top
        f = f'font-family="Segoe UI, Arial, Helvetica, sans-serif" font-weight="800" font-size="{ry*1.55:.0f}" text-anchor="middle"'
        s += (f'<g transform="translate({cx},{cy}) scale(1,{ry/rx*1.9:.3f})">'
              f'<text x="3" y="{ry*0.56/ (ry/rx*1.9):.0f}" {f} fill="#8A5A0C" fill-opacity="0.8">H</text>'
              f'<text x="-3" y="{ry*0.56/ (ry/rx*1.9) - 4:.0f}" {f} fill="#FFF6C8" fill-opacity="0.85">H</text>'
              f'<text x="0" y="{ry*0.56/ (ry/rx*1.9) - 2:.0f}" {f} fill="url(#letterGrad)">H</text></g>')
    # specular highlight
    s += f'<ellipse cx="{cx-rx*0.38:.0f}" cy="{cy-ry*0.45:.0f}" rx="{rx*0.32:.0f}" ry="{ry*0.28:.0f}" fill="url(#specGrad)"/>'
    return s

def stack(cx, base_y, rx, ry, h, n, jitter=()):
    s = ""
    y = base_y
    for i in range(n):
        dx = jitter[i] if i < len(jitter) else 0
        top = y - h
        s += coin_side(cx+dx, top, rx, ry, h)
        s += coin_top(cx+dx, top, rx, ry, monogram=(i == n-1))
        y = top
    return s

defs = """
<defs>
  <radialGradient id="topGrad" cx="38%" cy="32%" r="75%">
    <stop offset="0" stop-color="#FFF6C4"/><stop offset="0.35" stop-color="#FFD84D"/>
    <stop offset="0.75" stop-color="#F0B429"/><stop offset="1" stop-color="#C98A12"/>
  </radialGradient>
  <linearGradient id="rimGrad" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#FFF3A6"/><stop offset="1" stop-color="#B07612"/>
  </linearGradient>
  <linearGradient id="sideGrad" x1="0" y1="0" x2="1" y2="0">
    <stop offset="0" stop-color="#9A6210"/><stop offset="0.25" stop-color="#E2AB2C"/>
    <stop offset="0.5" stop-color="#FFD25A"/><stop offset="0.75" stop-color="#D9981F"/><stop offset="1" stop-color="#8E5A0C"/>
  </linearGradient>
  <linearGradient id="aoGrad" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity="0.35"/>
  </linearGradient>
  <radialGradient id="specGrad" cx="50%" cy="50%" r="50%">
    <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.85"/><stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
  </radialGradient>
  <linearGradient id="letterGrad" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#FFE98A"/><stop offset="1" stop-color="#D9971A"/>
  </linearGradient>
  <radialGradient id="shadowGrad" cx="50%" cy="50%" r="50%">
    <stop offset="0" stop-color="#000" stop-opacity="0.45"/><stop offset="0.7" stop-color="#000" stop-opacity="0.18"/><stop offset="1" stop-color="#000" stop-opacity="0"/>
  </radialGradient>
</defs>
"""

body = ""
# ground shadow
body += '<ellipse cx="520" cy="830" rx="440" ry="120" fill="url(#shadowGrad)"/>'
# back-left tall stack
body += stack(cx=360, base_y=760, rx=200, ry=72, h=44, n=8, jitter=(0, 6, -4, 8, 2, -6, 4, 0))
# right stack
body += stack(cx=690, base_y=800, rx=190, ry=68, h=42, n=5, jitter=(0, -5, 6, -3, 2))
# a loose coin lying flat in front
body += coin_side(470, 850, 175, 62, 40)
body += coin_top(470, 850, 175, 62)
# a coin leaning against the front (rotated: reads as standing on its edge)
body += ('<g transform="rotate(-62 300 870)">'
         + coin_side(300, 848, 150, 54, 40) + coin_top(300, 848, 150, 54) + '</g>')

svg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{W}" viewBox="0 0 {W} {W}">{defs}{body}</svg>'
open("hoard.svg", "w").write(svg)
cairosvg.svg2png(bytestring=svg.encode(), write_to="hoard-1024.png", output_width=W, output_height=W)

img = Image.open("hoard-1024.png").convert("RGBA")
# trim transparent margins then pad to a square with a small border so the icon fills the tile
# bounding box of the solid artwork (ignore the faint outer part of the ground shadow so the coins fill the tile)
alpha = img.split()[3].point(lambda a: 255 if a > 60 else 0)
bbox = alpha.getbbox()
crop = img.crop(bbox)
side = max(crop.size)
pad = int(side * 0.06)
canvas = Image.new("RGBA", (side + 2*pad, side + 2*pad), (0, 0, 0, 0))
canvas.paste(crop, (pad + (side - crop.width)//2, pad + (side - crop.height)//2))
canvas = canvas.resize((1024, 1024), Image.LANCZOS)
canvas.save("hoard-1024.png")

sizes = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]
frames = [canvas.resize((s, s), Image.LANCZOS) for s in sizes]
frames[0].save("hoard.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[1:])
canvas.resize((256, 256), Image.LANCZOS).save("hoard-256.png")
canvas.resize((48, 48), Image.LANCZOS).save("hoard-48.png")
canvas.resize((16, 16), Image.LANCZOS).save("hoard-16.png")
print("done")
