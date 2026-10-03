"""Replay every saved screenshot through the slicer and FAIL if anything got worse.

Every repost job keeps its source `post_image.png`, so `out/` is a free regression corpus of real
layouts in real shapes - 151 of them at the time of writing. `slice_sweep.py` prints a summary
for a human to read; this one compares against a committed baseline and exits non-zero, because
a summary nobody reads is not a check.

That distinction is not theoretical. A one-line change to how OCR rows are assigned to slices
pushed 19 of these screenshots from Info to Broken, and it was caught only because someone
happened to re-run the sweep and compare the counts by eye.

    python tools/corpus_check.py                 # check against tools/corpus_baseline.json
    python tools/corpus_check.py --write         # accept the current numbers as the new baseline
    python tools/corpus_check.py --cache plans/  # keep each plan as JSON for inspection

Needs the API running (it uses /api/repost/preflight, the same slicing code the pipeline runs).
"""
import argparse
import glob
import json
import os
import sys
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, 'src', 'MemeShorts.Api', 'out')
BASELINE = os.path.join(HERE, 'corpus_baseline.json')
API = os.environ.get('MEMESHORTS_API', 'http://localhost:5180')

# Titles carry emoji and the Windows console is cp1252 by default, which raises rather
# than printing a fallback character.
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')



def preflight(path):
    boundary = uuid.uuid4().hex
    with open(path, 'rb') as f:
        data = f.read()
    body = (
        f'--{boundary}\r\nContent-Disposition: form-data; name="image"; '
        f'filename="{os.path.basename(path)}"\r\nContent-Type: image/png\r\n\r\n'
    ).encode() + data + f'\r\n--{boundary}--\r\n'.encode()
    req = urllib.request.Request(
        f'{API}/api/repost/preflight', data=body,
        headers={'Content-Type': f'multipart/form-data; boundary={boundary}'})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)


def span(slice_, width):
    """A slice's horizontal extent, with "no bound" resolved to the card's edges."""
    return (slice_.get('xLeft') or 0, slice_.get('xRight') or width)


def drawn_twice(plan):
    """Pairs of slices covering the same rows AND the same columns, outside one comparison.

    The card STACKS its slices, so such a pair draws that content a second time and makes the
    card taller than the post. Slices inside ONE comparison are exempt: that group is drawn as a
    positioned box, where a caption lying over a picture is where the post put it.
    """
    # cropTop, not top: the reveal draws from where the slice's crop begins, and the whole
    # repeated-rows family of defects lives in the difference between the two.
    slices = [dict(s, top=s.get('cropTop', s['top']))
              for s in plan.get('slices', []) if s['bottom'] > s['top']]
    pairs = 0
    for i, a in enumerate(slices):
        for b in slices[i + 1:]:
            # Slices in one comparison are exempt only when a CAPTION lies over a picture -
            # that is where the post put it. Two PICTURES in one group drawing the same pixels
            # is never that, and exempting them blinded this check to the whole family: a
            # full-width picture claimed by one column on top of the next column's own panel
            # passed with "0 pairs" while putting the punchline on screen before it was read.
            if (a.get('group') is not None and a.get('group') == b.get('group')
                    and not (a.get('isMedia') and b.get('isMedia'))):
                continue
            if min(a['bottom'], b['bottom']) - max(a['top'], b['top']) <= 4:
                continue
            ax0, ax1 = span(a, plan['width'])
            bx0, bx1 = span(b, plan['width'])
            if min(ax1, bx1) - max(ax0, bx0) > 4:
                pairs += 1
    return pairs


def gate_rejects(plan):
    """Pictures the post-render gate would call duplicates - the same artwork drawn twice."""
    pics = [s for s in plan.get('slices', []) if s.get('isMedia')]
    n = 0
    for i, a in enumerate(pics):
        for b in pics[i + 1:]:
            if a.get('group') is not None and a.get('group') == b.get('group'):
                continue
            rows = min(a['bottom'], b['bottom']) - max(a['top'], b['top'])
            shorter = min(a['bottom'] - a['top'], b['bottom'] - b['top'])
            if shorter <= 0 or rows < shorter * 0.8:
                continue
            ax0, ax1 = span(a, plan['width'])
            bx0, bx1 = span(b, plan['width'])
            narrower = min(ax1 - ax0, bx1 - bx0)
            if narrower > 0 and min(ax1, bx1) - max(ax0, bx0) >= narrower * 0.8:
                n += 1
    return n


def measure(cache=None):
    files = sorted(glob.glob(os.path.join(OUT, '*', 'post_image.png')))
    if not files:
        sys.exit(f'No screenshots under {OUT} - nothing to check.')

    severities, per_job = {}, {}
    twice = rejects = empty = 0
    grids = []

    for i, path in enumerate(files, 1):
        job = os.path.basename(os.path.dirname(path))
        try:
            plan = preflight(path)
        except Exception as ex:                       # a screenshot that crashes the slicer IS the news
            plan = {'severity': 'ERROR', 'error': str(ex), 'slices': [], 'width': 1}
        if cache:
            os.makedirs(cache, exist_ok=True)
            with open(os.path.join(cache, job + '.json'), 'w', encoding='utf-8') as f:
                json.dump(plan, f, indent=1)

        sev = plan.get('severity', 'ERROR')
        severities[sev] = severities.get(sev, 0) + 1
        per_job[job] = sev
        twice += drawn_twice(plan)
        rejects += gate_rejects(plan)
        empty += sum(1 for s in plan.get('slices', []) if s['bottom'] <= s['top'])
        if plan.get('isDiagram') and 'comic grid' in (plan.get('columnNote') or ''):
            grids.append(job)

        if i % 25 == 0:
            print(f'  {i}/{len(files)}', flush=True)

    return {
        'screenshots': len(files),
        'severities': severities,
        'rows_drawn_twice': twice,
        'gate_rejects': rejects,
        'empty_slices': empty,
        'comic_grids': sorted(grids),
        'per_job': per_job,
    }


RANK = {'Info': 0, 'Warn': 1, 'Broken': 2, 'ERROR': 3}


def compare(now, was):
    """Every way the corpus can be worse than its baseline. Returns the failures.

    Counted only over the screenshots the baseline actually knows. The corpus grows every time a
    repost is made, so comparing raw totals reports a regression the moment a new screenshot
    warns about anything - and a check that cries wolf is a check that gets ignored.
    """
    bad = []
    known = set(was['per_job'])
    mine = {job: sev for job, sev in now['per_job'].items() if job in known}

    def tally(sevs):
        out = {}
        for sev in sevs:
            out[sev] = out.get(sev, 0) + 1
        return out

    counts = tally(mine.values())
    for level in ('Broken', 'ERROR', 'Warn'):
        if counts.get(level, 0) > was['severities'].get(level, 0):
            bad.append(f"{level}: {was['severities'].get(level, 0)} -> {counts.get(level, 0)}")

    for key, label in (('rows_drawn_twice', 'slice pairs drawing the same rows'),
                       ('gate_rejects', 'pictures the render gate would reject'),
                       ('empty_slices', 'slices with no height')):
        if now[key] > was[key]:
            bad.append(f'{label}: {was[key]} -> {now[key]} (whole corpus, including new screenshots)')

    for job, sev in sorted(mine.items()):
        before = was['per_job'][job]
        if RANK.get(sev, 3) > RANK.get(before, 0):
            bad.append(f'   {job}  {before} -> {sev}')
    return bad


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--write', action='store_true', help='accept these numbers as the baseline')
    ap.add_argument('--cache', help='directory to keep each plan in, for inspection')
    args = ap.parse_args()

    now = measure(args.cache)
    print('severities:', now['severities'])
    print('slice pairs drawing the same rows:', now['rows_drawn_twice'])
    print('pictures the render gate would reject:', now['gate_rejects'])
    print('slices with no height:', now['empty_slices'])
    print(f"comic grids claimed ({len(now['comic_grids'])}):", ' '.join(now['comic_grids']))

    if args.write:
        with open(BASELINE, 'w', encoding='utf-8') as f:
            json.dump(now, f, indent=1, sort_keys=True)
        print(f'\nBaseline written to {BASELINE}')
        return 0

    if not os.path.exists(BASELINE):
        sys.exit(f'No baseline at {BASELINE}. Run with --write once the corpus is known good.')

    with open(BASELINE, encoding='utf-8') as f:
        was = json.load(f)

    improved = [f'   {job}  {was["per_job"][job]} -> {sev}'
                for job, sev in sorted(now['per_job'].items())
                if job in was['per_job'] and was['per_job'][job] != sev
                and RANK.get(sev, 3) < RANK.get(was['per_job'][job], 0)]
    fresh = [job for job in now['per_job'] if job not in was['per_job']]
    if fresh:
        print(f"\n{len(fresh)} screenshot(s) are new since the baseline and are not compared.")
    if improved:
        print(f'\n{len(improved)} screenshot(s) improved:')
        print('\n'.join(improved))

    bad = compare(now, was)
    if bad:
        print('\nWORSE THAN THE BASELINE:')
        print('\n'.join(bad))
        print('\nFix it, or re-baseline deliberately with --write if the change is intended.')
        return 1

    print('\nNo regressions against the baseline.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
