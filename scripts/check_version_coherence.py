#!/usr/bin/env python3
"""
Verify version coherence across:
- Jellyfin.Plugin.MaintenanceDeluxe/MaintenanceDeluxe.csproj   (<AssemblyVersion>)
- deploy/MaintenanceDeluxe/meta.json                           ("version")
- manifest.json                                                (versions[0].version)

Exits 1 if any of them disagrees. CI uses this as a release gate — prevents
shipping a release where the manifest advertises version X but the DLL is
version Y.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

CSPROJ = Path("Jellyfin.Plugin.MaintenanceDeluxe/MaintenanceDeluxe.csproj")
META = Path("deploy/MaintenanceDeluxe/meta.json")
MANIFEST = Path("manifest.json")

ASSEMBLY_VERSION_RE = re.compile(r"<AssemblyVersion>([^<]+)</AssemblyVersion>")


def read_csproj_version() -> str:
    text = CSPROJ.read_text(encoding="utf-8")
    m = ASSEMBLY_VERSION_RE.search(text)
    if not m:
        raise SystemExit(f"error: no <AssemblyVersion> in {CSPROJ}")
    return m.group(1).strip()


def read_meta_version() -> str:
    return json.loads(META.read_text(encoding="utf-8"))["version"].strip()


def read_manifest_top_version() -> str:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    return manifest[0]["versions"][0]["version"].strip()


def main() -> int:
    missing = [p for p in (CSPROJ, META, MANIFEST) if not p.is_file()]
    if missing:
        for p in missing:
            print(f"error: missing file {p}", file=sys.stderr)
        return 2

    csproj_v = read_csproj_version()
    meta_v = read_meta_version()
    manifest_v = read_manifest_top_version()

    print(f"csproj   : {csproj_v}")
    print(f"meta.json: {meta_v}")
    print(f"manifest : {manifest_v}")

    versions = {csproj_v, meta_v, manifest_v}
    if len(versions) == 1:
        print("OK: all three files agree.")
        return 0

    print(
        "\nFAIL: version mismatch. All three must match.\n"
        "Bump csproj (<AssemblyVersion> + <FileVersion>), update meta.json 'version',\n"
        "and prepend a new entry to manifest.json versions[0].",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
