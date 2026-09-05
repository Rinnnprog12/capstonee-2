"""
evaluate.py — Evaluate a trained TsuOrgLayoutLMv3 model on the test split.

Outputs:
  • Token-level: Precision / Recall / F1 per BIO label, macro average
  • Sequence-level: Accuracy, per-class Precision / Recall / F1, confusion matrix
  • Optional: exports misclassified examples to misclassified.jsonl

Usage:
  python -m training.scripts.evaluate \\
    --model  models/layoutlmv3-tsu-v1/best \\
    --data   data/processed/test.jsonl \\
    --labels data/processed/label_map.json \\
    --output results/eval_report.json
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np
import torch
from PIL import Image as PILImage
from torch.utils.data import DataLoader
from transformers import AutoProcessor, LayoutLMv3ForTokenClassification

from training.scripts.finetune import TsuOrgDataset, TsuOrgLayoutLMv3


# ─── Per-class token F1 ──────────────────────────────────────────────────────

def token_classification_report(
    all_preds: list[list[int]],
    all_labels: list[list[int]],
    id2label: dict[int, str],
) -> dict[str, Any]:
    tp: dict[str, int] = defaultdict(int)
    fp: dict[str, int] = defaultdict(int)
    fn: dict[str, int] = defaultdict(int)

    for pred_seq, label_seq in zip(all_preds, all_labels):
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

    report: dict[str, Any] = {}
    macro_p, macro_r, macro_f1 = [], [], []

    all_labels_set = sorted(set(list(tp) + list(fp) + list(fn)) - {"O"})
    for label in all_labels_set:
        prec = tp[label] / (tp[label] + fp[label] + 1e-9)
        rec  = tp[label] / (tp[label] + fn[label] + 1e-9)
        f1   = 2 * prec * rec / (prec + rec + 1e-9)
        report[label] = {"precision": round(prec, 4), "recall": round(rec, 4), "f1": round(f1, 4)}
        macro_p.append(prec); macro_r.append(rec); macro_f1.append(f1)

    report["macro"] = {
        "precision": round(float(np.mean(macro_p)), 4) if macro_p else 0.0,
        "recall":    round(float(np.mean(macro_r)), 4) if macro_r else 0.0,
        "f1":        round(float(np.mean(macro_f1)), 4) if macro_f1 else 0.0,
    }
    return report


# ─── Sequence classification report ─────────────────────────────────────────

def seq_classification_report(
    preds: list[int],
    labels: list[int],
    class_names: list[str],
) -> dict[str, Any]:
    n = len(class_names)
    cm = np.zeros((n, n), dtype=int)
    for p, l in zip(preds, labels):
        cm[l][p] += 1

    report: dict[str, Any] = {"confusion_matrix": cm.tolist()}
    macro_p, macro_r, macro_f1 = [], [], []

    for i, cls in enumerate(class_names):
        tp = cm[i][i]
        fp = cm[:, i].sum() - tp
        fn = cm[i, :].sum() - tp
        prec = tp / (tp + fp + 1e-9)
        rec  = tp / (tp + fn + 1e-9)
        f1   = 2 * prec * rec / (prec + rec + 1e-9)
        report[cls] = {"precision": round(float(prec), 4), "recall": round(float(rec), 4), "f1": round(float(f1), 4)}
        macro_p.append(prec); macro_r.append(rec); macro_f1.append(f1)

    correct = sum(p == l for p, l in zip(preds, labels))
    report["accuracy"] = round(correct / len(labels), 4) if labels else 0.0
    report["macro"] = {
        "precision": round(float(np.mean(macro_p)), 4),
        "recall":    round(float(np.mean(macro_r)), 4),
        "f1":        round(float(np.mean(macro_f1)), 4),
    }
    return report


# ─── Main evaluation ─────────────────────────────────────────────────────────

def evaluate(
    model_dir: str,
    data_path: str,
    label_map_path: str,
    output_path: str,
    batch_size: int = 4,
    export_misclassified: bool = False,
) -> dict[str, Any]:
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Using device: {device}")

    label_map   = json.loads(Path(label_map_path).read_text(encoding="utf-8"))
    id2label    = {int(k): v for k, v in label_map["id2label"].items()}
    doc_classes = list(label_map["doc_classes"].keys())
    num_token   = len(label_map["token_labels"])
    num_seq     = len(doc_classes)

    processor = AutoProcessor.from_pretrained(model_dir, apply_ocr=False)

    model = TsuOrgLayoutLMv3(model_dir, num_token, num_seq, id2label=id2label)
    seq_head_path = Path(model_dir) / "seq_head.pt"
    if seq_head_path.exists():
        model.seq_head.load_state_dict(torch.load(seq_head_path, map_location="cpu"))
    model.to(device).eval()

    test_ds = TsuOrgDataset(data_path, processor, label_map["token_labels"])
    loader  = DataLoader(test_ds, batch_size=batch_size, shuffle=False)

    all_token_preds, all_token_labels = [], []
    all_seq_preds, all_seq_labels = [], []
    misclassified = []

    with torch.no_grad():
        for batch_idx, batch in enumerate(loader):
            inputs     = {k: v.to(device) for k, v in batch.items() if k != "doc_labels"}
            doc_labels = batch["doc_labels"].to(device)
            out        = model(**inputs, doc_labels=doc_labels)

            tok_logits = out["token_logits"]
            seq_logits = out["seq_logits"]
            label_ids  = inputs.get("labels")

            if label_ids is not None:
                for pred_row, label_row in zip(
                        tok_logits.argmax(-1).cpu().tolist(),
                        label_ids.cpu().tolist()):
                    all_token_preds.append(pred_row)
                    all_token_labels.append(label_row)

            seq_p = seq_logits.argmax(-1).cpu().tolist()
            seq_l = doc_labels.cpu().tolist()
            all_seq_preds.extend(seq_p)
            all_seq_labels.extend(seq_l)

            if export_misclassified:
                for i, (p, l) in enumerate(zip(seq_p, seq_l)):
                    if p != l:
                        global_idx = batch_idx * batch_size + i
                        rec = test_ds.records[global_idx] if global_idx < len(test_ds.records) else {}
                        misclassified.append({
                            "image_path":  rec.get("image_path"),
                            "true_label":  doc_classes[l],
                            "pred_label":  doc_classes[p],
                        })

    tok_report = token_classification_report(all_token_preds, all_token_labels, id2label)
    seq_report = seq_classification_report(all_seq_preds, all_seq_labels, doc_classes)

    result = {
        "token_classification": tok_report,
        "sequence_classification": seq_report,
        "num_test_samples": len(test_ds),
    }

    out_path = Path(output_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"\nEval report → {out_path}")

    print(f"\n{'='*60}")
    print(f"Token macro-F1 : {tok_report['macro']['f1']:.4f}")
    print(f"Seq accuracy   : {seq_report['accuracy']:.4f}")
    print(f"Seq macro-F1   : {seq_report['macro']['f1']:.4f}")
    print(f"{'='*60}")

    if export_misclassified and misclassified:
        mis_path = out_path.parent / "misclassified.jsonl"
        with mis_path.open("w", encoding="utf-8") as f:
            for r in misclassified:
                f.write(json.dumps(r) + "\n")
        print(f"Misclassified ({len(misclassified)}) → {mis_path}")

    return result


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model",  required=True)
    ap.add_argument("--data",   required=True)
    ap.add_argument("--labels", default="data/processed/label_map.json")
    ap.add_argument("--output", default="results/eval_report.json")
    ap.add_argument("--batch-size", type=int, default=4)
    ap.add_argument("--export-misclassified", action="store_true")
    args = ap.parse_args()

    evaluate(
        args.model,
        args.data,
        args.labels,
        args.output,
        args.batch_size,
        args.export_misclassified,
    )


if __name__ == "__main__":
    main()
