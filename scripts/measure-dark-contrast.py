"""深色播放画面截图的对比度探针：看文字底下的环境光有多亮。

对固定区域统计像素亮度：中位数当作文字底下的背景，97 分位当作文字本体，两者给出
WCAG 式对比度。区域固定，所以改动前后可直接比较。
"""
import sys
from pathlib import Path
from PIL import Image

REGIONS = {
    'field-only': (640, 260, 780, 600),      # 封面与文字栏之间的纯环境光
    'text-column': (790, 240, 1090, 610),    # 播放页的歌词／曲目信息文字栏
    'bottom-bar': (150, 840, 1450, 890),     # 底部播放条：靠边，受暗角影响
}


def luminance(pixel):
    out = []
    for channel in pixel[:3]:
        value = channel / 255
        out.append(value / 12.92 if value <= .03928 else ((value + .055) / 1.055) ** 2.4)
    return .2126 * out[0] + .7152 * out[1] + .0722 * out[2]


def percentile(values, fraction):
    return values[min(len(values) - 1, int(len(values) * fraction))]


def probe(path, region):
    image = Image.open(path).convert('RGB').crop(region)
    values = sorted(luminance(pixel) for pixel in image.getdata())
    background, text = percentile(values, .50), percentile(values, .97)
    return background, text, (text + .05) / (background + .05)


if __name__ == '__main__':
    for pattern in sys.argv[1:]:
        for path in sorted(Path().glob(pattern)):
            cells = []
            for name, region in REGIONS.items():
                background, text, ratio = probe(path, region)
                cells.append('%s bg=%.3f text=%.3f ratio=%.2f' % (name, background, text, ratio))
            print('%-28s %s' % (path.name, ' | '.join(cells)))
