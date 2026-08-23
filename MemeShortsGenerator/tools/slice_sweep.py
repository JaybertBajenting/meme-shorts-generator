"""Replay every saved post screenshot through the live slicer and report broken plans.

Every repost job keeps its source `post_image.png`, so the out/ folder is a free regression
corpus of real screenshots in real layouts. This posts each one to /api/repost/preflight —
the same slicing code the pipeline runs — and summarises what the validator says.

Run it after ANY change to PostSliceService / DiagramSliceService:

    (API must be running)
    python tools/slice_sweep.py

Baseline as of the run that introduced it: 103 screenshots, 102 Info, 1 Broken (a picture
with genuinely no readable text in it).
"""
import glob, json, os, sys, urllib.request, uuid
from collections import Counter

OUT = os.path.join(os.path.dirname(__file__), '..', 'src', 'MemeShorts.Api', 'out')
API = os.environ.get('MEMESHORTS_API', 'http://localhost:5180')


def preflight(path):
    b = uuid.uuid4().hex
    with open(path, 'rb') as f:
        data = f.read()
    body = (f'--{b}\r\nContent-Disposition: form-data; name="image"; '
            f'filename="{os.path.basename(path)}"\r\nContent-Type: image/png\r\n\r\n').encode() \
        + data + f'\r\n--{b}--\r\n'.encode()
    req = urllib.request.Request(f'{API}/api/repost/preflight', data=body,
                                 headers={'Content-Type': f'multipart/form-data; boundary={b}'})
    with urllib.request.urlopen(req, timeout=180) as r:
        return json.load(r)


def main():
    files = sorted(glob.glob(os.path.join(OUT, '*', 'post_image.png')))
    if not files:
        print('no saved screenshots found in', OUT); return 1

    rows = []
    for i, f in enumerate(files):
        job = os.path.basename(os.path.dirname(f))
        try:
            r = preflight(f)
            rows.append((job, r['severity'], [(x['code'], x['message']) for x in r['issues']], len(r['spoken'])))
        except Exception as e:
            rows.append((job, 'ERROR', [('request-failed', str(e)[:120])], 0))
        if (i + 1) % 20 == 0:
            print(f'  ...{i+1}/{len(files)}', flush=True)

    print(f'\n=== {len(rows)} screenshots ===')
    for k, v in Counter(r[1] for r in rows).most_common():
        print(f'  {k:8} {v}')
    codes = Counter(c for r in rows for c, _ in r[2])
    if codes:
        print('\n=== issues ===')
        for k, v in codes.most_common():
            print(f'  {k:22} {v}')
    bad = [r for r in rows if r[1] in ('Broken', 'ERROR')]
    if bad:
        print('\n=== broken ===')
        for job, sev, issues, lines in bad:
            print(f'  {job}  lines={lines}  {issues[0][1][:95]}')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
