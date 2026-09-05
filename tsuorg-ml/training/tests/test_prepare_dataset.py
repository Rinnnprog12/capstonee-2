"""Unit tests for dataset prep label resolution and LS parsing (no OCR required)."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from training.utils.label_vocab import load_label_aliases, resolve_token_label
from training.scripts.prepare_dataset import (
    _percent_box_valid,
    resolve_image_path,
    write_group_aware_splits,
)
from training.scripts.validate_annotations import validate, _load_tasks


class LabelAliasTests(unittest.TestCase):
    def setUp(self) -> None:
        cfg = load_label_aliases()
        self.aliases = cfg["aliases"]
        self.exclude = cfg["exclude"]

    def test_rectanglelabels_known(self) -> None:
        lab, status = resolve_token_label("ORG_NAME", aliases=self.aliases, exclude=self.exclude)
        self.assertEqual(lab, "ORG_NAME")
        self.assertEqual(status, "ok")

    def test_table_alias(self) -> None:
        lab, status = resolve_token_label(
            "TABLE-ACTIVITY_NO", aliases=self.aliases, exclude=self.exclude
        )
        self.assertEqual(lab, "ACTIVITY_NO")
        self.assertEqual(status, "aliased")

    def test_activity_photo_alias(self) -> None:
        lab, status = resolve_token_label(
            "ACTIVITY_PHOTO", aliases=self.aliases, exclude=self.exclude
        )
        self.assertEqual(lab, "PHOTO_DOCUMENTATION")
        self.assertEqual(status, "aliased")

    def test_unknown_not_o(self) -> None:
        lab, status = resolve_token_label("NOT_A_REAL_FIELD", aliases=self.aliases, exclude=self.exclude)
        self.assertIsNone(lab)
        self.assertEqual(status, "unknown")

    def test_from_name_label_is_unknown(self) -> None:
        """Regression: falling back to from_name='label' must NOT become a field."""
        lab, status = resolve_token_label("label", aliases=self.aliases, exclude=self.exclude)
        self.assertIsNone(lab)
        self.assertEqual(status, "unknown")


class BoxValidationTests(unittest.TestCase):
    def test_valid_box(self) -> None:
        self.assertTrue(_percent_box_valid({"x": 10, "y": 10, "width": 20, "height": 5}))

    def test_invalid_zero_size(self) -> None:
        self.assertFalse(_percent_box_valid({"x": 10, "y": 10, "width": 0, "height": 5}))


class ImageResolveTests(unittest.TestCase):
    def test_basename_resolve(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "sub").mkdir()
            target = root / "sub" / "abc-page_001.png"
            target.write_bytes(b"x")
            found = resolve_image_path("/data/upload/4/abc-page_001.png", root)
            self.assertEqual(found, target)

    def test_missing_image(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            found = resolve_image_path("/data/upload/4/nope.png", Path(td))
            self.assertIsNone(found)


class SplitLeakageTests(unittest.TestCase):
    def test_no_group_leakage(self) -> None:
        records = []
        for g in ("orgA", "orgB", "orgC", "orgD"):
            for i in range(5):
                records.append(
                    {
                        "id": f"{g}-{i}",
                        "group_id": g,
                        "source": "sf08",
                        "words": ["a"],
                        "boxes": [[0, 0, 1, 1]],
                        "word_labels": [1],
                        "doc_label": 0,
                        "image_path": "/tmp/x",
                    }
                )
        with tempfile.TemporaryDirectory() as td:
            report = write_group_aware_splits(records, Path(td), 0.5, 0.25, seed=0)
            self.assertEqual(report["potential_leakage"], [])


class ValidateJohnLloydSmoke(unittest.TestCase):
    """Smoke-test against reference LS exports (no images required)."""

    def test_john_lloyd_structure(self) -> None:
        root = Path("clean-dataset/accomplishment-report/john-lloyd")
        if not root.exists():
            root = Path("tsuorg-ml/clean-dataset/accomplishment-report/john-lloyd")
        if not root.exists():
            self.skipTest("john-lloyd reference not present")
        tasks = _load_tasks(root)
        self.assertGreater(len(tasks), 0)
        stats = validate(tasks, images_dir=None)
        self.assertEqual(stats["missing_labels"], 0)
        self.assertEqual(stats["unknown_labels"], {})  # aliases cover TABLE-* / ACTIVITY_PHOTO
        self.assertGreater(stats["valid_annotations"], 0)
        # box-only reference
        self.assertGreater(stats["box_only"], 0)


if __name__ == "__main__":
    unittest.main()
