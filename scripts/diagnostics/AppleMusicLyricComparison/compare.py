"""Compare the moments when the same printed word crosses half highlight in two player windows."""
import argparse
import bisect
import csv
import ctypes
import json
import re
import statistics
import unicodedata
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime
from difflib import SequenceMatcher
from functools import lru_cache
from pathlib import Path


def read_rows(path):
    with path.open(encoding="utf-8-sig") as stream:
        return [json.loads(line) for line in stream if line.strip()]


def seconds(stamp):
    return datetime.fromisoformat(stamp).timestamp()


def write_csv(path, fields, rows):
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


@lru_cache(maxsize=12000)
def tokens(text):
    text = unicodedata.normalize("NFKC", text).casefold().replace("’", "'")
    if re.search(r"[\u3400-\u9fff]", text):
        size = len(text.encode("utf-16-le")) // 2
        buffer = ctypes.create_unicode_buffer(size * 2 + 1)
        if ctypes.windll.kernel32.LCMapStringEx("zh-CN", 0x02000000, text, size, buffer, len(buffer), None, None, 0):
            text = buffer.value
    # OCR can group multiple Chinese characters into one rectangle.
    units = re.findall(r"[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af]|[^\W_\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af]+",
                       text.replace("'", ""), re.UNICODE)
    expansions = {"couldve": ("could", "have"), "wouldve": ("would", "have"), "shouldve": ("should", "have")}
    return tuple(part for unit in units for part in expansions.get(unit, (unit,)))


class Timeline:
    def __init__(self, rows):
        self.rows = sorted(rows, key=lambda row: seconds(row["started"]))
        self.times = [seconds(row["started"]) for row in self.rows]
        segment = 0
        previous = None
        for row, when in zip(self.rows, self.times):
            if previous:
                gap = when - seconds(previous["started"])
                before, after = previous.get("appleSeconds"), row.get("appleSeconds")
                state_changed = (row.get("title"), row.get("artist"), row.get("state")) != (
                    previous.get("title"), previous.get("artist"), previous.get("state"))
                jumped = before is not None and after is not None and (after < before or after - before > gap + 2)
                if state_changed or jumped or gap > 1.35:
                    segment += 1
            row["segment"] = segment
            previous = row

    def at(self, when):
        i = bisect.bisect_left(self.times, when)
        nearby = self.rows[max(0, i - 1):i + 1]
        if not nearby:
            return None
        row = min(nearby, key=lambda item: abs(seconds(item["started"]) - when))
        return row if abs(seconds(row["started"]) - when) <= .30 else None


@dataclass(frozen=True)
class Frame:
    app: str
    sample: str
    file: str
    start: float
    end: float
    track: str
    segment: int


def read_frame(picture, app, sample, timeline, images):
    if not picture or picture["File"] not in images:
        return None
    start, end = seconds(picture["Started"]), seconds(picture["Finished"])
    tag = timeline.at((start + end) / 2)
    if not tag or not tag.get("title") or tag.get("state") != "Playing" or end < start:
        return None
    return Frame(app, sample, picture["File"], start, end, tag["title"], tag["segment"])


def read_ink(image):
    cells = []
    for row in image["lines"]:
        words = row["words"]
        if not words:
            continue
        y, height = words[0]["y"], statistics.median(w["h"] for w in words)
        # The capture helper clips to the lyric pane. Its Apple header and the
        # preview rows around DropSpace's current line have different roles.
        if height < 10 or (image["app"] == "apple" and y < 78):
            continue
        if image["app"] == "drop" and (not .18 <= y / image["h"] <= .72 or height < 15):
            continue
        for word in words:
            units = tokens(word["t"])
            profile = word["profile"]
            for i, unit in enumerate(units):
                part = profile[round(len(profile) * i / len(units)):round(len(profile) * (i + 1) / len(units))]
                ink = part.count("1") + part.count("2")
                cells.append((unit, part.count("2") / ink if ink >= 2 else None))
    return cells


def locate(phrase, cells):
    values = [cell[0] for cell in cells]
    n = len(phrase)
    starts = {max(0, j - i) for i, unit in enumerate(phrase) for j, value in enumerate(values) if value == unit}
    best = (0, False, 0)
    result = {}
    tied = False
    for start in sorted(starts):
        for length in (n, n + 1, n - 1):
            if length <= 0:
                continue
            words = values[start:start + length]
            blocks = SequenceMatcher(None, phrase, words, autojunk=False).get_matching_blocks()
            agreement = sum(b.size for b in blocks) / max(n, len(words))
            if agreement < .8:
                continue
            match = {b.a + i: start + b.b + i for b in blocks for i in range(b.size)}
            score = (agreement, len(words) == n, sum(cells[j][1] or 0 for j in match.values()))
            if score > best:
                best, result, tied = score, match, False
            elif score == best and match != result:
                tied = True
    if tied:
        return {}
    return {i: cells[j][1] for i, j in result.items() if cells[j][1] is not None}


@dataclass(frozen=True)
class Crossing:
    start: float
    end: float
    file: str


def record_observations(frames, images, phrases):
    samples = defaultdict(list)
    active = {}
    for frame in sorted(frames, key=lambda f: f.start):
        cells = read_ink(images[frame.file])
        candidates = []
        for phrase in phrases.get(frame.segment, ()):
            ink = locate(phrase, cells)
            if not ink:
                continue
            samples[(frame.segment, frame.track, phrase, frame.app)].append((frame, ink))
            lit = [x for x in ink.values() if x >= .25]
            if lit:
                candidates.append(((sum(lit), len(ink) / len(phrase)), phrase, ink))
        candidates.sort(key=lambda item: item[0], reverse=True)
        if candidates and (len(candidates) == 1 or candidates[0][0] != candidates[1][0]):
            _, phrase, ink = candidates[0]
            active[(frame.sample, frame.app)] = phrase, ink
    return samples, active


def find_crossings(samples, active):
    events = defaultdict(list)
    for (segment, track, phrase, app), rows in samples.items():
        for (before, old), (after, new) in zip(rows, rows[1:]):
            if active.get((after.sample, app), ((), ()))[0] != phrase:
                continue
            for i in old.keys() & new.keys():
                if old[i] < .5 <= new[i]:
                    events[(segment, track, phrase, i, app)].append(Crossing(before.start, after.end, after.file))
    return events


def compare_events(events, timeline):
    rows = []
    for segment, track, phrase, i in sorted({key[:-1] for key in events}):
        base = dict(segment=segment, track=track, phrase=" ".join(phrase), unit=phrase[i], index=i + 1)
        key = (segment, track, phrase, i)
        apples = events.get((*key, "apple"), [])
        drops = events.get((*key, "drop"), [])
        used = set()
        def near(a, d):
            return abs((a.start + a.end - d.start - d.end) / 2) < 4
        for apple in apples:
            matches = [drop for drop in drops if near(apple, drop)]
            if len(matches) != 1 or sum(near(other, matches[0]) for other in apples) != 1:
                reason = "ambiguous-occurrence" if matches else "no-drop-crossing"
                rows.append(dict(base, appleFile=apple.file, evidence=reason))
                used.update(matches)
                continue
            drop = matches[0]
            used.add(drop)
            row = dict(base, appleFile=apple.file, dropFile=drop.file)
            if max(apple.end - apple.start, drop.end - drop.start) > 1.35:
                rows.append(dict(row, evidence="sparse-frames"))
                continue
            lower, upper = apple.start - drop.end, apple.end - drop.start
            row.update(leadMin=round(lower, 3), leadMax=round(upper, 3),
                       evidence="drop-ahead" if lower > 0 else "drop-behind" if upper < 0 else "overlapping-bounds")
            for name, when, prop in (("applePre", apple.start, "appleSeconds"), ("applePost", apple.end, "appleSeconds"),
                                     ("dropPre", drop.start, "dropSeconds"), ("dropPost", drop.end, "dropSeconds")):
                row[name] = (timeline.at(when) or {}).get(prop)
            rows.append(row)
        for drop in drops:
            if drop not in used:
                rows.append(dict(base, dropFile=drop.file, evidence="no-apple-crossing"))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("timeline", type=Path)
    parser.add_argument("pixels", type=Path)
    parser.add_argument("out", type=Path)
    args = parser.parse_args()
    pairs = read_rows(Path(str(args.timeline) + ".frames") / "pairs.jsonl")
    timeline = Timeline(read_rows(args.timeline))
    images = {image["file"]: image for image in read_rows(args.pixels)}
    refs = defaultdict(Counter)
    for row in timeline.rows:
        phrase = tokens(row.get("lyric") or "")
        if row.get("state") == "Playing" and len(phrase) >= 3:
            refs[row["segment"]][phrase] += 1
    phrases = {s: [p for p, n in counts.items() if n >= 2] for s, counts in refs.items()}
    frames = []
    for pair in pairs:
        for app in ("apple", "drop"):
            frame = read_frame(pair.get(app), app, pair["sample"], timeline, images)
            if frame:
                frames.append(frame)
    samples, active = record_observations(frames, images, phrases)
    events = compare_events(find_crossings(samples, active), timeline)
    details = []
    for pair in pairs:
        sample = pair["sample"]
        ap, aw = active.get((sample, "apple"), ((), {}))
        dp, dw = active.get((sample, "drop"), ((), {}))
        files = {app: (pair.get(app) or {}).get("File", "") for app in ("apple", "drop")}
        state = "missing-picture" if any(name not in images for name in files.values()) else (
            "no-comparable-lit-line" if not ap or not dp else "same-line" if ap == dp else "different-lines")
        detail = dict(sample=sample, appleFile=files["apple"], dropFile=files["drop"], observation=state)
        for app, phrase, ink in (("apple", ap, aw), ("drop", dp, dw)):
            detail[app] = " ".join(phrase)
            detail[app + "Half"] = " ".join(str(i + 1) for i in sorted(ink) if ink[i] >= .5)
            detail[app + "Ink"] = " ".join(f"{i + 1}={ink[i]:.2f}" for i in sorted(ink))
        details.append(detail)
    args.out.mkdir(exist_ok=True, parents=True)
    write_csv(args.out / "pairs.csv", "sample appleFile dropFile observation apple appleHalf appleInk drop dropHalf dropInk".split(), details)
    write_csv(args.out / "crossings.csv", "segment track phrase unit index appleFile dropFile applePre applePost dropPre dropPost leadMin leadMax evidence".split(), events)
    summary = {"pairs": len(pairs), "framesWithPlayback": len(frames),
               "pairCounts": dict(Counter(row["observation"] for row in details)),
               "boundaryCounts": dict(Counter(row["evidence"] for row in events))}
    (args.out / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    main()
