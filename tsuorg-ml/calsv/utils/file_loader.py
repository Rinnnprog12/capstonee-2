"""Download a file from a URL (Azure Blob signed URL) and convert to image bytes."""

from __future__ import annotations

import io
import logging
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

import httpx

logger = logging.getLogger(__name__)


@dataclass
class LoadedFile:
    image_bytes: bytes          # always a PNG image after load
    page_count: int
    mime_type: str


def load_file(url: str, target_dpi: int = 300) -> LoadedFile:
    """
    Download from signed URL, detect format, and return normalised PNG bytes.
    Supports: PDF (first page), PNG, JPEG, TIFF.
    """
    raw = _download(url)
    mime = _detect_mime(raw)

    if mime == "application/pdf":
        pages = _pdf_to_images(raw, dpi=target_dpi)
        return LoadedFile(image_bytes=pages[0], page_count=len(pages), mime_type=mime)

    png = _image_to_png(raw)
    return LoadedFile(image_bytes=png, page_count=1, mime_type=mime)


def load_file_bytes(raw: bytes, target_dpi: int = 300) -> LoadedFile:
    """Same as load_file but accepts raw bytes directly (for tests / local files)."""
    mime = _detect_mime(raw)
    if mime == "application/pdf":
        pages = _pdf_to_images(raw, dpi=target_dpi)
        return LoadedFile(image_bytes=pages[0], page_count=len(pages), mime_type=mime)
    png = _image_to_png(raw)
    return LoadedFile(image_bytes=png, page_count=1, mime_type=mime)


def _download(url: str) -> bytes:
    try:
        with httpx.Client(timeout=60.0, follow_redirects=True) as client:
            resp = client.get(url)
            resp.raise_for_status()
            return resp.content
    except httpx.HTTPError as exc:
        if ":10000" in url:
            raise RuntimeError(
                "Could not download the form (Azurite/blob emulator is not running). "
                "Restart TsuOrg.Api (local disk storage) and re-upload the PDF."
            ) from exc
        raise


def _detect_mime(data: bytes) -> str:
    if data[:4] == b"%PDF":
        return "application/pdf"
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return "image/png"
    if data[:2] in (b"\xff\xd8", b"\xff\xe0", b"\xff\xe1"):
        return "image/jpeg"
    if data[:4] in (b"II*\x00", b"MM\x00*"):
        return "image/tiff"
    return "application/octet-stream"


def _pdf_to_images(data: bytes, dpi: int) -> list[bytes]:
    """Convert PDF pages to PNG byte arrays using pypdfium2 (no poppler needed)."""
    try:
        import pypdfium2 as pdfium
    except ImportError as exc:
        raise RuntimeError("pypdfium2 required for PDF support: pip install pypdfium2") from exc

    pdf = pdfium.PdfDocument(data)
    results: list[bytes] = []
    scale = dpi / 72.0
    for page in pdf:
        bitmap = page.render(scale=scale, rotation=0)
        pil_img = bitmap.to_pil()
        buf = io.BytesIO()
        pil_img.save(buf, format="PNG")
        results.append(buf.getvalue())

    return results if results else [b""]


def _image_to_png(data: bytes) -> bytes:
    from PIL import Image
    img = Image.open(io.BytesIO(data)).convert("RGB")
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()
