"""Release notes from git history (#129): what changed since the last release tag, grouped by the closed issues' type labels,
with upgrade notes and the decisions (ADRs) made in between.

Upgrade notes come from commit body lines starting with "Upgrade:" or "BREAKING:", plus what the diff itself shows: new database
migrations and new ADRs. Issue titles and labels come from `gh` when it is available; without it, commits are listed as they are.

Usage: python scripts/changelog.py <version> [--since <tag>] [--write]   (--write prepends the section to CHANGELOG.md)
"""
import datetime
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLOSES = re.compile(r"\b(?:closes|fixes|resolves)\s+#(\d+)", re.I)
NOTE = re.compile(r"^(upgrade|breaking):\s*(.+)$", re.I | re.M)
GROUPS = [("type:feature", "Features"), ("type:bug", "Fixes"), ("type:chore", "Maintenance")]


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True, encoding="utf-8").stdout


def last_tag() -> str | None:
    tags = [t for t in git("tag", "--list", "v*", "--sort=-v:refname").splitlines() if t]
    return tags[0] if tags else None


def issues() -> dict[int, dict]:
    if not shutil.which("gh"):
        return {}
    try:
        out = subprocess.run(["gh", "issue", "list", "--state", "all", "--limit", "1000", "--json", "number,title,labels"],
                             cwd=ROOT, check=True, capture_output=True, text=True, encoding="utf-8").stdout
        return {i["number"]: {"title": i["title"], "labels": [l["name"] for l in i["labels"]]} for i in json.loads(out)}
    except (subprocess.CalledProcessError, json.JSONDecodeError):
        return {}


def section(version: str, since: str | None) -> str:
    rng = f"{since}..HEAD" if since else "HEAD"
    raw = git("log", rng, "--no-merges", "--format=%H%x1f%s%x1f%b%x1e")
    commits = [c.strip("\n").split("\x1f") for c in raw.split("\x1e") if c.strip()]
    known = issues()
    grouped: dict[str, list[str]] = {title: [] for _, title in GROUPS}
    grouped["Other changes"] = []
    notes: list[str] = []
    for sha, subject, body in commits:
        if subject.startswith("Release v"):
            continue
        numbers = [int(n) for n in CLOSES.findall(body)]
        refs = ", ".join(f"#{n}" for n in numbers)
        line = f"- {subject}" + (f" ({refs})" if refs else "") + f" `{sha[:7]}`"
        labels = {l for n in numbers for l in known.get(n, {}).get("labels", [])}
        group = next((title for label, title in GROUPS if label in labels), "Other changes")
        grouped[group].append(line)
        notes += [f"- {m.group(2).strip()}" + (" (breaking)" if m.group(1).lower() == "breaking" else "") for m in NOTE.finditer(body)]

    added = git("diff", "--name-only", "--diff-filter=A", f"{since}..HEAD" if since else git("rev-list", "--max-parents=0", "HEAD").split()[0], "--").splitlines() \
        if since else git("ls-files").splitlines()
    migrations = sorted(Path(p).stem for p in added if "/Persistence/Migrations/" in p and p.endswith(".cs") and not p.endswith(".Designer.cs")
                        and "ModelSnapshot" not in p)
    adrs = sorted(p for p in added if p.startswith("docs/adr/") and p.endswith(".md"))
    if migrations:
        notes.append(f"- {len(migrations)} database migration(s), applied on start when `Database:MigrateOnStartup` is true (the default). "
                     "Back up the database first: migrations are not rolled back by downgrading the image.")

    date = datetime.date.today().isoformat()
    out = [f"## v{version} ({date})", ""]
    if notes:
        out += ["### Upgrade notes", "", *notes, ""]
    for title, lines in grouped.items():
        if lines:
            out += [f"### {title}", "", *lines, ""]
    if adrs:
        out += ["### Decisions", ""]
        for p in adrs:
            first = (ROOT / p).read_text(encoding="utf-8").splitlines()[0].lstrip("# ").strip()
            out.append(f"- {first} ([{Path(p).name}]({p}))")
        out.append("")
    return "\n".join(out)


def main() -> int:
    args = sys.argv[1:]
    if not args or not re.fullmatch(r"\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?", args[0]):
        print(__doc__, file=sys.stderr)
        return 2
    version = args[0]
    since = args[args.index("--since") + 1] if "--since" in args else last_tag()
    text = section(version, since)
    if "--write" in args:
        path = ROOT / "CHANGELOG.md"
        head = "# Changelog\n\nReleases of Lots. Made by `scripts/release.sh`; see docs/releasing.md.\n\n"
        old = path.read_text(encoding="utf-8").removeprefix(head) if path.exists() else ""
        path.write_text(head + text + "\n" + old, encoding="utf-8", newline="\n")
        print(f"CHANGELOG.md: added v{version}")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
