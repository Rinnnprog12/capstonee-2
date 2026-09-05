"""Serve SF08 PNGs for Label Studio (CORS-enabled static server)."""

from __future__ import annotations

import argparse
import os
from functools import partial
from http.server import HTTPServer, SimpleHTTPRequestHandler
from pathlib import Path


class CORSRequestHandler(SimpleHTTPRequestHandler):
    def end_headers(self) -> None:
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "*")
        super().end_headers()

    def do_OPTIONS(self) -> None:  # noqa: N802
        self.send_response(200)
        self.end_headers()


def main() -> None:
    ap = argparse.ArgumentParser(description="Serve label images for Label Studio")
    ap.add_argument("--root", type=Path, default=Path("data/raw/images"))
    ap.add_argument("--port", type=int, default=9090)
    args = ap.parse_args()

    ml_root = Path(__file__).resolve().parents[2]
    root = (ml_root / args.root).resolve()
    os.chdir(root)

    handler = partial(CORSRequestHandler, directory=str(root))
    server = HTTPServer(("127.0.0.1", args.port), handler)
    print(f"Serving {root} at http://127.0.0.1:{args.port}/")
    print("Example: http://127.0.0.1:9090/sf08/220-sf08__page-1.png")
    server.serve_forever()


if __name__ == "__main__":
    main()
