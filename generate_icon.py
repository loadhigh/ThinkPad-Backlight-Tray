#!/usr/bin/env python3
"""Generate the application icon for ThinkPad Backlight Tray."""

from PIL import Image, ImageDraw, ImageFilter
import math, struct, io, sys, os

def draw_icon(size):
    """Draw a keyboard-backlight icon at the given pixel size."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size  # shorthand

    # ── colour palette ──────────────────────────────────────────
    glow_colour   = (130, 200, 255)      # brighter blue glow
    key_fill      = (70, 70, 82)         # lighter key caps
    key_outline   = (140, 145, 160)      # brighter key border
    body_fill     = (50, 50, 60)         # lighter keyboard body
    body_outline  = (120, 120, 135)

    pad = s * 0.08                       # outer padding

    # ── glow behind the keyboard ────────────────────────────────
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    # Radial-ish glow: stack of translucent ellipses
    cx, cy_glow = s / 2, s * 0.38
    for i in range(6, 0, -1):
        rx = s * 0.32 * (i / 6)
        ry = s * 0.26 * (i / 6)
        alpha = int(100 * (1 - i / 7))
        gd.ellipse(
            [cx - rx, cy_glow - ry, cx + rx, cy_glow + ry],
            fill=(*glow_colour, alpha),
        )
    glow = glow.filter(ImageFilter.GaussianBlur(radius=s * 0.07))
    img = Image.alpha_composite(img, glow)
    d = ImageDraw.Draw(img)

    # ── light rays ──────────────────────────────────────────────
    ray_len   = s * 0.16
    ray_start = s * 0.16
    num_rays  = 9
    for i in range(num_rays):
        angle = math.pi + math.pi * (i / (num_rays - 1))  # 180° arc
        x1 = cx + ray_start * math.cos(angle)
        y1 = cy_glow + ray_start * math.sin(angle) * 0.7
        x2 = cx + (ray_start + ray_len) * math.cos(angle)
        y2 = cy_glow + (ray_start + ray_len) * math.sin(angle) * 0.7
        w = max(1, s // 32)
        d.line([(x1, y1), (x2, y2)], fill=(*glow_colour, 230), width=w)

    # ── keyboard body (rounded rect) ───────────────────────────
    kb_l = pad
    kb_r = s - pad
    kb_t = s * 0.46
    kb_b = s - pad
    r = s * 0.08  # corner radius
    d.rounded_rectangle([kb_l, kb_t, kb_r, kb_b], radius=r,
                        fill=body_fill, outline=body_outline,
                        width=max(1, s // 64))

    # ── key grid ────────────────────────────────────────────────
    rows = [5, 5, 4]        # keys per row
    key_pad = s * 0.03      # gap between keys
    region_l = kb_l + s * 0.06
    region_r = kb_r - s * 0.06
    region_t = kb_t + s * 0.06
    region_b = kb_b - s * 0.06
    row_h = (region_b - region_t) / len(rows)

    for ri, ncols in enumerate(rows):
        y_top = region_t + ri * row_h + key_pad / 2
        y_bot = region_t + (ri + 1) * row_h - key_pad / 2
        # Offset middle rows slightly for stagger effect
        offset = (s * 0.02) * (ri % 2)
        col_w = (region_r - region_l - offset) / ncols
        for ci in range(ncols):
            x_l = region_l + offset + ci * col_w + key_pad / 2
            x_r = region_l + offset + (ci + 1) * col_w - key_pad / 2
            kr = max(1, s // 48)
            d.rounded_rectangle([x_l, y_top, x_r, y_bot], radius=kr,
                                fill=key_fill, outline=key_outline,
                                width=max(1, s // 128))

    return img


# ── build multi-resolution .ico ─────────────────────────────────
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
big = draw_icon(256)

out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "app.ico")
big.save(out_path, format="ICO", sizes=sizes)

fsize = os.path.getsize(out_path)
print(f"Icon saved to {out_path}  ({fsize} bytes, {len(sizes)} sizes)")


