"""Causal, delayed section decisions on the original 16 kHz audio clock.

Text timestamps are arrival positions, NOT word alignments. Punctuation-only
cuts are explicitly estimates. All comparisons share the same observations.
"""
from dataclasses import dataclass, field
import pysbd

SR = 16000


@dataclass
class Options:
    language: str = "de"
    pause: float = 0.5
    wait: float = 1.2
    maximum: float = 20.0
    legacy: float = 0.9
    text_delay: float = 0.48
    minimum: float = 1.0
    signals: tuple = ("vad", "punct")
    combine: str = "and"

    def __post_init__(self):
        for name, lo, hi in [("pause", .1, 3), ("wait", 0, 5),
                             ("maximum", 3, 60), ("legacy", .1, 5),
                             ("text_delay", 0, 3), ("minimum", 0, 5)]:
            value = float(getattr(self, name))
            if not lo <= value <= hi:
                raise ValueError(f"Invalid parameter: {name}")
            setattr(self, name, value)
        if not self.signals or not set(self.signals) <= {"vad", "punct", "legacy", "length"}:
            raise ValueError("Select at least one valid trigger")
        if self.combine not in ("and", "or"):
            raise ValueError("Invalid combination")


@dataclass
class Lane:
    signals: tuple
    combine: str
    start: int = 0
    pending: dict | None = None
    count: int = 0
    used: dict = field(default_factory=dict)


class Segmenter:
    def __init__(self, options):
        self.o = options
        self.now = 0
        self.speech = False
        self.speech_seen = False
        self.silence_start = None
        self.pause_reported = False
        self.candidates = []
        self.text = ""
        self.last_text = None
        self.serial = 0
        self.last_tick = 0
        self.sbd = pysbd.Segmenter(language=options.language, clean=False, char_span=True)
        self.sbd_text = None
        self.sentence_ends = set()
        self.lanes = {s: Lane((s,), "or") for s in options.signals}
        if len(options.signals) > 1:
            self.lanes["combined"] = Lane(options.signals, options.combine)

    def candidate(self, source, cut, **extra):
        self.serial += 1
        c = dict(id=self.serial, source=source, cut=max(0, int(cut)),
                 observed=self.now, **extra)
        self.candidates.append(c)
        return dict(type="candidate", **c)

    def audio(self, end_sample, probability):
        self.now = end_sample
        out = []
        if probability >= .5:
            if not self.speech:
                out.append(dict(type="speech", active=True, at=self.now))
            self.speech = self.speech_seen = True
            self.silence_start = None
            self.pause_reported = False
        elif probability < .35:
            if self.speech:
                self.speech = False
                self.silence_start = max(0, self.now - 512)
                out.append(dict(type="speech", active=False, at=self.silence_start))
            if (self.silence_start is not None and not self.pause_reported
                    and self.now - self.silence_start >= self.o.pause * SR):
                # Place the cut inside observed silence, retaining trailing audio.
                out.append(self.candidate("vad", self.silence_start + min(.2, self.o.pause / 2) * SR,
                                          speech_end=self.silence_start, estimated=False))
                self.pause_reported = True
        out += self.tick()
        return out

    def delta(self, text):
        self.text += text
        self.last_text = self.now
        out = []
        # This is an intentionally visible punctuation heuristic, not semantic NLP.
        for i, ch in enumerate(text):
            if ch in ".!?":
                out.append(self.candidate("punct", self.now - self.o.text_delay * SR,
                                          estimated=True, excerpt=self.text[-160:],
                                          text_end=len(self.text)-len(text)+i+1))
        return out + self.tick()

    def valid_sentence_end(self, candidate, eof=False):
        """Recheck with text received during lookahead, preserving original offsets."""
        end = candidate.get("text_end")
        if end is None:  # Observations imported from older runs without char offsets.
            return True
        if self.sbd_text != self.text:
            self.sbd_text = self.text
            self.sentence_ends = set()
            for span in self.sbd.segment(self.text):
                tail = span.sent.rstrip().rstrip('\"\u201d\u201c\u00bb\u00ab\u2019\')]}').rstrip()
                if tail and tail[-1] in ".!?":
                    self.sentence_ends.add(span.start + len(tail))
        # A trailing ordinal/decimal needs its continuation, even if wait elapsed.
        if (not eof and end >= 2 and self.text[end-1] == "."
                and self.text[end-2].isdigit() and not self.text[end:].strip()):
            return False
        return end in self.sentence_ends

    def tick(self):
        out = []
        if self.last_text is not None and self.now - self.last_text >= self.o.legacy * SR:
            out.append(self.candidate("legacy", self.last_text, estimated=True))
            self.last_text = None
        for name, lane in self.lanes.items():
            relevant = [c for c in self.candidates if c["source"] in lane.signals
                        and c["id"] > lane.used.get(c["source"], 0)
                        and c["cut"] >= lane.start + self.o.minimum * SR]
            # A duration limit is an independent fallback, even with AND selected.
            if lane.pending is None:
                selected = None
                acoustic = [c for c in relevant if c["source"] == "vad"]
                nonlength = set(lane.signals) - {"length"}
                for c in relevant:
                    nearby = [x for x in relevant if abs(x["cut"] - c["cut"]) <= 1.5 * SR]
                    if lane.combine == "and" and not nonlength <= {x["source"] for x in nearby}:
                        continue
                    match = nearby if lane.combine == "and" else [c]
                    cut_source = next((x for x in match if x["source"] == "vad"), c)
                    selected = dict(cut=cut_source["cut"], observed=max(x["observed"] for x in match),
                                    sources=sorted({x["source"] for x in match}),
                                    estimated=cut_source["estimated"], matches=match,
                                    text_end=next((x["text_end"] for x in match if "text_end" in x), None))
                    break
                if (selected is None and "length" in lane.signals and self.speech_seen
                        and self.now - lane.start >= self.o.maximum * SR):
                    # Prefer a recent observed pause; otherwise mark a forced cut.
                    recent = [c for c in acoustic if self.now - c["cut"] <= 5 * SR]
                    c = recent[-1] if recent else None
                    selected = dict(cut=c["cut"] if c else self.now, observed=self.now,
                                    sources=["length"] + (["vad"] if c else []),
                                    estimated=c is None, matches=[c] if c else [])
                if selected:
                    lane.pending = selected
                    out.append(dict(type="pending", lane=name, **{k:v for k,v in selected.items() if k != "matches"}))
            p = lane.pending
            if p and self.now >= p["observed"] + self.o.wait * SR:
                invalid = [c for c in p.get("matches", [])
                           if c["source"] == "punct" and not self.valid_sentence_end(c)]
                if invalid:
                    lane.pending = None
                    for c in invalid:
                        lane.used["punct"] = max(lane.used.get("punct", 0), c["id"])
                    out.append(dict(type="cancelled", lane=name, cut=p["cut"],
                                    reason="No sentence ending confirmed by following text"))
                else:
                    out.append(self.commit(name, p))
        # Keep bounded history while retaining enough for delayed associations.
        self.candidates = [c for c in self.candidates if self.now - c["observed"] < 70 * SR]
        return out

    def commit(self, name, p):
        lane = self.lanes[name]
        cut = min(self.now, max(lane.start, p["cut"]))
        lane.count += 1
        event = dict(type="segment", lane=name, number=lane.count, start=lane.start,
                     end=cut, decided=self.now, lag_s=(self.now-cut)/SR,
                     sources=p["sources"], estimated=p["estimated"], text_end=p.get("text_end"))
        lane.start = cut
        lane.pending = None
        for c in p.get("matches", []):
            lane.used[c["source"]] = max(lane.used.get(c["source"], 0), c["id"])
        return event

    def finish(self, total_samples):
        self.now = total_samples
        out = []
        for name, lane in self.lanes.items():
            if lane.pending and lane.pending["cut"] > lane.start:
                if all(c["source"] != "punct" or self.valid_sentence_end(c, eof=True)
                       for c in lane.pending.get("matches", [])):
                    out.append(self.commit(name, lane.pending))
                else:
                    lane.pending = None
                    out.append(dict(type="cancelled", lane=name, reason="Kein Satzende"))
            if total_samples > lane.start:
                out.append(self.commit(name, dict(cut=total_samples, sources=["stop"], estimated=False)))
        return out
