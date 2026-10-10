"""Vocabulary correction: spell names and jargon the way the user wrote them.

Speech models have strong priors ("Christoffer" is far more common than "Christopher"), and prompts or hotwords do not
reliably override them. So after transcription, words that match a vocabulary word (exactly, ignoring case) get its
spelling, and words that are *close* to one (similarity above a threshold) are replaced by it. This is deterministic and
fully under the user's control: only words the user put in their list are ever substituted in.
"""
from __future__ import annotations

import re
from difflib import SequenceMatcher

# Letters (including Swedish ones), digits, apostrophes and hyphens inside a word; everything else is kept as is.
_TOKEN = re.compile(r"[\wåäöÅÄÖ'’-]+", re.UNICODE)

MIN_FUZZY_LENGTH = 5      # short words are only fixed on an exact (case-insensitive) match: too many false friends
DEFAULT_THRESHOLD = 0.8


def _phonetic(word: str) -> str:
    """A rough sound-alike form, so spellings of the same name compare equal (Christopher, Christoffer, Kristoffer)."""
    w = word.lower()
    for a, b in (("ph", "f"), ("ch", "k"), ("ck", "k"), ("c", "k"), ("w", "v"), ("y", "i"), ("z", "s"), ("q", "k"), ("x", "ks")):
        w = w.replace(a, b)
    return re.sub(r"(.)\1+", r"\1", w)  # double letters sound like single ones


def similarity(a: str, b: str) -> float:
    """The larger of the plain and the sound-alike similarity (0..1)."""
    return max(SequenceMatcher(None, a.lower(), b.lower()).ratio(), SequenceMatcher(None, _phonetic(a), _phonetic(b)).ratio())


def parse_vocabulary(prompt: str | None) -> list[str]:
    """The comma (or semicolon, newline) separated list the shell sends, without empties or duplicates."""
    if not prompt:
        return []
    seen: dict[str, str] = {}
    for part in re.split(r"[,;\n]", prompt):
        word = part.strip()
        if word and word.lower() not in seen:
            seen[word.lower()] = word
    return list(seen.values())


def apply_vocabulary(text: str, words: list[str], threshold: float = DEFAULT_THRESHOLD) -> str:
    """Returns text with vocabulary spellings applied (single-word entries; multi-word entries only match exactly)."""
    if not words or not text:
        return text

    singles = [w for w in words if _TOKEN.fullmatch(w)]
    exact = {w.lower(): w for w in words}

    def fix(match: re.Match[str]) -> str:
        token = match.group(0)
        low = token.lower()
        if low in exact:
            return exact[low]
        if len(low) < MIN_FUZZY_LENGTH:
            return token
        best, best_score = None, 0.0
        for w in singles:
            if len(w) < MIN_FUZZY_LENGTH or abs(len(w) - len(low)) > 3:
                continue
            score = similarity(low, w)
            if score > best_score:
                best, best_score = w, score
        return best if best is not None and best_score >= threshold else token

    corrected = _TOKEN.sub(fix, text)

    # Multi-word entries ("Longhorn Manager"): fix casing when they appear exactly, ignoring case.
    for w in words:
        if " " in w:
            corrected = re.sub(re.escape(w), w, corrected, flags=re.IGNORECASE)
    return corrected
