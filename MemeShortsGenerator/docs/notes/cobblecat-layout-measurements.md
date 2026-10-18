# CobbleCat Shorts format study

## Re-measured 2026-09-26 (supersedes the numbers below where they disagree)

Sample: his 16 newest Shorts plus his four biggest (rCSkTv58qtM 5.2M, nfbq3AAW048 3.3M,
rOfw6EV9HYM 2.9M, TGYLOdkhmj0 2.8M), downloaded at 1080p60, transcribed with faster-whisper and
read frame by frame. Scripts in the session scratchpad; every tracker was checked against an
overlay before a number was kept.

| | measured | how |
|---|---|---|
| pace | median 201 wpm (p10 191, p90 227), 59 words, 16.5s; first word at 0.0s; 0.03-0.4s tail | whisper, all 20 |
| picture changes | every 0.6-2.1s, median ~1.0s; a new picture on the word that names a new thing | frame diffs, all 20 |
| card width | ~700-950px, median ~800; full frame about once in nine videos | by hand, ~60 frames / 9 videos |
| card centre y | median 959, per-video sd 25-65px | edge-fill tracker, 14 videos |
| card look | white sticker (cut-out object) or media-filled; no white outline on media cards | by eye, 20 |
| corner | r ~ 0.11 x card width | edge profile |
| shadow | none on top/left; right/bottom offset +38/+36px on a 733px card, sigma ~15px, ~50% black | luminance profile |
| caption face | Comic Sans MS Bold, fitted to within 1-2px: 116px on a 733px card, 136px on 888px (~0.155 x width) | glyph-box fit vs PIL renders |
| caption stroke | ~11-12px outside the glyph (~0.095em) + a soft drop shadow | same fit |
| caption pages | 1-2 words; a short function word pairs with the next word; shouted words alone; `?`/`!` kept, commas/periods dropped | gelatin short, 46 words -> 35 pages |
| caption place | upper part of the card (~0.11 of its height below the top), or the bottom; always white | by eye |
| page / picture changes | hard cuts; some cards grow in from ~0.88 over ~6 frames; nothing pops from zero | 60fps strips |
| mascot | straddles the card's LEFT edge in front of it, 280-450px (40-50% of the card), stays across several pictures; ~40% of the time | by eye, 16 videos |
| background | heavily blurred gameplay: edge 10-90% width p25 32-37px (ours was 24px at blur 14) | edge widths |
| punchline shrink | on the final reaction line the whole frame hard-cuts to 0.70-0.85 (median ~0.79) inside black and holds to the end, 0.7-2.3s. 8/20 videos, ALL FOUR top hits | margin scan, all 20 |
| last line | a short spoken reaction ("god dangit!"-type), 1-5 words | whisper, all 20 |

What this changed in the generator: one short line per picture (10-16 beats), ONE voice take for
the whole script cut at word timings (per-line clips were costing ~50 wpm), the sticker card and
caption spec above in `ListStage.tsx`, the punchline shrink in `MemeShort.tsx`, generated
fallback pictures isolated on white so they cut out onto a white sticker, and blur 14 -> 20.

Study date: 2026-09-01. Sample: the 15 newest public Shorts on
[CobbleCat's Shorts page](https://www.youtube.com/@CobbleCatYT/shorts), reviewed with metadata,
captions, and regular frame samples. This is a production grammar for original videos, not a
license to reuse the creator's scripts, voice, footage, mascot, or branding.

## Measured format

- Runtime: 12-25 seconds; 17.1-second mean and 18-second median.
- Structure: direct oddity at 0-2 seconds, fact/evidence and escalating interpretation through
  the middle, strongest visual joke or smash-cut consequence in the final 2-4 seconds.
- Scope: one premise, one useful fact, one escalation, one ending. No greeting or spoken intro;
  no outro; only one of the 15 sampled videos used an explicit call to action.
- Foreground: a centered portrait-to-square media card over continuously blurred Minecraft
  parkour. Framing or media changes typically arrive about every 0.8-1.3 seconds. Moving clips
  dominate; stills need a slow push and reframing so the card never feels frozen.
- Captions: large rounded white type with a heavy black outline, normally only 1-3 words visible.
  A few important uppercase words can use the green accent. Captions overlap the foreground
  instead of living in a permanent banner.
- Mascot: an intermittent reaction sticker, usually entering from the left. In the five-video
  position sample it appeared for a median of roughly 27% of frames, not throughout the video.
- Ending: cut immediately after the punchline. Do not summarize, resolve, or ask for engagement.

## Content and joke grammar

The repeatable content unit is an externally legible oddity: an unusual animal behavior,
scientific image, historical object, technology detail, public post, map, name, or everyday
phenomenon. Give the minimum context needed to believe it, then reinterpret it comically.

Useful joke mechanisms:

- A true fact becomes an absurd analogy.
- Respectable source wording gets translated into blunt everyday language.
- A plausible explanation is rejected for an obviously dramatic explanation.
- An animal or object is assigned human motives.
- A short list escalates from normal to unhinged.
- A mundane inventor, institution, or object is framed as a petty villain.
- The last image turns the fact into a catastrophic consequence or meme-chain ending.

Voice should be quick, dry, incredulous, and conversational. One natural personal aside helps;
repeated catchphrases and disconnected slang do not. The joke has to grow out of the real detail.

## Generator targets

- 5-8 spoken beats and 45-70 words total.
- Target 14-20 seconds at roughly 220-240 spoken words per minute.
- Hook in 6-10 words. No "did you know," greeting, premise preamble, or CTA.
- Every beat must have a concrete foreground-media search term.
- Use only 1-3 deliberately uppercase words across the complete narration.
- Prefer portrait/square images and short video clips. Use Ken Burns motion only as the fallback.
- Use a stable per-video random seed so card framing and mascot entrances vary between uploads.

## Representative references

- [QWERTY layout](https://www.youtube.com/shorts/ZWvXhc5IFKY): fact, villain framing,
  evidence graphic, compressed final gag.
- [No Name products](https://www.youtube.com/shorts/v7cqvbSHH8M): deadpan list escalation.
- [Colored flames](https://www.youtube.com/shorts/9Q7K9H-Zxwk): true fact to catastrophic
  smash cut.
- [Mushrooms](https://www.youtube.com/shorts/U-7kW1CrRns): alien-horror escalation.
- [Mars formation](https://www.youtube.com/shorts/GHUNiN9Z1Ug): plausible explanations rejected
  for a pop-culture answer.
- [Baby owls](https://www.youtube.com/shorts/BQ1vAIKrJvc): analogy followed by an identity tag.

## Review checklist

Before approving a draft, check that the opening makes sense with sound off, every factual claim
can be verified, every visual clearly supports the line, the ending is the funniest beat, and no
borrowed script wording, unlicensed clip, cloned voice, or creator-owned brand asset slipped in.
