"""Rebuild text-only UI art in Chinese while preserving source assets and attribution."""
import json
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[1] / 'GameData'
manifest = json.loads((root / 'manifest.json').read_text())
font_path = root / 'localization/NotoSansCJK-Regular.ttc'
credits = [
    [('— 噼啪摇篮 —', 22), ('制作', 74), ('Foxtail', 99)],
    [('程序', 22), ('sakifox', 46), ('图像', 80), ('sakifox', 104)],
    [('音乐', 16), ('無属性 · くろゑ/khloe', 41), ('稲屋 隆 · こおろぎ', 64), ('01SoundEarth · Senses Circuit', 87), ('音楽素材/魔王魂', 110), ('sakifox', 133)],
    [('音效', 22), ('ザ·マッチメイカァズ2nd【免费音效素材】', 51), ('配音', 87), ('阿久女イク/StudioS', 116)],
    [('— 噼啪摇篮 —', 22), ('终', 75), ('感谢游玩！', 145)],
]
for sprite in manifest['sprites']:
    name = sprite['name']
    if name not in ['sprTitle', 'sprTitleSurvival', 'sprMissionStart', 'sprMissionClear', 'sprCredit', 'sprEndingScreen']:
        continue
    for index, frame in enumerate(sprite['frames']):
        width, height = frame['width'], frame['height']
        image = Image.new('RGBA', (width, height), (0, 0, 0, 255 if name == 'sprCredit' else 0))
        draw = ImageDraw.Draw(image)
        def label(text, y, size, color='white'):
            font = ImageFont.truetype(str(font_path), size)
            bounds = draw.textbbox((0, 0), text, font=font)
            draw.text(((width - bounds[2] + bounds[0]) / 2, y), text, font=font, fill=color, stroke_width=1 if size > 25 else 0, stroke_fill='black')
        if name == 'sprCredit':
            for text, y in credits[index]:
                label(text, y, 12 if index != 3 else 11)
        elif name == 'sprTitle':
            label('噼啪摇篮', 26, 72, '#bb2424')
            label('扩展版', 166, 48, '#bb2424')
        elif name == 'sprTitleSurvival':
            label('沉降之暇', 65, 76, '#bb2424')
        elif name in ['sprMissionStart', 'sprMissionClear']:
            label('任务开始！' if name == 'sprMissionStart' else '任务完成！', -5, 40)
        else:
            # Preserve the creator logo; replace the title and embedded finish hint.
            image = Image.open(root / frame['path']).convert('RGBA')
            draw = ImageDraw.Draw(image)
            draw.rectangle((300, 0, width, 220), fill=(0, 0, 0, 0))
            draw.rectangle((310, 380, width, height), fill=(0, 0, 0, 0))
            label('沉降之暇', 30, 55, '#bb2424')
            label('Backspace  返回标题', 407, 24, '#68bc72')
        destination = root / 'localization' / frame['path']
        destination.parent.mkdir(parents=True, exist_ok=True)
        image.save(destination)
