# Historical training metrics — NOT production-ready

Files in this folder (`training_result.json`, `eval_report.json`) record an
earlier SF08 experiment (~89 pages) with reported token macro-F1 ≈ 1.0.

**Do not** use these numbers to claim the model is ready for deployment.

Likely contributing factors under the old pipeline:

- Very small / homogeneous test set
- Possible train/test leakage (random shuffle without grouping)
- Missing full-page background `O` tokens
- Shared region bounding boxes for multi-word fields
- Possible `rectanglelabels` parsing issues on newer exports

After the corrected dataset pipeline validates, run a fresh `evaluate.py` on a
group-aware held-out split and replace reliance on these files.
