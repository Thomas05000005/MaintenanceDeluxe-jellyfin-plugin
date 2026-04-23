#!/usr/bin/env python3
"""
Convert non-ASCII characters in a JavaScript file to \\uXXXX escapes.

Why: JavaScriptInjector serves banner.js with Content-Type
application/javascript WITHOUT a charset. Some browsers default to
Latin-1 in that case, which mangles French accents (Retour prévu →
Retour prÃ©vu). Escaping every non-ASCII char to a JS Unicode escape
sidesteps the issue: the JS parser decodes the escape at parse time
regardless of the byte-level charset.

Usage:
    python scripts/escape_non_ascii.py path/to/banner.js
    python scripts/escape_non_ascii.py --check path/to/banner.js

--check: exit 1 if any non-ASCII char is found (for CI), no rewrite.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def encode_char(ch: str) -> str:
    code = ord(ch)
    if code <= 0xFFFF:
        return f"\\u{code:04X}"
    # Surrogate pair for astral plane (emoji etc.)
    code -= 0x10000
    high = 0xD800 + (code >> 10)
    low = 0xDC00 + (code & 0x3FF)
    return f"\\u{high:04X}\\u{low:04X}"


def escape_text(text: str) -> tuple[str, int]:
    """Return (escaped_text, count_of_escapes)."""
    out: list[str] = []
    count = 0
    for ch in text:
        if ord(ch) > 127:
            out.append(encode_char(ch))
            count += 1
        else:
            out.append(ch)
    return "".join(out), count


def find_non_ascii(text: str) -> list[tuple[int, int, str]]:
    """Return list of (line_no, col_no, char) for each non-ASCII char."""
    hits: list[tuple[int, int, str]] = []
    for i, line in enumerate(text.splitlines(), 1):
        for j, ch in enumerate(line, 1):
            if ord(ch) > 127:
                hits.append((i, j, ch))
    return hits


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", help="Path to the .js file")
    parser.add_argument(
        "--check",
        action="store_true",
        help="Report non-ASCII chars and exit 1 if found, without rewriting",
    )
    args = parser.parse_args()

    p = Path(args.path)
    if not p.is_file():
        print(f"error: {p} is not a file", file=sys.stderr)
        return 2

    text = p.read_text(encoding="utf-8")

    if args.check:
        hits = find_non_ascii(text)
        if not hits:
            print(f"OK: {p} contains only ASCII.")
            return 0
        print(f"FAIL: {p} contains {len(hits)} non-ASCII char(s).")
        for line_no, col_no, ch in hits[:20]:
            print(f"  line {line_no}:{col_no}  {ch!r}  (U+{ord(ch):04X})")
        if len(hits) > 20:
            print(f"  ... and {len(hits) - 20} more")
        print(
            f"\nFix: python scripts/escape_non_ascii.py {p}",
            file=sys.stderr,
        )
        return 1

    escaped, count = escape_text(text)
    if count == 0:
        print(f"No non-ASCII chars in {p}; nothing to do.")
        return 0
    p.write_text(escaped, encoding="utf-8")
    print(f"Rewrote {p}: {count} char(s) escaped.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
