"""Build the bundled lookup hints from Princeton's WNdb-3.0.tar.gz (pass the archive path)."""

import gzip
import json
import re
import sys
import tarfile
from pathlib import Path


def build(archive, destination):
    particles = set("about across after ahead along apart around aside at away back by down for forward from in into of off on out over past through to together under up upon with".split())
    parts, verbs, verb_phrases = {}, {}, {}
    for source, pos in [("noun", "n."), ("verb", "v."), ("adj", "adj."), ("adv", "adv.")]:
        index = archive.extractfile("dict/index." + source).read().decode("ascii").splitlines()
        data = archive.extractfile("dict/data." + source).read()
        if source == "noun":
            notice = [re.sub(r"^\s*\d+\s*", "", line).rstrip() for line in index if line.startswith(" ")]
            (destination / "WordNet-LICENSE.txt").write_text("\n".join(notice) + "\n", encoding="ascii")
        for line in index:
            if not line or line.startswith(" "):
                continue
            lemma, _, _, pointer_count, *fields = line.split()
            if lemma.isalpha():
                # The first sense offset follows the pointers, sense count and tagged sense count.
                offset = int(fields[int(pointer_count) + 2])
                gloss = data[offset:data.index(b"\n", offset)].split(b" | ", 1)[1].decode("ascii")
                definition = gloss.split('; "', 1)[0]
                example = next((s for s in re.findall(r'"([^"]+)"', gloss)
                                if len(s) <= 100 and lemma in re.findall(r"[a-z]+", s.lower())), "")
                clue = definition if len(definition) <= 180 else ""
                parts.setdefault(lemma, {})[pos] = "; ".join(filter(None, (clue, example)))
            elif source == "verb" and lemma.count("_") == 1:
                head, particle = lemma.split("_")
                if head.isalpha() and particle in particles:
                    phrase = lemma.replace("_", " ")
                    verbs.setdefault(head, []).append(phrase)
                    definitions = []
                    for sense in fields[int(pointer_count) + 2:]:
                        offset = int(sense)
                        gloss = data[offset:data.index(b"\n", offset)].split(b" | ", 1)[1].decode("ascii")
                        definitions.append(gloss.split('; "', 1)[0].strip())
                    verb_phrases[phrase] = {"Parts": {"v.": " / ".join(definitions)}, "PhrasalVerbs": []}

    words = {w for w in parts if len(parts[w]) > 1} | verbs.keys()
    entries = {w: {"Parts": parts.get(w, {}), "PhrasalVerbs": verbs.get(w, [])} for w in sorted(words)}
    entries.update(verb_phrases)
    encoded = json.dumps(entries, separators=(",", ":")).encode("ascii")
    (destination / "english-lexical-hints.json.gz").write_bytes(gzip.compress(encoded, mtime=0))


if __name__ == "__main__":
    with tarfile.open(sys.argv[1]) as archive:
        build(archive, Path(__file__).resolve().parents[1] / "src/AiTranslate.Ai/Lexicon")
