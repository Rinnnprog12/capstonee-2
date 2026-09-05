"""Normalize CALSV scores so the backend can persist a single 0–1 unit.

Layer 2 (Tesseract) emits per-token confidence on 0–100.
Layer 3 (rules / LayoutLM) emits document confidence on 0–1.
Dashboard / tracker percentages are computed in ASP.NET from DB rows —
this module only keeps the ML JSON contract consistent.
"""

from __future__ import annotations


def as_unit(value: float | int | None) -> float:
    """Clamp a score to [0, 1]. Values > 1 are treated as 0–100 percent."""
    if value is None:
        return 0.0
    v = float(value)
    if v < 0:
        return 0.0
    if v > 1.0:
        v = v / 100.0
    return round(min(1.0, v), 4)


def as_percent(value: float | int | None) -> int:
    """Integer 0–100 for logs / UI copy. Does not write to the database."""
    return int(round(as_unit(value) * 100))
