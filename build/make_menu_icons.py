"""Generates 16x16 nonzero StreamGeometry glyphs for menu icons (NextCommander.App/Styles/MenuIcons.axaml)."""
import math, sys

def f(v):
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s

def area(pts):
    return sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts)))

def poly(pts, hole=False):
    if (area(pts) > 0) == hole:
        pts = pts[::-1]
    return "M" + " L".join(f"{f(x)},{f(y)}" for x, y in pts) + " Z"

def rect(x, y, w, h, hole=False):
    return poly([(x, y), (x + w, y), (x + w, y + h), (x, y + h)], hole)

def ring_rect(x, y, w, h, t=1.4):
    return rect(x, y, w, h) + " " + rect(x + t, y + t, w - 2 * t, h - 2 * t, True)

def circle(cx, cy, r, hole=False):
    sweep = 0 if hole else 1
    return (f"M{f(cx - r)},{f(cy)} A{f(r)},{f(r)} 0 1 {sweep} {f(cx + r)},{f(cy)} "
            f"A{f(r)},{f(r)} 0 1 {sweep} {f(cx - r)},{f(cy)} Z")

def ring(cx, cy, r, t=1.4):
    return circle(cx, cy, r) + " " + circle(cx, cy, r - t, True)

def ellipse_pts(cx, cy, rx, ry, a0=0, a1=360, n=40):
    return [(cx + rx * math.sin(math.radians(a0 + (a1 - a0) * i / n)), cy - ry * math.cos(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]

def arc_band(cx, cy, r, t, a0, a1, n=36):
    """Thick arc from angle a0 to a1 (degrees clockwise from 12 o'clock)."""
    outer = ellipse_pts(cx, cy, r, r, a0, a1, n)
    inner = ellipse_pts(cx, cy, r - t, r - t, a0, a1, n)[::-1]
    return poly(outer + inner)

def ellipse_ring(cx, cy, rx, ry, t=1.3):
    return poly(ellipse_pts(cx, cy, rx, ry)[:-1]) + " " + poly(ellipse_pts(cx, cy, rx - t, ry - t)[:-1], True)

def bar(x1, y1, x2, y2, w):
    dx, dy = x2 - x1, y2 - y1
    l = math.hypot(dx, dy)
    nx, ny = -dy / l * w / 2, dx / l * w / 2
    return poly([(x1 + nx, y1 + ny), (x2 + nx, y2 + ny), (x2 - nx, y2 - ny), (x1 - nx, y1 - ny)])

def doc(x, y, w, h, fold=3.2, t=1.3):
    outer = [(x, y), (x + w - fold, y), (x + w, y + fold), (x + w, y + h), (x, y + h)]
    inner = [(x + t, y + t), (x + w - fold - 0.3, y + t), (x + w - fold - 0.3, y + fold + 0.3), (x + w - t, y + fold + 0.3), (x + w - t, y + h - t), (x + t, y + h - t)]
    return poly(outer) + " " + poly(inner, True)

def j(*parts):
    return "F1 " + " ".join(parts)

def gear(cx, cy, ro, ri, hole, teeth=8):
    pts = []
    for i in range(teeth * 4):
        a = math.radians(360 / (teeth * 4) * i)
        r = ro if (i % 4) in (0, 1) else ri
        pts.append((cx + r * math.sin(a), cy - r * math.cos(a)))
    return poly(pts) + " " + circle(cx, cy, hole, True)

G = {}
# Files
G["MiAttrib"] = j(ring_rect(1.5, 1.5, 4, 4, 1.2), rect(2.9, 2.9, 1.2, 1.2), rect(7, 2.8, 7.5, 1.4),
                  ring_rect(1.5, 6, 4, 4, 1.2), rect(7, 7.3, 7.5, 1.4),
                  ring_rect(1.5, 10.5, 4, 4, 1.2), rect(2.9, 11.9, 1.2, 1.2), rect(7, 11.8, 7.5, 1.4))
G["MiCompare"] = j(ring_rect(1, 1.5, 6.3, 13, 1.2), ring_rect(8.7, 1.5, 6.3, 13, 1.2),
                   rect(2.6, 4, 3.1, 1), rect(2.6, 6.5, 3.1, 1), rect(2.6, 9, 3.1, 1),
                   rect(10.3, 4, 3.1, 1), rect(10.3, 6.5, 3.1, 1.6), rect(10.3, 9, 3.1, 1))
G["MiGear"] = j(gear(8, 8, 7, 5.2, 2.2))
G["MiInfo"] = j(ring(8, 8, 7, 1.4), circle(8, 4.7, 1.05), rect(7.2, 6.6, 1.6, 5.4))
G["MiPie"] = j(ring(8, 8, 7, 1.3), poly([(8, 8)] + ellipse_pts(8, 8, 4.6, 4.6, 0, 120, 16)))
G["MiPencil"] = j(poly([(1.5, 14.5), (2.4, 10.6), (10.6, 2.4), (13.6, 5.4), (5.4, 13.6)]), bar(11.4, 1.6, 14.4, 4.6, 1.6))
G["MiPrinter"] = j(rect(4, 1.5, 8, 3.2), poly([(1.5, 6), (14.5, 6), (14.5, 11.5), (12.5, 11.5), (12.5, 9.5), (3.5, 9.5), (3.5, 11.5), (1.5, 11.5)]),
                   ring_rect(4.5, 10.5, 7, 4, 1.2), rect(11.8, 7, 1.4, 1, True))
G["MiSplit"] = j(ring_rect(2.5, 1.5, 11, 5, 1.3), ring_rect(2.5, 9.5, 11, 5, 1.3), rect(1, 7.5, 2, 1), rect(4.5, 7.5, 2, 1), rect(8, 7.5, 2, 1), rect(11.5, 7.5, 2, 1))
G["MiCombine"] = j(ring_rect(2.5, 1.5, 11, 5, 1.3), ring_rect(2.5, 6.6, 11, 7.9, 1.3))
G["MiMail"] = j(ring_rect(1, 3, 14, 10, 1.3), poly([(2.3, 4.3), (8, 8.6), (13.7, 4.3), (13.7, 5.9), (8, 10.2), (2.3, 5.9)]))
G["MiHash"] = j(poly([(4.6, 1.5), (6.3, 1.5), (5.2, 14.5), (3.5, 14.5)]), poly([(9.9, 1.5), (11.6, 1.5), (10.5, 14.5), (8.8, 14.5)]),
                rect(1.5, 4.7, 13, 1.6), rect(1.5, 9.7, 13, 1.6))
G["MiCheck"] = j(poly([(1.3, 8.4), (2.9, 6.8), (6, 9.9), (13.1, 2.8), (14.7, 4.4), (6, 13.1)]))
G["MiPower"] = j(arc_band(8, 8.6, 6.4, 1.6, 35, 325), rect(7.2, 1, 1.6, 7.2))
G["MiEye"] = j("M8,3.3 C11.6,3.3 14,6 15.2,8 C14,10 11.6,12.7 8,12.7 C4.4,12.7 2,10 0.8,8 C2,6 4.4,3.3 8,3.3 Z",
               "M8,4.7 C5.4,4.7 3.5,6.6 2.5,8 C3.5,9.4 5.4,11.3 8,11.3 C10.6,11.3 12.5,9.4 13.5,8 C12.5,6.6 10.6,4.7 8,4.7 Z",
               circle(8, 8, 2.3))
G["MiDoc"] = j(doc(2.5, 1, 11, 14))
G["MiCopy"] = j(poly([(1.5, 1.5), (10.5, 1.5), (10.5, 2.9), (2.9, 2.9), (2.9, 11), (1.5, 11)]), ring_rect(4.5, 4.5, 10, 10, 1.4))
G["MiArrowRight"] = j(poly([(1, 6.9), (8.6, 6.9), (8.6, 3), (15, 8), (8.6, 13), (8.6, 9.1), (1, 9.1)]))
G["MiArrowLeft"] = j(poly([(15, 6.9), (7.4, 6.9), (7.4, 3), (1, 8), (7.4, 13), (7.4, 9.1), (15, 9.1)]))
G["MiArrowUp"] = j(poly([(6.9, 15), (6.9, 7.4), (3, 7.4), (8, 1), (13, 7.4), (9.1, 7.4), (9.1, 15)]))
G["MiArrowDown"] = j(poly([(6.9, 1), (6.9, 8.6), (3, 8.6), (8, 15), (13, 8.6), (9.1, 8.6), (9.1, 1)]))
G["MiX"] = j(bar(3, 3, 13, 13, 2.2), bar(13, 3, 3, 13, 2.2))
G["MiTrash"] = j(rect(1.8, 2.6, 12.4, 1.5), rect(5.8, 1, 4.4, 1.6), poly([(3, 5), (13, 5), (12.1, 15), (3.9, 15)]),
                 rect(5.3, 6.6, 1.2, 6.8, True), rect(7.4, 6.6, 1.2, 6.8, True), rect(9.5, 6.6, 1.2, 6.8, True))
G["MiClipboard"] = j(ring_rect(2.3, 2.5, 11.4, 12.5, 1.4), rect(5.3, 1, 5.4, 3.2), rect(4.8, 7, 6.4, 1.1), rect(4.8, 9.5, 6.4, 1.1), rect(4.8, 12, 4, 1.1))
G["MiScissors"] = j(ring(4.2, 12, 2.6, 1.3), ring(11.8, 12, 2.6, 1.3), bar(5.6, 9.9, 12, 1.2, 1.5), bar(10.4, 9.9, 4, 1.2, 1.5))
G["MiPlusCircle"] = j(ring(8, 8, 7, 1.4), rect(7.2, 4, 1.6, 8), rect(4, 7.2, 8, 1.6))
G["MiMinusCircle"] = j(ring(8, 8, 7, 1.4), rect(4, 7.2, 8, 1.6))
G["MiSelectAll"] = j(ring_rect(1.5, 1.5, 13, 13, 1.4), poly([(3.8, 8.4), (5.2, 7), (7, 8.8), (10.8, 5), (12.2, 6.4), (7, 11.6)]))
G["MiSelectNone"] = j(ring_rect(1.5, 1.5, 13, 13, 1.4))
G["MiInvert"] = j(ring_rect(1.5, 1.5, 13, 13, 1.4), poly([(2.9, 13.1), (13.1, 2.9), (13.1, 13.1)]))
G["MiStar"] = j(poly([(8, 1), (10.1, 5.6), (15, 6.1), (11.3, 9.4), (12.4, 14.3), (8, 11.8), (3.6, 14.3), (4.7, 9.4), (1, 6.1), (5.9, 5.6)]))
G["MiSave"] = j(poly([(1.5, 1.5), (11.8, 1.5), (14.5, 4.2), (14.5, 14.5), (1.5, 14.5)]), rect(4.3, 1.5, 6.4, 3.8, True), rect(3.8, 8.6, 8.4, 4.6, True),
                rect(8.4, 2.3, 1.4, 2.3))
G["MiFolderOpen"] = j(poly([(1, 3), (5.6, 3), (7.1, 4.5), (12.5, 4.5), (12.5, 6.3), (4, 6.3), (2.4, 12), (1, 13)]),
                      poly([(4.8, 7.4), (15.4, 7.4), (12.6, 13.6), (1.6, 13.6)]))
G["MiSearch"] = j(ring(6.5, 6.5, 5.2, 1.6), bar(10.2, 10.2, 14.5, 14.5, 2.4))
G["MiSwap"] = j(rect(1.5, 4.1, 8.8, 1.7), poly([(9.8, 1.6), (14.6, 4.95), (9.8, 8.3)]), rect(5.7, 10.2, 8.8, 1.7), poly([(6.2, 7.7), (1.4, 11.05), (6.2, 14.4)]))
G["MiExchange"] = j(poly([(1, 8), (5.2, 3.6), (5.2, 7), (10.8, 7), (10.8, 3.6), (15, 8), (10.8, 12.4), (10.8, 9), (5.2, 9), (5.2, 12.4)]))
G["MiEquals"] = j(rect(2, 4.6, 12, 2), rect(2, 9.4, 12, 2))
G["MiTag"] = j(poly([(1.5, 1.5), (8.2, 1.5), (14.8, 8.1), (8.1, 14.8), (1.5, 8.2)]), circle(5, 5, 1.4, True))
G["MiChip"] = j(rect(3.5, 3.5, 9, 9), rect(5.6, 5.6, 4.8, 4.8, True), rect(6.1, 6.1, 3.8, 3.8),
                *[rect(x, 1, 1.2, 2.5) for x in (5.2, 7.4, 9.6)], *[rect(x, 12.5, 1.2, 2.5) for x in (5.2, 7.4, 9.6)],
                *[rect(1, y, 2.5, 1.2) for y in (5.2, 7.4, 9.6)], *[rect(12.5, y, 2.5, 1.2) for y in (5.2, 7.4, 9.6)])
G["MiMonitor"] = j(ring_rect(1, 1.8, 14, 9.6, 1.4), rect(7, 11.4, 2, 1.9), rect(4.3, 13.3, 7.4, 1.5))
G["MiTransfer"] = j(poly([(4.4, 15), (4.4, 6.2), (1.5, 6.2), (5.2, 1.2), (8.9, 6.2), (6, 6.2), (6, 15)]),
                    poly([(10, 1), (10, 9.8), (7.1, 9.8), (10.8, 14.8), (14.5, 9.8), (11.6, 9.8), (11.6, 1)]))
G["MiKey"] = j(ring(5, 5, 3.9, 1.6), bar(7.3, 7.3, 14.6, 14.6, 1.9), bar(11.3, 11.3, 13.3, 9.3, 1.6), bar(13.1, 13.1, 14.9, 11.3, 1.6))
G["MiPlug"] = j(rect(5, 1, 1.5, 4.2), rect(9.5, 1, 1.5, 4.2), poly([(3, 5.2), (13, 5.2), (13, 8), (10, 11.3), (6, 11.3), (3, 8)]), rect(7.2, 11, 1.6, 4))
G["MiGlobe"] = j(ring(8, 8, 7, 1.3), ellipse_ring(8, 8, 3.1, 7, 1.2), rect(1.5, 7.35, 13, 1.3), rect(2.6, 4.2, 10.8, 1.1), rect(2.6, 10.7, 10.8, 1.1))
G["MiLock"] = j(arc_band(8, 6.2, 4.2, 1.6, -90, 90), rect(3.8, 6.2, 1.6, 1.4), rect(10.6, 6.2, 1.6, 1.4), rect(2.5, 7.4, 11, 7.6),
                circle(8, 10.5, 1.3, True), rect(7.4, 10.8, 1.2, 2.6, True))
G["MiKeyboard"] = j(ring_rect(0.8, 3.5, 14.4, 9.2, 1.3),
                    *[rect(x, 5.5, 1.6, 1.4) for x in (2.8, 5.1, 7.3, 9.6, 11.8)],
                    *[rect(x, 7.8, 1.6, 1.4) for x in (2.8, 5.1, 7.3, 9.6, 11.8)],
                    rect(4.4, 10.1, 7.2, 1.2))
G["MiSort"] = j(rect(1.2, 2.2, 7, 1.6), rect(1.2, 6.2, 5.2, 1.6), rect(1.2, 10.2, 3.4, 1.6), rect(11.1, 1.5, 1.7, 8.6), poly([(8.5, 9.6), (15.4, 9.6), (11.95, 14.6)]))
G["MiColumns"] = j(ring_rect(1, 2, 14, 12, 1.3), rect(2.3, 3.3, 11.4, 2.2), rect(5.6, 5.5, 1.2, 7.2), rect(9.4, 5.5, 1.2, 7.2))
G["MiSplitView"] = j(ring_rect(1, 2, 14, 12, 1.3), rect(7.4, 3.3, 1.2, 9.4))
G["MiFullView"] = j(ring_rect(1, 2, 14, 12, 1.3), rect(2.3, 3.3, 6, 9.4))
G["MiQuickView"] = j(ring_rect(1, 2, 14, 12, 1.3), rect(8, 3.3, 5.7, 9.4))
G["MiTheme"] = j(ring(8, 8, 7, 1.4), poly(ellipse_pts(8, 8, 5.6, 5.6, 180, 360, 24)))
G["MiHome"] = j(poly([(8, 1.2), (15.2, 8), (13.2, 8), (13.2, 14.8), (9.6, 14.8), (9.6, 10.4), (6.4, 10.4), (6.4, 14.8), (2.8, 14.8), (2.8, 8), (0.8, 8)]))
G["MiTabs"] = j(poly([(1, 4), (6.6, 4), (7.8, 6), (15, 6), (15, 14.5), (1, 14.5)]), rect(2.4, 7.4, 11.2, 5.7, True), rect(8.6, 1.5, 6.4, 3.3))
G["MiPlus"] = j(rect(7.1, 2, 1.8, 12), rect(2, 7.1, 12, 1.8))
G["MiRefresh"] = j(arc_band(8, 8.4, 6.2, 1.6, 50, 340), poly([(6.6, 0.6), (11.2, 2.5), (7.4, 5.6)]))
G["MiTerminal"] = j(ring_rect(1, 2, 14, 12, 1.4), poly([(3.4, 5.4), (7.4, 8.2), (3.4, 11), (3.4, 9.3), (5.2, 8.2), (3.4, 7.1)]), rect(8, 9.8, 4.6, 1.3))
G["MiNetwork"] = j(ring_rect(5, 1, 6, 4.4, 1.2), rect(7.4, 5.4, 1.2, 3), rect(2.2, 8.2, 11.6, 1.2), rect(2.2, 9.4, 1.2, 1.6), rect(12.6, 9.4, 1.2, 1.6),
                   ring_rect(0.5, 11, 4.6, 4, 1.1), ring_rect(10.9, 11, 4.6, 4, 1.1))
G["MiHistory"] = j(arc_band(8, 8, 6.8, 1.5, -100, 225), poly([(0.4, 2.8), (5.2, 5.4), (0.8, 7.6)]), rect(7.2, 4.2, 1.5, 4.6), bar(7.95, 8.3, 10.8, 10.6, 1.5))
G["MiFilter"] = j(poly([(1.2, 2), (14.8, 2), (9.6, 8.2), (9.6, 13.4), (6.4, 15), (6.4, 8.2)]))
G["MiDesktop"] = G["MiMonitor"]
G["MiDrive"] = j(ring_rect(1, 4, 14, 8, 1.3), rect(10.6, 8.6, 2.4, 1.4), rect(3, 8.8, 5, 1))
G["MiPhone"] = j(ring_rect(3.5, 1, 9, 14, 1.3), rect(6.5, 12.1, 3, 1.1))
G["MiHammer"] = j(bar(7.6, 1.6, 14.4, 8.4, 3.6), bar(1.8, 14.2, 10.2, 5.8, 2))
G["MiStacked"] = j(ring_rect(1, 1.5, 14, 13, 1.3), rect(2.3, 7.35, 11.4, 1.3))
G["MiComments"] = j(doc(2.5, 1, 11, 14), rect(5, 5.5, 6, 1.1), rect(5, 8, 6, 1.1), rect(5, 10.5, 4, 1.1))
G["MiCheckMark"] = G["MiCheck"]

# Overlays: drawn in the bottom-right quadrant; the base glyph is cut out around them (halo).
O = {}
O["OvPlus"] = j(rect(11.3, 8.4, 1.8, 7.2), rect(8.6, 11.1, 7.2, 1.8))
O["OvX"] = j(bar(9.2, 9.2, 15, 15, 1.8), bar(15, 9.2, 9.2, 15, 1.8))
O["OvArrowDown"] = j(rect(11.3, 8.2, 1.8, 3.8), poly([(9, 11.6), (15.4, 11.6), (12.2, 15.6)]))
O["OvArrowRight"] = j(rect(8.4, 11.3, 3.8, 1.8), poly([(11.6, 9), (15.6, 12.2), (11.6, 15.4)]))
O["OvArrowLeft"] = j(rect(11.8, 11.3, 3.8, 1.8), poly([(12.4, 9), (8.4, 12.2), (12.4, 15.4)]))
O["OvCheck"] = j(poly([(8.4, 12.2), (9.6, 11), (11.3, 12.7), (14.7, 9.3), (15.9, 10.5), (11.3, 15.1)]))
O["OvSearch"] = j(ring(11, 11, 2.6, 1.1), bar(12.8, 12.8, 15.6, 15.6, 1.4))
O["OvHalo"] = "F1 " + rect(7.4, 7.4, 8.6, 8.6)

out = ['<ResourceDictionary xmlns="https://github.com/avaloniaui"', '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
       '  <!-- Generated by build/make_menu_icons.py: 16x16 nonzero glyphs for menu items (see Services/MenuIcons.cs) -->']
for k, v in {**G, **O}.items():
    out.append(f'  <StreamGeometry x:Key="{k}">{v}</StreamGeometry>')
out.append('</ResourceDictionary>')
open(sys.argv[1], "w", encoding="utf-8").write("\n".join(out) + "\n")
print(len(G), "glyphs,", len(O), "overlays")
