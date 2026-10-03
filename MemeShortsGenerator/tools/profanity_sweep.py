"""Replay every saved post screenshot through the profanity filter and list what it would blur.

Same corpus and same idea as slice_sweep.py: every repost job keeps its source `post_image.png`,
so out/ is a free set of real posts in real layouts. This posts each one to
/api/repost/preflight and prints the words the blur would cover.

The point is the FALSE POSITIVES. A missed swear word is visible the moment you watch the
video; an innocent word quietly smeared over is not, because you have to be looking at that one
frame to notice. So read the output as a list of words and ask whether each one deserved it -
anything that did not goes in appsettings under Profanity:Allow.

Run it after any change to ProfanityFilter:

    (API must be running)
    python tools/profanity_sweep.py

Point it elsewhere with MEMESHORTS_API=http://localhost:5199 to avoid the live instance.
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
        print('no saved screenshots found in', OUT)
        return 1

    words = Counter()
    dirty = 0
    for i, path in enumerate(files, 1):
        job = os.path.basename(os.path.dirname(path))
        try:
            hits = preflight(path).get('profanity', [])
        except Exception as ex:
            print(f'{i:>4}/{len(files)} {job}  ERROR {ex}')
            continue

        if hits:
            dirty += 1
            found = [h['word'] for h in hits]
            words.update(w.strip('.,!?').lower() for w in found)
            print(f'{i:>4}/{len(files)} {job}  {len(hits):>2} blurred: {", ".join(found)}')

    print()
    print(f'{len(files)} screenshots, {dirty} with something to blur, '
          f'{sum(words.values())} words covered in total.')
    if words:
        print('by word:', ', '.join(f'{w} x{n}' for w, n in words.most_common()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
