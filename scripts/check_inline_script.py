#!/usr/bin/env python3
"""
Extract the <script> block(s) from an HTML file and write the JS to stdout
or a target file. Used by CI to run `node --check` on configPage.html's
inline script — this would have caught v0.1.11 and v0.1.12 syntax errors
that froze the admin UI.

Usage:
    python scripts/check_inline_script.py path/to/configPage.html > extracted.js
    node --check extracted.js
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

SCRIPT_RE = re.compile(
    r"<script\b[^>]*>(.*?)</script>",
    re.DOTALL | re.IGNORECASE,
)


def extract_scripts(html: str) -> list[str]:
    return [m.group(1) for m in SCRIPT_RE.finditer(html)]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", help="HTML file to extract scripts from")
    parser.add_argument(
        "-o",
        "--output",
        help="Write extracted JS to this file (default: stdout)",
    )
    args = parser.parse_args()

    p = Path(args.path)
    if not p.is_file():
        print(f"error: {p} is not a file", file=sys.stderr)
        return 2

    html = p.read_text(encoding="utf-8")
    scripts = extract_scripts(html)
    if not scripts:
        print(f"warning: no <script> blocks found in {p}", file=sys.stderr)
        return 0

    combined = "\n/* --- next script block --- */\n".join(scripts)

    if args.output:
        Path(args.output).write_text(combined, encoding="utf-8")
        print(
            f"Extracted {len(scripts)} script block(s) from {p} -> {args.output}",
            file=sys.stderr,
        )
    else:
        sys.stdout.write(combined)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
