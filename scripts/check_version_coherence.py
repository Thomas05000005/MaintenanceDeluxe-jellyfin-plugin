#!/usr/bin/env python3
"""
Verify version coherence across every place the plugin version is written:
- Jellyfin.Plugin.MaintenanceDeluxe/MaintenanceDeluxe.csproj   (<AssemblyVersion> AND <FileVersion>)
- Jellyfin.Plugin.MaintenanceDeluxe/Configuration/admin.js     (PLUGIN_VERSION constant, sent in the export payload)
- manifest.json                                                (versions[0].version)

Exits 1 on a version mismatch, 2 on a malformed/missing source. CI uses this as a release gate —
it prevents shipping a release where the manifest advertises version X while the DLL (Assembly/File
version) or the admin export payload (PLUGIN_VERSION) still says version Y. v0.8.6: extended from an
AssemblyVersion-vs-manifest check to also cover <FileVersion> (A#39) and admin.js PLUGIN_VERSION
(A#41), which `make bump` now rewrites in lockstep. The deploy/MaintenanceDeluxe/meta.json file is
derived from manifest.json at release time (see release.yml) so it is not checked here.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

CSPROJ = Path("Jellyfin.Plugin.MaintenanceDeluxe/MaintenanceDeluxe.csproj")
ADMIN_JS = Path("Jellyfin.Plugin.MaintenanceDeluxe/Configuration/admin.js")
MANIFEST = Path("manifest.json")

ASSEMBLY_VERSION_RE = re.compile(r"<AssemblyVersion>([^<]+)</AssemblyVersion>")
FILE_VERSION_RE = re.compile(r"<FileVersion>([^<]+)</FileVersion>")
PLUGIN_VERSION_RE = re.compile(r"PLUGIN_VERSION\s*=\s*'([^']+)'")


def _read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except OSError as e:
        print(f"FAIL: cannot read {path}: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(2)


def _extract(pattern: "re.Pattern[str]", text: str, what: str, path: Path) -> str:
    m = pattern.search(text)
    if not m:
        print(f"FAIL: no {what} found in {path}", file=sys.stderr)
        sys.exit(2)
    return m.group(1).strip()


def read_manifest_top_version() -> str:
    # Guard every step: an empty/truncated manifest.json (e.g. mid-release write) used to crash
    # with a cryptic JSONDecodeError or IndexError. Surface a clear "malformed manifest" instead.
    try:
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        return manifest[0]["versions"][0]["version"].strip()
    except (json.JSONDecodeError, KeyError, IndexError, TypeError, AttributeError, OSError) as e:
        print(f"FAIL: malformed manifest.json: {type(e).__name__}: {e}", file=sys.stderr)
        sys.exit(2)


def main() -> int:
    missing = [p for p in (CSPROJ, ADMIN_JS, MANIFEST) if not p.is_file()]
    if missing:
        for p in missing:
            print(f"error: missing file {p}", file=sys.stderr)
        return 2

    csproj_text = _read(CSPROJ)
    sources = {
        "csproj <AssemblyVersion>": _extract(ASSEMBLY_VERSION_RE, csproj_text, "<AssemblyVersion>", CSPROJ),
        "csproj <FileVersion>": _extract(FILE_VERSION_RE, csproj_text, "<FileVersion>", CSPROJ),
        "admin.js PLUGIN_VERSION": _extract(PLUGIN_VERSION_RE, _read(ADMIN_JS), "PLUGIN_VERSION", ADMIN_JS),
        "manifest versions[0].version": read_manifest_top_version(),
    }

    width = max(len(name) for name in sources)
    for name, val in sources.items():
        print(f"{name.ljust(width)} : {val}")

    if len(set(sources.values())) == 1:
        print("OK: all version sources agree.")
        return 0

    print("\nFAIL: version mismatch across sources.", file=sys.stderr)
    print(
        "Bump ALL of: csproj <AssemblyVersion> + <FileVersion>, admin.js PLUGIN_VERSION, and\n"
        "prepend a matching entry to manifest.json versions[0] (or run `make bump V=X.Y.Z`).",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
