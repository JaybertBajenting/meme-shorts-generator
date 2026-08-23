"""
Word-level transcription for caption timing.

Usage:
    python whisper_words.py <model> <device> <compute> <wav1> [wav2 ...]

Prints a JSON array to stdout:
    [ { "file": "<path>", "words": [ {"text": "...", "start": 0.12, "end": 0.41}, ... ] }, ... ]

Times are seconds, relative to the start of each wav. Loads the model once for all files.
"""
import sys
import json


def main() -> int:
    if len(sys.argv) < 5:
        print("usage: whisper_words.py <model> <device> <compute> <wav...>", file=sys.stderr)
        return 2

    model_name, device, compute = sys.argv[1], sys.argv[2], sys.argv[3]
    files = sys.argv[4:]

    # Imported here so a missing dependency yields a clean non-zero exit (caller falls back).
    from faster_whisper import WhisperModel

    model = WhisperModel(model_name, device=device, compute_type=compute)

    out = []
    for f in files:
        segments, _info = model.transcribe(f, word_timestamps=True, language="en", vad_filter=False)
        words = []
        for seg in segments:
            for w in (seg.words or []):
                words.append({"text": w.word.strip(), "start": float(w.start), "end": float(w.end)})
        out.append({"file": f, "words": words})

    sys.stdout.write(json.dumps(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
