"""Convert PDF files to PNG page images (pypdfium2 — no Poppler required)."""

from __future__ import annotations

import io
import re
from pathlib import Path

from PIL import Image


def slugify(name: str, max_len: int = 80) -> str:
    base = Path(name).stem.lower()
    base = re.sub(r"[^a-z0-9]+", "-", base).strip("-")
    return base[:max_len] or "page"


def pdf_to_png_pages(pdf_path: Path, dpi: int = 300) -> list[bytes]:
    import pypdfium2 as pdfium

    data = pdf_path.read_bytes()
    pdf = pdfium.PdfDocument(data)
    scale = dpi / 72.0
    pages: list[bytes] = []
    for page in pdf:
        bitmap = page.render(scale=scale, rotation=0)
        pil_img = bitmap.to_pil().convert("RGB")
        buf = io.BytesIO()
        pil_img.save(buf, format="PNG")
        pages.append(buf.getvalue())
    return pages


def image_to_png_bytes(image_path: Path) -> bytes:
    with Image.open(image_path) as im:
        buf = io.BytesIO()
        im.convert("RGB").save(buf, format="PNG")
        return buf.getvalue()
