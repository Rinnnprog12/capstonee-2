"""
LayoutLMv3 model loader with lazy initialization and CPU/GPU fallback.

Loading strategy:
  1. Check CALSV_MODEL_DIR for a fine-tuned checkpoint.
  2. If found → load token-classification model + optional seq_head.pt.
  3. If not found → return None (inference falls back to rule-based validation).

Training (finetune.py) saves:
  - HuggingFace token model under models/.../best/
  - Separate torch state_dict seq_head.pt (document-type classifier on CLS)
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from transformers import LayoutLMv3ForTokenClassification, LayoutLMv3Processor

logger = logging.getLogger(__name__)

_processor: "LayoutLMv3Processor | None" = None
_model: "LayoutLMv3ForTokenClassification | None" = None
_seq_head: Any = None
_model_version: str = "not-loaded"
_load_attempted: bool = False

# Document-type classes used by finetune.py seq head
DOC_TYPE_CLASSES = ["SF08", "ACCOMPLISHMENT", "ACCREDITATION"]


def load_model(model_dir: Path) -> bool:
    """
    Attempt to load the fine-tuned LayoutLMv3 model (+ seq_head if present).
    Returns True if successful, False if model not found or load fails.
    """
    global _processor, _model, _seq_head, _model_version, _load_attempted
    _load_attempted = True

    checkpoint = _resolve_checkpoint(model_dir)
    if checkpoint is None:
        logger.info(
            "Model directory %s (or %s/best) not found — rule-based fallback will be used. "
            "Place Label Studio datasets under data/raw/, run prepare_dataset + finetune.",
            model_dir,
            model_dir,
        )
        return False

    try:
        import torch
        from torch import nn
        from transformers import LayoutLMv3ForTokenClassification, LayoutLMv3Processor

        logger.info("Loading LayoutLMv3 from %s", checkpoint)
        _processor = LayoutLMv3Processor.from_pretrained(str(checkpoint), apply_ocr=False)
        _model = LayoutLMv3ForTokenClassification.from_pretrained(str(checkpoint))

        device = "cuda" if torch.cuda.is_available() else "cpu"
        _model = _model.to(device)
        _model.eval()

        # Optional dual-head sequence classifier saved by finetune.py
        seq_path = checkpoint / "seq_head.pt"
        if seq_path.exists():
            hidden = _model.config.hidden_size
            head = nn.Sequential(
                nn.Dropout(0.1),
                nn.Linear(hidden, len(DOC_TYPE_CLASSES)),
            )
            head.load_state_dict(torch.load(seq_path, map_location=device, weights_only=True))
            head.to(device)
            head.eval()
            _seq_head = head
            logger.info("Loaded seq_head.pt (%d doc-type classes)", len(DOC_TYPE_CLASSES))
        else:
            _seq_head = None
            logger.warning(
                "seq_head.pt not found in %s — neural path will use token head + rules only",
                checkpoint,
            )

        _model_version = model_dir.name if model_dir.name else checkpoint.name
        if _model_version == "best":
            _model_version = checkpoint.parent.name or "layoutlmv3-tsu-v1"
        logger.info("LayoutLMv3 loaded on %s — model version: %s", device, _model_version)
        return True

    except Exception as exc:
        logger.error("Failed to load LayoutLMv3 model: %s", exc)
        _model = None
        _processor = None
        _seq_head = None
        return False


def get_model():
    return _model


def get_seq_head():
    return _seq_head


def get_processor():
    return _processor


def get_model_version() -> str:
    return _model_version


def is_model_loaded() -> bool:
    return _model is not None


def ensure_loaded(model_dir: Path) -> bool:
    """Load once; subsequent calls return cached state."""
    if _load_attempted:
        return is_model_loaded()
    return load_model(model_dir)


def _resolve_checkpoint(model_dir: Path) -> Path | None:
    """
    finetune.py saves under models/<name>/best/.
    Accept either that folder or a promoted root checkpoint that contains config.json.
    """
    candidates = [
        model_dir / "best",
        model_dir,
    ]
    for path in candidates:
        if (path / "config.json").exists() or (path / "preprocessor_config.json").exists():
            return path
        # HF sometimes only has model.safetensors + config after save_pretrained
        if path.is_dir() and any(path.glob("model.*")) and (path / "config.json").exists():
            return path
    # Empty dir or missing weights
    if model_dir.is_dir() and any(model_dir.iterdir()):
        # Prefer best/ even if only partially present so logs are clear
        best = model_dir / "best"
        if best.is_dir() and any(best.iterdir()):
            return best
    return None
