"""
finetune.py — Fine-tune LayoutLMv3 on TSU-ORGDOCX dataset.

Architecture:
  • Token-classification head   → per-word BIO field labels (e.g. B-ACTIVITY_TITLE)
  • Sequence-classification head → document class (SF08 / Accomplishment / Accreditation)

Both heads are trained jointly with a weighted loss:
  total_loss = token_loss + α * seq_loss   (α = 0.3, see config)

Training protocol per /docs/06-CALSV-ENGINE.md:
  - Base: microsoft/layoutlmv3-base
  - 15 epochs, LR 3e-5, early stopping patience 3 on val macro-F1
  - Gradient accumulation × 4 (effective batch = 16)
  - AdamW + linear warmup (10 % of steps)

Usage:
  python -m training.scripts.finetune \\
    --config training/configs/layoutlmv3_base.yaml \\
    --data   data/processed/             # must contain train.jsonl / val.jsonl
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
from pathlib import Path
from typing import Any

import numpy as np
import torch
import yaml
from PIL import Image as PILImage
from torch import nn
from torch.utils.data import DataLoader, Dataset
from transformers import (
    AutoProcessor,
    LayoutLMv3ForTokenClassification,
    get_linear_schedule_with_warmup,
)

# ─── Reproducibility ─────────────────────────────────────────────────────────

SEED = 42
random.seed(SEED)
np.random.seed(SEED)
torch.manual_seed(SEED)
if torch.cuda.is_available():
    torch.cuda.manual_seed_all(SEED)


# ─── Dataset ─────────────────────────────────────────────────────────────────

class TsuOrgDataset(Dataset):
    """
    Reads a prepared JSONL (output of prepare_dataset.py) and returns
    LayoutLMv3 processor outputs + sequence-class label.
    """

    def __init__(
        self,
        jsonl_path: str | Path,
        processor: AutoProcessor,
        label_map: dict[str, int],
        max_length: int = 512,
        image_size: int = 224,
    ) -> None:
        self.processor  = processor
        self.label_map  = label_map
        self.max_length = max_length
        self.image_size = image_size

        with open(jsonl_path, encoding="utf-8") as f:
            self.records = [json.loads(line) for line in f if line.strip()]

    def __len__(self) -> int:
        return len(self.records)

    def __getitem__(self, idx: int) -> dict[str, torch.Tensor]:
        rec = self.records[idx]

        image = PILImage.open(rec["image_path"]).convert("RGB")
        image = image.resize((self.image_size, self.image_size))

        words      = rec["words"]
        boxes      = rec["boxes"]
        word_labels= rec["word_labels"]

        encoding = self.processor(
            images=image,
            text=words,
            boxes=boxes,
            word_labels=word_labels,
            max_length=self.max_length,
            padding="max_length",
            truncation=True,
            return_tensors="pt",
        )

        # Squeeze the batch dim added by the processor
        item = {k: v.squeeze(0) for k, v in encoding.items()}
        item["doc_labels"] = torch.tensor(rec["doc_label"], dtype=torch.long)
        return item


# ─── Dual-head model wrapper ─────────────────────────────────────────────────

class TsuOrgLayoutLMv3(nn.Module):
    """
    Wraps LayoutLMv3ForTokenClassification and adds a sequence-classification
    head on top of the [CLS] token representation.
    """

    def __init__(self, base_model_name: str, num_token_labels: int, num_doc_classes: int) -> None:
        super().__init__()
        self.token_model = LayoutLMv3ForTokenClassification.from_pretrained(
            base_model_name,
            num_labels=num_token_labels,
            ignore_mismatched_sizes=True,
        )
        hidden = self.token_model.config.hidden_size
        self.seq_head = nn.Sequential(
            nn.Dropout(0.1),
            nn.Linear(hidden, num_doc_classes),
        )

    def forward(
        self,
        input_ids,
        attention_mask,
        token_type_ids=None,
        bbox=None,
        pixel_values=None,
        labels=None,
        doc_labels=None,
        seq_loss_weight: float = 0.3,
    ) -> dict[str, torch.Tensor]:
        outputs = self.token_model(
            input_ids=input_ids,
            attention_mask=attention_mask,
            token_type_ids=token_type_ids,
            bbox=bbox,
            pixel_values=pixel_values,
            labels=labels,
            output_hidden_states=True,
        )

        # CLS token from last hidden state
        cls_hidden = outputs.hidden_states[-1][:, 0, :]
        seq_logits = self.seq_head(cls_hidden)

        total_loss = outputs.loss  # token-classification cross-entropy

        if doc_labels is not None:
            seq_loss   = nn.CrossEntropyLoss()(seq_logits, doc_labels)
            total_loss = total_loss + seq_loss_weight * seq_loss

        return {
            "loss":       total_loss,
            "token_logits": outputs.logits,
            "seq_logits": seq_logits,
        }


# ─── Metrics ─────────────────────────────────────────────────────────────────

def compute_metrics(
    token_preds: list[list[int]],
    token_labels: list[list[int]],
    seq_preds: list[int],
    seq_labels: list[int],
    id2label: dict[int, str],
    doc_classes: list[str],
) -> dict[str, float]:
    from collections import defaultdict

    # Token-level macro-F1 (ignoring -100 padding)
    tp: dict[str, int] = defaultdict(int)
    fp: dict[str, int] = defaultdict(int)
    fn: dict[str, int] = defaultdict(int)

    for pred_seq, label_seq in zip(token_preds, token_labels):
        for p, l in zip(pred_seq, label_seq):
            if l == -100:
                continue
            pl = id2label.get(p, "O")
            ll = id2label.get(l, "O")
            if pl == ll:
                tp[ll] += 1
            else:
                fp[pl] += 1
                fn[ll] += 1

    f1s = []
    for label in set(list(tp) + list(fp) + list(fn)):
        if label == "O":
            continue
        prec = tp[label] / (tp[label] + fp[label] + 1e-9)
        rec  = tp[label] / (tp[label] + fn[label] + 1e-9)
        f1   = 2 * prec * rec / (prec + rec + 1e-9)
        f1s.append(f1)

    token_macro_f1 = float(np.mean(f1s)) if f1s else 0.0

    # Sequence-level accuracy
    correct = sum(p == l for p, l in zip(seq_preds, seq_labels))
    seq_acc = correct / len(seq_labels) if seq_labels else 0.0

    return {"token_macro_f1": token_macro_f1, "seq_accuracy": seq_acc}


# ─── Training loop ────────────────────────────────────────────────────────────

def train(cfg: dict[str, Any], data_dir: Path) -> None:
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using device: {device}")

    # Label maps
    label_map_path = data_dir / "label_map.json"
    label_map      = json.loads(label_map_path.read_text(encoding="utf-8"))
    token_label2id = label_map["token_labels"]
    doc_classes    = list(label_map["doc_classes"].keys())
    id2label       = {int(k): v for k, v in label_map["id2label"].items()}

    num_token_labels = len(token_label2id)
    num_doc_classes  = len(doc_classes)

    processor = AutoProcessor.from_pretrained(
        cfg["base_model"], apply_ocr=False)

    train_ds = TsuOrgDataset(data_dir / "train.jsonl", processor,
                              token_label2id, cfg["max_seq_length"], cfg["image_size"])
    val_ds   = TsuOrgDataset(data_dir / "val.jsonl",   processor,
                              token_label2id, cfg["max_seq_length"], cfg["image_size"])

    train_loader = DataLoader(train_ds, batch_size=cfg["batch_size"], shuffle=True,  num_workers=2)
    val_loader   = DataLoader(val_ds,   batch_size=cfg["batch_size"], shuffle=False, num_workers=2)

    model = TsuOrgLayoutLMv3(cfg["base_model"], num_token_labels, num_doc_classes).to(device)

    total_steps   = math.ceil(len(train_loader) / cfg["gradient_accumulation_steps"]) * cfg["epochs"]
    warmup_steps  = int(0.10 * total_steps)

    optimizer = torch.optim.AdamW(
        model.parameters(),
        lr=cfg["learning_rate"],
        weight_decay=cfg["weight_decay"],
    )
    scheduler = get_linear_schedule_with_warmup(optimizer, warmup_steps, total_steps)

    out_dir = Path(cfg["output_dir"])
    out_dir.mkdir(parents=True, exist_ok=True)

    best_metric   = -1.0
    patience_left = cfg["early_stopping_patience"]
    history: list[dict] = []

    print(f"\nStarting training: {cfg['epochs']} epochs, {total_steps} optimizer steps")

    for epoch in range(1, cfg["epochs"] + 1):
        # ── Train ─────────────────────────────────────────────────────────────
        model.train()
        optimizer.zero_grad()
        epoch_loss = 0.0
        step = 0

        for batch_idx, batch in enumerate(train_loader):
            inputs = {k: v.to(device) for k, v in batch.items()
                      if k not in ("doc_labels",)}
            doc_labels = batch["doc_labels"].to(device)

            out = model(**inputs, doc_labels=doc_labels, seq_loss_weight=0.3)
            loss = out["loss"] / cfg["gradient_accumulation_steps"]
            loss.backward()
            epoch_loss += out["loss"].item()

            if (batch_idx + 1) % cfg["gradient_accumulation_steps"] == 0:
                torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
                optimizer.step()
                scheduler.step()
                optimizer.zero_grad()
                step += 1

            if batch_idx % 20 == 0:
                print(f"  Epoch {epoch} | step {batch_idx}/{len(train_loader)} "
                      f"| loss {out['loss'].item():.4f}")

        avg_train_loss = epoch_loss / len(train_loader)

        # ── Validate ──────────────────────────────────────────────────────────
        model.eval()
        all_token_preds, all_token_labels = [], []
        all_seq_preds, all_seq_labels = [], []
        val_loss = 0.0

        with torch.no_grad():
            for batch in val_loader:
                inputs     = {k: v.to(device) for k, v in batch.items() if k != "doc_labels"}
                doc_labels = batch["doc_labels"].to(device)
                out        = model(**inputs, doc_labels=doc_labels)
                val_loss  += out["loss"].item()

                token_logits = out["token_logits"]
                seq_logits   = out["seq_logits"]
                label_ids    = inputs.get("labels")

                if label_ids is not None:
                    for pred_row, label_row in zip(
                            token_logits.argmax(-1).cpu().tolist(),
                            label_ids.cpu().tolist()):
                        all_token_preds.append(pred_row)
                        all_token_labels.append(label_row)

                all_seq_preds.extend(seq_logits.argmax(-1).cpu().tolist())
                all_seq_labels.extend(doc_labels.cpu().tolist())

        metrics = compute_metrics(
            all_token_preds, all_token_labels,
            all_seq_preds,   all_seq_labels,
            id2label, doc_classes)

        avg_val_loss = val_loss / len(val_loader)
        combined     = 0.7 * metrics["token_macro_f1"] + 0.3 * metrics["seq_accuracy"]

        print(f"\nEpoch {epoch:02d}: "
              f"train_loss={avg_train_loss:.4f}  val_loss={avg_val_loss:.4f}  "
              f"token_F1={metrics['token_macro_f1']:.4f}  "
              f"seq_acc={metrics['seq_accuracy']:.4f}  "
              f"combined={combined:.4f}")

        history.append({
            "epoch": epoch,
            "train_loss": avg_train_loss,
            "val_loss": avg_val_loss,
            **metrics,
            "combined": combined,
        })

        if combined > best_metric:
            best_metric   = combined
            patience_left = cfg["early_stopping_patience"]
            best_ckpt = out_dir / "best"
            best_ckpt.mkdir(exist_ok=True)
            # Save token classification model weights
            model.token_model.save_pretrained(str(best_ckpt))
            processor.save_pretrained(str(best_ckpt))
            # Save the seq head separately
            torch.save(model.seq_head.state_dict(), best_ckpt / "seq_head.pt")
            print(f"  ✓ Saved best model (combined={combined:.4f})")
        else:
            patience_left -= 1
            print(f"  No improvement. Patience: {patience_left}/{cfg['early_stopping_patience']}")
            if patience_left == 0:
                print("  Early stopping triggered.")
                break

    # Save training history
    (out_dir / "history.json").write_text(
        json.dumps(history, indent=2), encoding="utf-8")

    # Save config alongside model
    (out_dir / "train_config.yaml").write_text(
        yaml.dump(cfg), encoding="utf-8")

    print(f"\nTraining complete. Best combined metric: {best_metric:.4f}")
    print(f"Model saved to: {out_dir / 'best'}")


# ─── Entrypoint ───────────────────────────────────────────────────────────────

def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="training/configs/layoutlmv3_base.yaml")
    ap.add_argument("--data",   default="data/processed")
    args = ap.parse_args()

    cfg      = yaml.safe_load(Path(args.config).read_text(encoding="utf-8"))
    data_dir = Path(args.data)

    required = ["train.jsonl", "val.jsonl", "label_map.json"]
    missing  = [f for f in required if not (data_dir / f).exists()]
    if missing:
        print(f"[ERROR] Missing in {data_dir}: {missing}")
        print("  Run prepare_dataset.py --split first.")
        raise SystemExit(1)

    train(cfg, data_dir)


if __name__ == "__main__":
    main()
