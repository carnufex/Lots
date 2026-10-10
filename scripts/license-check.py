#!/usr/bin/env python
"""Lists the licenses of all NuGet (restored), npm and, with --voice-image, the voice service's Python dependencies, and fails on
anything that is not permissive/Apache-2.0-compatible. Run after `dotnet restore` and `npm ci`.

Usage: python scripts/license-check.py [--voice-image lots-voice:local] [--write docs/third-party-licenses.md]
The models the services download are listed by hand in docs/model-licenses.md (they are not packages).
"""
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))

# SPDX ids that are compatible with distributing under Apache-2.0.
ALLOWED = {
    "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD", "Unlicense", "CC0-1.0",
    "BlueOak-1.0.0", "Python-2.0", "Zlib", "PostgreSQL", "MS-PL", "CC-BY-4.0",
}
COPYLEFT = re.compile(r"\b(A?GPL|LGPL|SSPL|BUSL|EUPL|CPAL|OSL)", re.I)

# Python packages of the voice image whose license is fine to redistribute but not in ALLOWED, with the reason (#131).
PY_ACCEPTED = {
    "certifi": "MPL-2.0: file-level copyleft, shipped unmodified",
    "orjson": "MPL-2.0 AND (Apache-2.0 OR MIT): file-level copyleft, shipped unmodified",
    "tqdm": "MPL-2.0 AND MIT: file-level copyleft, shipped unmodified",
    "soxr": "LGPL-2.1-or-later: a dynamically linked library, shipped unmodified, replaceable by the user",
    "distlib": "PSF-2.0", "typing_extensions": "PSF-2.0", "regex": "Apache-2.0 AND CNRI-Python", "pillow": "MIT-CMU",
}
# Python packages without license metadata, checked by hand.
PY_MANUAL = {"setuptools": "MIT"}
# NVIDIA CUDA libraries (only in GPU builds): proprietary, redistributable as runtime components under the NVIDIA license.
PY_NVIDIA = re.compile(r"^nvidia-")

# Packages whose nuspec only has a licenseUrl / no expression, checked by hand against the project's license.
MANUAL = {
    "microsoft.netcore.platforms": "MIT",
    "npgsql": "PostgreSQL",
    "npgsql.entityframeworkcore.postgresql": "PostgreSQL",
}


def nuget_license(pkg_dir: Path) -> str:
    for nuspec in pkg_dir.glob("*.nuspec"):
        root = ET.parse(nuspec).getroot()
        ns = {"n": root.tag.split("}")[0].strip("{")}
        lic = root.find(".//n:metadata/n:license", ns)
        if lic is not None and lic.text:
            return lic.text.strip()
        url = root.find(".//n:metadata/n:licenseUrl", ns)
        if url is not None and url.text:
            u = url.text.lower()
            if "mit" in u:
                return "MIT"
            if "apache" in u:
                return "Apache-2.0"
            if "dotnet.microsoft.com/en-us/dotnet_library_license" in u or "microsoft.com/web/webpi/eula" in u:
                return "MS-EULA"
            return f"see {url.text.strip()}"
    return "UNKNOWN"


def nuget_packages() -> dict:
    found = {}
    for assets in ROOT.glob("src/**/obj/project.assets.json"):  # shipped projects only; test packages are not distributed
        data = json.loads(assets.read_text(encoding="utf-8"))
        for key, lib in data.get("libraries", {}).items():
            if lib.get("type") != "package":
                continue
            name, version = key.split("/")
            found[(name.lower(), version)] = lib.get("path", f"{name.lower()}/{version}")
    return found


def npm_packages() -> dict:
    found = {}
    lock = ROOT / "web" / "package-lock.json"
    if not lock.exists():
        return found
    for path, info in json.loads(lock.read_text(encoding="utf-8")).get("packages", {}).items():
        if not path or info.get("dev"):
            continue  # dev-only tools are not distributed
        pj = ROOT / "web" / path / "package.json"
        lic = "UNKNOWN"
        if pj.exists():
            raw = json.loads(pj.read_text(encoding="utf-8")).get("license", "UNKNOWN")
            lic = raw if isinstance(raw, str) else raw.get("type", "UNKNOWN")
        found[(path.split("node_modules/")[-1], info.get("version", "?"))] = lic
    return found


def python_packages(image: str) -> dict:
    """name -> (version, license) from the installed distributions inside the image."""
    import subprocess
    code = ("import importlib.metadata as m, json\n"
            "out = {}\n"
            "for d in m.distributions():\n"
            "    md = d.metadata\n"
            "    cls = [c.split('::')[-1].strip() for c in (md.get_all('Classifier') or []) if c.startswith('License ::')]\n"
            "    out[md['Name'].lower()] = [d.version, (md.get('License-Expression') or md.get('License') or '').strip(), cls]\n"
            "print(json.dumps(out))")
    raw = subprocess.run(["docker", "run", "--rm", "--entrypoint", "python", image, "-c", code], check=True, capture_output=True, text=True).stdout
    return {n: (v, PY_MANUAL.get(n) or normalise_python(lic, cls)) for n, (v, lic, cls) in json.loads(raw).items()}


CLASSIFIER = {
    "MIT License": "MIT", "BSD License": "BSD-3-Clause", "Apache Software License": "Apache-2.0", "ISC License (ISCL)": "ISC",
    "Mozilla Public License 2.0 (MPL 2.0)": "MPL-2.0", "Python Software Foundation License": "PSF-2.0",
    "Other/Proprietary License": "Proprietary",
}


def normalise_python(lic: str, classifiers: list[str]) -> str:
    """Package metadata is free text: map the common spellings to SPDX ids, fall back to the trove classifiers."""
    first = lic.splitlines()[0].strip() if lic else ""
    first = {"apache2.0": "Apache-2.0", "apache 2.0": "Apache-2.0", "apache-2": "Apache-2.0"}.get(first.lower(), first)
    if re.fullmatch(r"[A-Za-z0-9.+-]+( (AND|OR|WITH) [A-Za-z0-9.+-]+| \(.*\))*", first) and "License" not in first and first not in ("BSD", "Dual"):
        return first
    text = first.lower()
    for words, spdx in [("lgpl", "LGPL"), ("gpl", "GPL"), ("nvidia", "Proprietary"), ("apache", "Apache-2.0"), ("mit", "MIT"),
                        ("3-clause", "BSD-3-Clause"), ("isc", "ISC")]:
        if words in text:
            return spdx
    mapped = sorted({CLASSIFIER.get(c, c) for c in classifiers})
    return " OR ".join(mapped) if mapped else (first or "UNKNOWN")


def ok(expr: str) -> bool:
    expr = re.sub(r"\s+WITH\s+[A-Za-z0-9.-]+", "", expr)  # e.g. Apache-2.0 WITH LLVM-exception: the exception only grants more
    if COPYLEFT.search(expr):
        return False
    ids = [t for t in re.split(r"[\s()]+|\bOR\b|\bAND\b", expr) if t and t not in ("OR", "AND")]
    # "A OR B": any permissive alternative is enough. "A AND B": every term must be fine.
    if " OR " in expr:
        return any(i in ALLOWED for i in ids)
    return bool(ids) and all(i in ALLOWED for i in ids)


def main() -> int:
    rows, bad = [], []
    for (name, version), rel in sorted(nuget_packages().items()):
        lic = MANUAL.get(name) or nuget_license(NUGET / rel)
        rows.append(("NuGet", name, version, lic))
        if not ok(lic):
            bad.append(("NuGet", name, version, lic))
    for (name, version), lic in sorted(npm_packages().items()):
        rows.append(("npm", name, version, lic))
        if not ok(lic):
            bad.append(("npm", name, version, lic))
    notes = []
    if "--voice-image" in sys.argv:
        for name, (version, lic) in sorted(python_packages(sys.argv[sys.argv.index("--voice-image") + 1]).items()):
            rows.append(("pip (voice)", name, version, lic))
            if name == "lots-voice" or ok(lic):
                continue
            if name in PY_ACCEPTED:
                notes.append(f"- `{name}`: {PY_ACCEPTED[name]}")
            elif PY_NVIDIA.match(name) and lic == "Proprietary":
                notes.append(f"- `{name}`: NVIDIA proprietary, redistributable CUDA runtime component (GPU builds only)")
            else:
                bad.append(("pip (voice)", name, version, lic))

    if "--write" in sys.argv:
        out = Path(sys.argv[sys.argv.index("--write") + 1])
        lines = ["# Third-party licenses", "",
                 "Generated by `python scripts/license-check.py --write docs/third-party-licenses.md`. "
                 "Runtime dependencies of the shipped projects only (test and dev tooling are not distributed).", "",
                 "| Ecosystem | Package | Version | License |", "|---|---|---|---|"]
        lines += [f"| {e} | {n} | {v} | {l} |" for e, n, v, l in rows]
        if notes:
            lines += ["", "## Accepted exceptions", "",
                      "Not on the permissive list, but fine to redistribute in the voice image as shipped:", "", *notes]
        lines += ["", "Models downloaded at runtime (speech, voices) are listed in [model-licenses.md](model-licenses.md)."]
        out.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")

    print(f"{len(rows)} dependencies checked")
    for e, n, v, l in bad:
        print(f"NOT OK: {e} {n} {v}: {l}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
