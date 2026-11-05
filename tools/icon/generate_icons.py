"""Generate all app icon assets (app-platform R10).

Usage: python tools/icon/generate_icons.py
Writes into WindowsCalendar/Assets. Requires Pillow.
"""
from pathlib import Path

from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parents[2] / "WindowsCalendar" / "Assets"
SS = 8  # supersampling factor

# ui-colour-theme R1
NAVY20, NAVY60, NAVY70, NAVY80 = "#C2D5FA", "#155CEE", "#0E49BF", "#0B3892"
SEA40 = "#1FB085"
WHITE = "#FFFFFF"


def draw_glyph(size, body, outline, header, rings, cell, today):
    """Calendar page drawn at size*SS; coordinates snapped to the target pixel grid."""
    s = size * SS

    def p(v):  # fraction of the icon -> snapped supersampled pixels
        return round(v * size) * SS

    def start(centre, width):  # left/top edge of a box centred at a fraction, snapped
        return round(centre * size - width / SS / 2) * SS

    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    left, right, top, bottom = p(0.08), p(0.92), p(0.16), p(0.92)
    radius = max(SS, p(0.14))
    stroke = max(SS, p(0.06))
    header_bottom = p(0.38)

    d.rounded_rectangle([left, top, right, bottom], radius, fill=body or outline)
    if body:
        d.rounded_rectangle([left, top, right, bottom], radius, outline=outline, width=stroke)
    else:  # monochrome: hollow out the body below the header
        d.rounded_rectangle([left + stroke, header_bottom, right - stroke, bottom - stroke],
                            max(SS, radius - stroke), fill=(0, 0, 0, 0))
    d.rounded_rectangle([left, top, right, header_bottom], radius, fill=header,
                        corners=(True, True, False, False))

    ring_w = max(SS, p(0.08))
    for cx in (0.32, 0.68):
        x = start(cx, ring_w)
        d.rounded_rectangle([x, p(0.06), x + ring_w, p(0.26)], ring_w // 2, fill=rings)

    # 3 x 2 day grid; the middle cell of the bottom row is "today"
    cell_w = max(SS, p(0.14))
    for j, cy in enumerate((0.53, 0.75)):
        for i, cx in enumerate((0.29, 0.50, 0.71)):
            is_today = (i, j) == (1, 1)
            fill = today if is_today else cell
            if fill is None:
                continue
            x, y = start(cx, cell_w), start(cy, cell_w)
            d.rounded_rectangle([x, y, x + cell_w, y + cell_w], max(1, cell_w // 4), fill=fill)
    return im.resize((size, size), Image.LANCZOS)


def app_icon(size):
    return draw_glyph(size, body=WHITE, outline=NAVY70, header=NAVY60, rings=NAVY80,
                      cell=NAVY20, today=SEA40)


def tray_icon(size, colour):
    # Monochrome: only the today cell is filled so the glyph stays readable at 16 px.
    return draw_glyph(size, body=None, outline=colour, header=colour, rings=colour,
                      cell=None, today=colour)


def centred(canvas, icon_size):
    w, h = canvas
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    im.alpha_composite(app_icon(icon_size), ((w - icon_size) // 2, (h - icon_size) // 2))
    return im


def save_ico(path, render, sizes):
    images = [render(s) for s in sizes]
    images[-1].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])


def main():
    outputs = {
        "Square44x44Logo.scale-200.png": app_icon(88),
        "StoreLogo.png": app_icon(50),
        "LockScreenLogo.scale-200.png": app_icon(48),
        "Square150x150Logo.scale-200.png": centred((300, 300), 200),
        "Wide310x150Logo.scale-200.png": centred((620, 300), 200),
        "SplashScreen.scale-200.png": centred((1240, 600), 300),
    }
    for t in (16, 24, 32, 48, 256):
        outputs[f"Square44x44Logo.targetsize-{t}_altform-unplated.png"] = app_icon(t)
    for name, im in outputs.items():
        im.save(ASSETS / name)

    tray_sizes = [16, 20, 24, 32, 40, 48]
    save_ico(ASSETS / "TrayIconLight.ico", lambda s: tray_icon(s, "#1C1C1C"), tray_sizes)  # light taskbar
    save_ico(ASSETS / "TrayIconDark.ico", lambda s: tray_icon(s, WHITE), tray_sizes)       # dark taskbar
    # Window icon for Alt-Tab and the taskbar thumbnail (AppWindow.SetIcon)
    save_ico(ASSETS / "AppIcon.ico", app_icon, [16, 20, 24, 32, 40, 48, 64, 256])
    print(f"Wrote {len(outputs) + 3} files to {ASSETS}")


if __name__ == "__main__":
    main()
