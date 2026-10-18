"""Render the code-native Kod Vsego network mark into Windows icon sizes."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
size = 1024
nodes = [(512, 190), (248, 348), (248, 664), (512, 822),
         (776, 664), (512, 506), (776, 348)]
edges = [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 0)]
sand, cyan = (238, 215, 173), (2, 165, 207)

background = Image.new('RGBA', (size, size))
pixels = background.load()
for y in range(size):
    t = y / (size - 1)
    color = tuple(round(a * (1-t) + b*t) for a, b in zip((25, 42, 52), (12, 22, 29)))
    for x in range(size):
        pixels[x, y] = (*color, 255)
corners = Image.new('L', (size, size))
ImageDraw.Draw(corners).rounded_rectangle((8, 8, 1016, 1016), radius=224, fill=255)
background.putalpha(corners)
ImageDraw.Draw(background).rounded_rectangle((9, 9, 1015, 1015), radius=224,
                                            outline=(53, 79, 89, 255), width=5)

mask = Image.new('L', (size, size))
draw = ImageDraw.Draw(mask)
for a, b in edges:
    draw.line((nodes[a], nodes[b]), fill=255, width=42)
for x, y in nodes:
    draw.ellipse((x-65, y-65, x+65, y+65), fill=255)
    draw.ellipse((x-30, y-30, x+30, y+30), fill=0)
gradient = Image.new('RGBA', (size, size))
gd = ImageDraw.Draw(gradient)
for y in range(size):
    t = min(1, max(0, (y - 190) / (740 - 190)))
    color = tuple(round(a*(1-t) + b*t) for a, b in zip(sand, cyan))
    gd.line((0, y, size, y), fill=(*color, 255))
gradient.putalpha(mask)
background = Image.alpha_composite(background, gradient)
background.save(root / 'res' / 'app-icon.png')
background.save(root / 'res' / 'app.ico', format='ICO',
                sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])

path = ' '.join(('M' if i == 0 else 'L') + f'{x},{y}' for i, (x,y) in enumerate(nodes)) + ' Z'
circles = '\n'.join(f'<circle cx="{x}" cy="{y}" r="47.5"/>' for x,y in nodes)
svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
<defs>
  <linearGradient id="bg" x2="0" y2="1"><stop stop-color="#192a34"/><stop offset="1" stop-color="#0c161d"/></linearGradient>
  <linearGradient id="mark" x1="0" y1="190" x2="0" y2="740" gradientUnits="userSpaceOnUse"><stop stop-color="#eed7ad"/><stop offset="1" stop-color="#02a5cf"/></linearGradient>
</defs>
<rect x="9" y="9" width="1006" height="1006" rx="224" fill="url(#bg)" stroke="#354f59" stroke-width="5"/>
<path d="{path}" fill="none" stroke="url(#mark)" stroke-width="42"/>
<g fill="url(#bg)" stroke="url(#mark)" stroke-width="35">{circles}</g>
</svg>'''
(root / 'res' / 'app-icon.svg').write_text(svg, encoding='utf-8')

# Show real small-size rendering alongside a larger preview.
sheet = Image.new('RGB', (680, 400), '#e8eef1')
for n, x, y in [(320, 32, 40), (128, 406, 40), (64, 566, 40),
               (48, 406, 220), (32, 486, 220), (16, 566, 220)]:
    icon = background.resize((n, n), Image.Resampling.LANCZOS)
    sheet.paste(icon, (x, y), icon)
    ImageDraw.Draw(sheet).text((x, y+n+10), f'{n} px', fill='#263943')
sheet.save(root / 'res' / 'app-icon-preview.png')
print('Created SVG, PNG and multi-size ICO (16–256 px).')
