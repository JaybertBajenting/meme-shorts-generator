"""Re-render finished jobs through the current code, refusing any that would be published.

`POST /api/jobs/{id}/revoice` re-runs the pipeline; for a repost that is a full re-slice plus
re-render off the stored `repost.json` and image, which is how old videos pick up slicer fixes.

**An APPROVED job uploads to YouTube when it finishes.** Approval lives on the job, not on the
render, so a second render of an approved job goes straight out. Three videos were published that
way in one session, each time because an exclusion list written earlier had gone stale while the
Review tab was being worked through. So the state is READ HERE, per job, immediately before
queuing - never remembered.

    python tools/rerender.py                 # every job that is done and not approved
    python tools/rerender.py 5ba6e6f1 79b8   # only these (id prefixes are fine)
"""
import json
import sys
import time
import urllib.request
import os

API = os.environ.get('MEMESHORTS_API', 'http://localhost:5180')

# Titles carry emoji and the Windows console is cp1252 by default, which raises rather
# than printing a fallback character.
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

POLL_SECONDS = 10
GIVE_UP_AFTER = 180          # 30 minutes per job


def get(path):
    with urllib.request.urlopen(API + path, timeout=30) as r:
        return json.load(r)


def post(path):
    req = urllib.request.Request(API + path, data=b'', method='POST')
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def jobs_to_do(wanted):
    everything = get('/api/jobs')
    rows = everything if isinstance(everything, list) else everything.get('jobs', [])
    out = []
    for j in rows:
        jid = j.get('id') or j.get('Id') or ''
        if wanted and not any(jid.startswith(w) for w in wanted):
            continue
        out.append((jid, j.get('title') or j.get('youTubeTitle') or ''))
    return out


def main():
    wanted = [a for a in sys.argv[1:] if not a.startswith('-')]
    todo = jobs_to_do(wanted)
    if not todo:
        print('Nothing matched.')
        return 0

    for jid, title in todo:
        try:
            job = get(f'/api/jobs/{jid}')
        except Exception as ex:
            print(f'{jid}  COULD NOT READ: {ex}')
            continue

        label = f'{jid}  {title[:34]:34}'
        if job.get('state') not in ('done', 'failed'):
            print(f'{label}  skipped - {job.get("state")}')
            continue

        # The whole point of this script.
        if job.get('review') == 'approved':
            print(f'{label}  SKIPPED - approved; re-rendering would publish it to YouTube')
            continue

        try:
            post(f'/api/jobs/{jid}/revoice')
        except Exception as ex:
            print(f'{label}  COULD NOT QUEUE: {ex}')
            continue

        state = stage = error = None
        for _ in range(GIVE_UP_AFTER):
            time.sleep(POLL_SECONDS)
            try:
                d = get(f'/api/jobs/{jid}')
            except Exception:
                continue
            state, stage, error = d.get('state'), d.get('stage'), d.get('error')
            if state in ('done', 'failed'):
                break

        print(f'{label}  {state}  ({stage})' + (f'\n    {error}' if error else ''), flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
