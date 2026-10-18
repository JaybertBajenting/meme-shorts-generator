---
name: blobbyclips-study
description: What @BlobbyClips actually is — a curation channel repackaging other animators' shorts, not an animation studio
metadata:
  type: reference
---

Matthew asked (2026-08-20) how @BlobbyClips makes his animated shorts and whether we could do
the same. Measured answer: **he does not animate anything.**

Evidence, not impression:
- **12 of 12 sampled videos credit a different animator** in the description ("Animated By
  TheAnonymousGhoul / DishIsh / MKMaffo / Zac / zBlanck / Lyferie / Bavid0 / Areg / Outter /
  Inácio / Kukomitzu / Cartoon"). 11 distinct creators.
- **Three top videos = three completely different art styles** (detailed line-art dragons,
  round-headed stick figures, cute blob particles). One artist does not switch like that.
- **Audio is the original creators' voice acting** — multiple character voices, dialogue and
  SFX ("Hoorah!", "AHHH!"), not a single narrator reading a script. He uses the clips whole.

His format is OUR format with different filling: heavily blurred Minecraft parkour behind a
roughly square card, centred, ~70-75% of frame width, sitting near the vertical middle;
karaoke captions INSIDE the card; a full-card title frame at the start ("HOW HUMANS SEE
DRAGONS"). Pace 162-215 wpm, videos 14-24s. Views 500k-12M.

**Technically trivial for us**: `ListStage.tsx` already draws the card; swapping `<Img>` for
`<OffthreadVideo>` is the only change, and `OffthreadVideo` is already used in
`Background.tsx`, so Remotion 4.0.482 supports it out of the box.

**The real constraint is not engineering, it is rights.** The whole product is other people's
animation. Crediting is consistent with a permission arrangement but is not itself permission.
Doing this needs animators to actually agree first — an outreach job, not a code job.
