"""Generate the repository-owned geometric MW icon without external imaging packages."""
from pathlib import Path
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]
POLYGON = [(55,181),(55,75),(82,75),(128,138),(174,75),(201,75),(201,181),(172,181),(172,124),(128,181),(84,124),(84,181)]


def inside(x, y):
    result = False
    for index, (a, b) in enumerate(POLYGON):
        c, d = POLYGON[index - 1]
        if (b > y) != (d > y) and x < (c - a) * (y - b) / (d - b) + a:
            result = not result
    return result


def color(x, y):
    dx, dy = max(abs(x - 128) - 68, 0), max(abs(y - 128) - 68, 0)
    if dx * dx + dy * dy > 52 * 52:
        return (0, 0, 0, 0)
    if inside(x, y):
        return (240, 244, 255, 255)
    return (56 + int(25 * y / 256), 70 + int(18 * y / 256), 133 + int(35 * y / 256), 255)


def png(size):
    rows = bytearray()
    scale = 256 / size
    for y in range(size):
        rows.append(0)
        for x in range(size):
            samples = [color((x + (sx + .5) / 4) * scale, (y + (sy + .5) / 4) * scale) for sy in range(4) for sx in range(4)]
            rows.extend(round(sum(sample[k] for sample in samples) / 16) for k in range(4))
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(bytes(rows), 9)) + chunk(b'IEND', b'')


sizes = [16, 24, 32, 48, 64, 128, 256]
images = [png(size) for size in sizes]
offset = 6 + 16 * len(sizes)
entries = bytearray()
for size, data in zip(sizes, images):
    entries.extend(struct.pack('<BBBBHHII', size if size < 256 else 0, size if size < 256 else 0, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
assets = ROOT / 'src/MediaWorkbench.App/Assets'
(assets / 'AppIcon.ico').write_bytes(struct.pack('<HHH', 0, 1, len(sizes)) + entries + b''.join(images))
(assets / 'MediaWorkbench.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><rect x="8" y="8" width="240" height="240" rx="52" fill="#46579e"/><path d="M55 181V75h27l46 63 46-63h27v106h-29v-57l-44 57-44-57v57Z" fill="#f0f4ff"/></svg>\n', encoding='utf-8')
print('Generated Media Workbench icon and SVG source')
