"""Generates the values reference in charts/lots/README.md from the comments in charts/lots/values.yaml (#133).

A comment block directly above a key (no blank line between) and a comment after its value describe it. Commented-out examples
separated by a blank line are skipped. Usage: python scripts/chart-values-doc.py [--check]  (--check fails when the README is stale)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VALUES = ROOT / "charts" / "lots" / "values.yaml"
README = ROOT / "charts" / "lots" / "README.md"
BEGIN, END = "<!-- values:begin -->", "<!-- values:end -->"
PIPE = chr(92) + "|"  # an escaped pipe inside a table cell
KEY = re.compile(r"^(?P<indent> *)(?P<key>[A-Za-z0-9_.-]+):(?P<rest>.*)$")


def split_inline(rest: str) -> tuple[str, str]:
    """Value and inline comment; a # inside quotes or brackets is part of the value."""
    depth, quote = 0, None
    for i, ch in enumerate(rest):
        if quote:
            if ch == quote:
                quote = None
        elif ch in "\"'":
            quote = ch
        elif ch in "[{":
            depth += 1
        elif ch in "]}":
            depth -= 1
        elif ch == "#" and depth == 0 and (i == 0 or rest[i - 1] == " "):
            return rest[:i].strip(), rest[i + 1:].strip()
    return rest.strip(), ""


def rows() -> list[tuple[str, str, str]]:
    out, stack, pending, inside_list = [], [], [], None
    lines = VALUES.read_text(encoding="utf-8").splitlines()
    for n, line in enumerate(lines):
        stripped = line.strip()
        if not stripped:
            pending = []
            continue
        if inside_list is not None:
            if len(line) - len(line.lstrip()) > inside_list:
                continue  # the items of a list value are its default, not keys
            inside_list = None
        if stripped.startswith("#"):
            pending.append(stripped.lstrip("#").strip())
            continue
        m = KEY.match(line)
        if not m or stripped.startswith("-"):
            pending = []
            continue
        indent = len(m["indent"])
        while stack and stack[-1][0] >= indent:
            stack.pop()
        path = ".".join([k for _, k in stack] + [m["key"]])
        value, inline = split_inline(m["rest"])
        nxt = next((l for l in lines[n + 1:] if l.strip() and not l.strip().startswith("#")), "")
        has_children = not value and len(nxt) - len(nxt.lstrip()) > indent
        description = " ".join(pending + ([inline] if inline else []))
        if has_children and nxt.lstrip().startswith("-"):
            out.append((path, "(list, see values.yaml)", description))
            inside_list = indent
        elif has_children:
            if description:
                out.append((path, "", description))
            stack.append((indent, m["key"]))
        else:
            out.append((path, value or '""', description))
        pending = []
    return out


def table() -> str:
    lines = ["| Key | Default | Description |", "|---|---|---|"]
    for path, default, description in rows():
        d = (f"`{default}`" if default else "").replace("|", PIPE)
        lines.append(f"| `{path}` | {d} | {description.replace('|', PIPE)} |")
    return "\n".join(lines)


def main() -> int:
    text = README.read_text(encoding="utf-8")
    start, end = text.index(BEGIN) + len(BEGIN), text.index(END)
    new = text[:start] + "\n" + table() + "\n" + text[end:]
    if "--check" in sys.argv:
        if new != text:
            print("charts/lots/README.md is stale: run python scripts/chart-values-doc.py", file=sys.stderr)
            return 1
        return 0
    README.write_text(new, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
