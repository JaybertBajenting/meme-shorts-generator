"""FAIL any job whose card shows something before the line that explains it.

`corpus_check.py` replays the SLICER: it proves the crops are right, and it is blind to when
each one appears. Both defects that shipped to a finished video were timing, not geometry - a
punchline picture on screen at 0ms under four captions nobody had read yet - so that check
could not have caught either of them, and did not.

This one reads the rendered `props.json`, which is where the reveal times actually live.

The invariant: WITHIN ONE COLUMN the card accumulates top to bottom, so nothing may appear
before the content above it. Columns are independent by design - the right column waits for its
own turn while the left is read - so this never compares across them.

    python tools/reveal_order.py           # every job under out/
    python tools/reveal_order.py ed5dda37  # id prefixes are fine
"""
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(HERE), 'src', 'MemeShorts.Api', 'out')

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

# A slice revealed a beat early is a rounding artefact of mapping segments onto lines; a slice
# revealed a WHOLE line early is the defect. Measured against the shipped failures, which were
# out by 4-10 seconds.
TOLERANCE_MS = 250


def violations(post):
    bad = []
    for block in post.get('blocks') or []:
        col = block.get('column')
        for above in post.get('blocks') or []:
            if above is block or above.get('column') != col:
                continue
            if above.get('bottom', 0) > block.get('top', 0):      # not strictly above
                continue
            if block.get('startMs', 0) < above.get('startMs', 0) - TOLERANCE_MS:
                bad.append((block, above))
                break
    return bad


def label(b):
    what = '[PIC]' if not b.get('text') else b['text'][:28]
    return f"rows {b['top']}..{b['bottom']} @ {b.get('startMs')}ms  {what}"


def main():
    wanted = sys.argv[1:]
    files = sorted(glob.glob(os.path.join(OUT, '*', 'props.json')))
    checked = failed = 0
    for f in files:
        job = os.path.basename(os.path.dirname(f))
        if wanted and not any(job.startswith(w) for w in wanted):
            continue
        try:
            props = json.load(open(f, encoding='utf-8'))
        except (ValueError, OSError):
            continue
        post = props.get('post')
        if not post or not post.get('blocks'):
            continue
        checked += 1
        bad = violations(post)
        if bad:
            failed += 1
            print(f'{job}  {len(bad)} slice(s) revealed before content above them')
            for block, above in bad[:3]:
                print(f'    {label(block)}')
                print(f'      is under {label(above)}')

    print(f'\n{checked} job(s) checked, {failed} with early reveals.')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
