"""Real-ESRGAN x4plus upscaling for the private ML child (requires torch and Pillow).

The network is the published RRDBNet layout (23 residual-in-residual dense blocks), written here so
the pinned weights load with `torch.load(weights_only=True)` and no extra packages. Large pictures
are processed in overlapping tiles so a 6 GB GPU can enlarge them.
"""
import math

import torch
from torch import nn
from torch.nn import functional as F

SCALE = 4


class ResidualDenseBlock(nn.Module):
    def __init__(self, features=64, growth=32):
        super().__init__()
        self.conv1 = nn.Conv2d(features, growth, 3, 1, 1)
        self.conv2 = nn.Conv2d(features + growth, growth, 3, 1, 1)
        self.conv3 = nn.Conv2d(features + 2 * growth, growth, 3, 1, 1)
        self.conv4 = nn.Conv2d(features + 3 * growth, growth, 3, 1, 1)
        self.conv5 = nn.Conv2d(features + 4 * growth, features, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, inplace=True)

    def forward(self, x):
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
        return x5 * 0.2 + x


class RRDB(nn.Module):
    def __init__(self, features=64, growth=32):
        super().__init__()
        self.rdb1 = ResidualDenseBlock(features, growth)
        self.rdb2 = ResidualDenseBlock(features, growth)
        self.rdb3 = ResidualDenseBlock(features, growth)

    def forward(self, x):
        return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x


class RRDBNet(nn.Module):
    def __init__(self, blocks=23, features=64, growth=32):
        super().__init__()
        self.conv_first = nn.Conv2d(3, features, 3, 1, 1)
        self.body = nn.Sequential(*[RRDB(features, growth) for _ in range(blocks)])
        self.conv_body = nn.Conv2d(features, features, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(features, features, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(features, features, 3, 1, 1)
        self.conv_hr = nn.Conv2d(features, features, 3, 1, 1)
        self.conv_last = nn.Conv2d(features, 3, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(0.2, inplace=True)

    def forward(self, x):
        feature = self.conv_first(x)
        feature = feature + self.conv_body(self.body(feature))
        feature = self.lrelu(self.conv_up1(F.interpolate(feature, scale_factor=2, mode="nearest")))
        feature = self.lrelu(self.conv_up2(F.interpolate(feature, scale_factor=2, mode="nearest")))
        return self.conv_last(self.lrelu(self.conv_hr(feature)))


def load(weights, device):
    state = torch.load(str(weights), map_location="cpu", weights_only=True)
    model = RRDBNet()
    model.load_state_dict(state.get("params_ema", state), strict=True)  # Any layout mismatch fails loudly.
    return model.eval().to(device)


@torch.no_grad()
def upscale(model, picture, device, tile=256, pad=16, progress=lambda done, total: None):
    """Enlarge a PIL RGB picture four times, tile by tile; returns a PIL RGB picture."""
    from PIL import Image
    import numpy as np
    source = torch.from_numpy(np.asarray(picture, dtype=np.float32) / 255.0).permute(2, 0, 1).unsqueeze(0)
    _, _, height, width = source.shape
    output = torch.empty((1, 3, height * SCALE, width * SCALE))
    rows, columns = math.ceil(height / tile), math.ceil(width / tile)
    for index in range(rows * columns):
        top, left = (index // columns) * tile, (index % columns) * tile
        bottom, right = min(top + tile, height), min(left + tile, width)
        # Overlapping context hides seams; only the tile's own area is kept.
        y0, x0, y1, x1 = max(top - pad, 0), max(left - pad, 0), min(bottom + pad, height), min(right + pad, width)
        result = model(source[:, :, y0:y1, x0:x1].to(device)).clamp_(0, 1).cpu()
        output[:, :, top * SCALE:bottom * SCALE, left * SCALE:right * SCALE] = result[
            :, :, (top - y0) * SCALE:(top - y0 + bottom - top) * SCALE, (left - x0) * SCALE:(left - x0 + right - left) * SCALE]
        progress(index + 1, rows * columns)
    array = (output[0].permute(1, 2, 0).numpy() * 255.0).round().astype("uint8")
    return Image.fromarray(array, "RGB")
