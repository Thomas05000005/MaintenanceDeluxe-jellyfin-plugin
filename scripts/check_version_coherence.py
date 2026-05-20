#!/usr/bin/env python3
"""
Verify version coherence across:
- Jellyfin.Plugin.MaintenanceDeluxe/MaintenanceDeluxe.csproj   (<AssemblyVersion>)
- manifest.json                                                (versions[0].version)

Exits 1 if they disagree. CI uses this as a release gate — prevents
shipping a release where the manifest advertises version X but the DLL is
version Y. The deploy/MaintenanceDeluxe/meta.json file is derived from
manifest.json at release time (see release.yml) so it is not checked here.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

CSPROJ = Path("Jellyfin.Plugin.MaintenanceDeluxe/MaintenanceDeluxe.csproj")
MANIFEST = Path("manifest.json")

ASSEMBLY_VERSION_RE = re.compile(r"<AssemblyVersion>([^<]+)</AssemblyVersion>")


def read_csproj_version() -> str:
    try:
        text = CSPROJ.read_text(encoding="utf-8")
    except OSError as e:
        print(f"FAIL: cannot read {CSPROJ}: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(2)
    m = ASSEMBLY_VERSION_RE.search(text)
    if not m:
        print(f"FAIL: no <AssemblyVersion> in {CSPROJ}", file=sys.stderr)
        sys.exit(2)
    return m.group(1).strip()


def read_manifest_top_version() -> str:
    # Guard every step: an empty/truncated manifest.json (e.g. mid-release-please write)
    # used to crash with a cryptic JSONDecodeError or IndexError. Now we surface a clear
    # failure with exit 2 so CI shows "malformed manifest" instead of a stack trace.
    try:
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        return manifest[0]["versions"][0]["version"].strip()
    except (json.JSONDecodeError, KeyError, IndexError, TypeError, AttributeError, OSError) as e:
        print(f"FAIL: malformed manifest.json: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(2)


def main() -> int:
    missing = [p for p in (CSPROJ, MANIFEST) if not p.is_file()]
    if missing:
        for p in missing:
            print(f"error: missing file {p}", file=sys.stderr)
        return 2

    csproj_v = read_csproj_version()
    manifest_v = read_manifest_top_version()

    print(f"csproj  : {csproj_v}")
    print(f"manifest: {manifest_v}")

    if csproj_v == manifest_v:
        print("OK: csproj and manifest agree.")
        return 0

    print(
        "\nFAIL: version mismatch.\n"
        "Bump csproj (<AssemblyVersion> + <FileVersion>) and prepend a\n"
        "matching entry to manifest.json versions[0].",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
