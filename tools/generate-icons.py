"""Rebuild the checked-in desktop artwork. Requires resvg-py and Pillow."""

from io import BytesIO
from pathlib import Path
import struct
from xml.etree import ElementTree as ET

from PIL import Image
from resvg_py import svg_to_bytes


ROOT = Path(__file__).resolve().parents[1] / "src/AiTranslate.Desktop/Assets/Brand"
SVG = "http://www.w3.org/2000/svg"
ET.register_namespace("", SVG)
ET.register_namespace("xlink", "http://www.w3.org/1999/xlink")
master = (ROOT / "ai-translate.svg").read_text(encoding="utf-8")
small = (ROOT / "ai-translate-small.svg").read_text(encoding="utf-8")


def render(art, size):
    # Oversample before reduction to keep the angled paper edges smooth at 1x.
    pixels = svg_to_bytes(svg_string=art, width=size * 2, height=size * 2)
    return Image.open(BytesIO(pixels)).convert("RGBA").resize((size, size), Image.Resampling.LANCZOS)


def png(image):
    stream = BytesIO()
    image.save(stream, "PNG", optimize=True)
    return stream.getvalue()


def write(relative, data):
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def symbolic_svg():
    # Paper and foil become holes through which the menu bar can show.
    root = ET.fromstring(small)
    mask = ET.SubElement(root.find(f"{{{SVG}}}defs"), f"{{{SVG}}}mask", id="ink")
    for layer in list(root):
        if "fill" in layer.attrib:
            layer.set("fill", "black" if layer.get("id") in ("paper", "foil") else "white")
            root.remove(layer)
            mask.append(layer)
    ET.SubElement(root, f"{{{SVG}}}rect", width="16", height="16", fill="black", mask="url(#ink)")
    return ET.tostring(root, encoding="unicode")


def mac_art(art):
    # A warm porcelain field follows the macOS icon grid with transparent margins.
    inset = ET.fromstring(art)
    inset.attrib.update(x="11", y="11", width="78", height="78")
    return f'''<svg xmlns="{SVG}" width="1024" height="1024" viewBox="0 0 100 100">
      <defs><linearGradient id="tile" x2=".8" y2="1">
        <stop stop-color="#FFFCF6"/><stop offset="1" stop-color="#F0E6DA"/>
      </linearGradient></defs>
      <rect x="9" y="10.5" width="82" height="82" rx="18.5" fill="#594639" opacity=".19"/>
      <rect x="9" y="9" width="82" height="82" rx="18.5" fill="url(#tile)" stroke="#CFBEB0" stroke-width=".4"/>
      {ET.tostring(inset, encoding="unicode")}
    </svg>'''


def write_ico(relative, art_for_size, sizes):
    images = [render(art_for_size(size), size) for size in sizes]
    stream = BytesIO()
    images[-1].save(stream, "ICO", sizes=[im.size for im in images], append_images=images[:-1])
    write(relative, stream.getvalue())


def windows_shell_art(art, view_box):
    # Frame the visible book (including the ribbon) inside the square taskbar slot.
    # The upright 16px artwork and the angled 24-unit master have different bounds.
    root = ET.fromstring(art)
    root.attrib.update(viewBox=view_box, preserveAspectRatio="none")
    return ET.tostring(root, encoding="unicode")


def main():
    def app_art(size):
        return small if size <= 24 else master

    symbolic = symbolic_svg()
    write("ai-translate-1024.png", png(render(master, 1024)))
    write("linux/hicolor/symbolic/apps/ai-translate-symbolic.svg", symbolic.encode("utf-8"))
    for size in (16, 22, 24, 32, 48, 64, 96, 128, 256, 512):
        write(f"linux/hicolor/{size}x{size}/apps/ai-translate.png", png(render(app_art(size), size)))

    windows_small = windows_shell_art(small, "0.75 0.60 14.5 14.65")
    windows_book = windows_shell_art(master, "1.95 0.85 19.85 21.95")
    write_ico("windows/ai-translate.ico", lambda size: windows_small if size <= 24 else windows_book,
              (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 96, 128, 256))
    write_ico("windows/tray.ico", lambda _: small, (16, 20, 24, 28, 32, 40, 48, 64))
    write("linux/tray.png", png(render(small, 64)))
    for size, suffix in ((18, ""), (36, "@2x")):
        write(f"macos/TrayTemplate{suffix}.png", png(render(symbolic, size)))

    # ICNS uses separate type codes for logical sizes even when pixel sizes coincide.
    # At 16pt use the optical mark, including on Retina displays.
    chunks = []
    for code, points, scale in (
        ("icp4", 16, 1), ("ic11", 16, 2), ("icp5", 32, 1), ("ic12", 32, 2),
        ("icp6", 64, 1), ("ic07", 128, 1), ("ic13", 128, 2), ("ic08", 256, 1),
        ("ic14", 256, 2), ("ic09", 512, 1), ("ic10", 512, 2),
    ):
        data = png(render(mac_art(app_art(points)), points * scale))
        chunks.append(struct.pack(">4sI", code.encode(), len(data) + 8) + data)
    body = b"".join(chunks)
    write("macos/ai-translate.icns", struct.pack(">4sI", b"icns", len(body) + 8) + body)


if __name__ == "__main__":
    main()
