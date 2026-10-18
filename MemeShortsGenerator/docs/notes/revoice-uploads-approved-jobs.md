---
name: revoice-uploads-approved-jobs
description: Re-rendering an already-approved job publishes it to YouTube; check approval before revoice.
metadata:
  type: project
---

`POST /api/jobs/{id}/revoice` on a job that was previously APPROVED re-runs the pipeline and
uploads the result to YouTube on completion. It is not a dry re-render.

This already happened once, on 2026-09-02: re-rendering the nine flagged repost jobs published
`15c5ef7b` (HoLGSx8oJEI) and `957c950e` (WM_9XXgvsgM), because both had been approved earlier.

**Why:** the review gate publishes on completion, and approval is stored on the job, not on the
render — so a second render of an approved job goes straight out.

**How to apply:** before re-rendering anything, check each job's approval state and exclude the
approved ones by id. When fixing a batch, re-render only the jobs still awaiting review; for an
approved one, say so and let Matthew decide. Related: [[repost-slicing-safety-net]],
[[meme-shorts-generator-project]].
