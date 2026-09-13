"""Boundary integrity and causal lookahead, independent of ASR wording."""
import unittest
from segments import Options, Segmenter, SR


class Boundaries(unittest.TestCase):
    def test_ordinal_is_rechecked_after_continuation(self):
        s = Segmenter(Options(signals=("punct",), wait=1.2))
        s.now = 2*SR
        s.delta("Zutritt ab dem vollendeten 16.")
        s.now = int(2.5*SR)
        s.delta(" Lebensjahr gestattet.")
        s.now = 4*SR
        events = s.tick()
        self.assertFalse(any(e["type"] == "segment" for e in events))
        events = s.tick()
        self.assertEqual(len([e for e in events if e["type"] == "segment"]), 1)
        self.assertEqual(s.lanes["punct"].count, 1)

    def test_abbreviations_and_decimal_are_not_sentences(self):
        s = Segmenter(Options(signals=("punct",)))
        s.text = "Dr. Müller zahlt z. B. 3.50 Euro. Danach geht er."
        accepted = [i+1 for i,c in enumerate(s.text) if c == "."
                    and s.valid_sentence_end({"text_end": i+1})]
        self.assertEqual(accepted, [s.text.index("Euro.")+5, len(s.text)])

    def test_delayed_acoustic_boundary_not_decision_time(self):
        s = Segmenter(Options(signals=("vad",), wait=1, pause=.5))
        events = []
        for n in range(512, SR*4, 512):
            events += s.audio(n, .9 if n < SR*2 else .01)
        cuts = [e for e in events if e["type"] == "segment"]
        self.assertEqual(len(cuts), 1)
        self.assertGreater(cuts[0]["lag_s"], 1)
        self.assertLess(cuts[0]["end"], 2.3*SR)
        self.assertGreater(cuts[0]["decided"], 3.4*SR)

    def test_and_waits_for_late_punctuation(self):
        s = Segmenter(Options(wait=.3))
        s.now = 2*SR
        s.candidate("vad", 1.8*SR, estimated=False)
        s.tick()
        self.assertIsNone(s.lanes["combined"].pending)
        s.now = int(2.2*SR)
        s.delta(" beendet.")
        self.assertIsNotNone(s.lanes["combined"].pending)
        s.now = 3*SR
        events = s.tick()
        e = next(e for e in events if e.get("lane") == "combined")
        self.assertEqual(e["end"], int(1.8*SR))
        self.assertFalse(e["estimated"])

    def test_no_punctuation_is_not_and_confirmation(self):
        s = Segmenter(Options(wait=0))
        s.now = 2*SR
        s.candidate("vad", 1.8*SR, estimated=False)
        s.delta("weil ich noch")
        s.now = 6*SR
        s.tick()
        self.assertEqual(s.lanes["combined"].count, 0)

    def test_finish_keeps_all_samples_each_lane(self):
        s = Segmenter(Options(signals=("vad", "punct", "legacy", "length"), wait=.2, maximum=3))
        events = []
        for n in range(512, SR*8, 512):
            events += s.audio(n, .9 if n % (SR*3) < SR*2 else .01)
            if n % (SR*3) < 512:
                events += s.delta(" Satz.")
        events += s.finish(8*SR+71)
        for lane in s.lanes:
            cuts = [e for e in events if e["type"] == "segment" and e["lane"] == lane]
            self.assertEqual(cuts[0]["start"], 0)
            self.assertEqual(cuts[-1]["end"], 8*SR+71)
            self.assertTrue(all(a["end"] == b["start"] for a,b in zip(cuts,cuts[1:])))
            self.assertTrue(all(e["decided"] >= e["end"] for e in cuts))


if __name__ == "__main__":
    unittest.main()
