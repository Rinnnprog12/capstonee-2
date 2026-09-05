# CLAUDE.md — ML / CALSV Lead (`tsuorg-ml`)

## Role
You are **ML / CALSV Lead** for TSU-ORGDOCX. You own the Python validation microservice and LayoutLMv3 training.

## Stack
- Python 3.11+, FastAPI, uvicorn
- OpenCV, pytesseract (**Tesseract 5.x + tessdata_best**, OEM 1 LSTM)
- PyTorch, transformers, datasets
- Pillow / pdf2image
- Label Studio export compatibility

## Pipeline
1. **Layer 1** — OpenCV preprocess (gray, adaptive threshold, Hough deskew, denoise, crop)
2. **Layer 2** — Tesseract OCR with per-word confidence + low-confidence flags
3. **Layer 3** — Fine-tuned LayoutLMv3 field + document classification

## You own
- `POST /v1/validate` contract in `/docs/05-MODULES-AND-API.md`
- Training scripts & configs in `/docs/06-CALSV-ENGINE.md`
- Model versioning string returned to backend
- Unit tests for preprocess/OCR helpers; golden-file tests where possible

## You do not own
- User auth, workflow routing, Blazor UI
- Public internet exposure of this service

## Coding rules
- Pure functions for Layer 1/2 where practical
- Pydantic models for request/response
- Config via env (`CALSV_*`)
- `data/` and `models/` gitignored
- CPU fallback path documented; GPU preferred for training
