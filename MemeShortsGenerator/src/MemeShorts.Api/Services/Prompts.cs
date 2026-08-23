using MemeShorts.Api.Models;

namespace MemeShorts.Api.Services;

/// <summary>
/// The two prompt templates that lock the reference-video style. Codex only
/// varies topic + beats; everything visual is deterministic in the renderer.
/// </summary>
public static class Prompts
{
    public const string RageFaceList =
        "\"fffuuu\",\"mother_of_god\",\"are_you_kidding_me\",\"determined\",\"cry\",\"poker\",\"happy\",\"yao_ming\",\"scream\"";

    public static string Storyboard(string topic, string? format, IReadOnlyList<string>? styleTitles = null)
    {
        var fmt = string.IsNullOrWhiteSpace(format) ? "(you choose the best fit)" : format;

        var titleStyleBlock = "";
        if (styleTitles is { Count: > 0 })
        {
            // Capped at 40, NOT 400: the prompt is passed to the Codex CLI as a command-line
            // argument, and Windows kills the process with Win32Exception 206 ("filename or
            // extension too long") once the whole command exceeds ~32k characters. 400 reference
            // titles blew that budget on their own. 40 is plenty to convey a titling style.
            var examples = string.Join("\n", styleTitles.Take(40).Select(t => "  - " + t));
            titleStyleBlock = $$"""

TITLE STYLE — `youtube_title` must match the tone/format of these REAL titles from the
target channel (short, Title Case, curiosity/relatable hooks; do NOT copy them verbatim):
{{examples}}

LEARNED TITLE PATTERNS from those examples:
- Start with a concrete familiar subject, then add the twist: object/person/action first,
  joke angle second. Avoid abstract premises that need explaining.
- Strong shapes: "How ___", "Why ___", "This Is Why ___", "Which ___ Would You Pick?",
  "What ___ Looks Like", "Secret Behind ___", "Evolution of ___", "___ Explained",
  "Real World vs ___", "Most/Goofiest/Coolest/Worst/Funniest ___".
- Titles usually feel like a viewer challenge, tiny mystery, fake explainer, comparison,
  superlative, or weirdly specific everyday moment.
- Keep it 4-8 words when possible. Use Title Case. Use punctuation sparingly.
- End `youtube_title` with exactly one relevant reaction emoji: 😂, 🤣, 💀, 🤔, or 😭.
  Put the emoji at the very end. Do not put `#Shorts` or any hashtag in the title.
- Do NOT copy a listed title or simply swap one noun. Make a new title with the same structure.
""";
        }

        return $$"""
You are a meme scriptwriter for vertical (9:16) short-form "rage comic" memes
in the classic MS-Paint / TikTok brainrot style.

Output ONLY a single valid JSON object. No markdown fences, no commentary.

THE SCRIPT MUST MAKE SENSE. Read this before anything else.
Every video is ONE coherent idea, not a pile of weird images. It needs a spine:
  1. Beat 1 poses a real QUESTION or makes a CLAIM about the thing ("why does X do Y?",
     "X is better than Z", "here is what X actually is").
  2. The middle beats are EVIDENCE or STEPS that genuinely work toward the answer — each one
     follows from the last. Not a fresh unrelated image every line.
  3. The last beat DELIVERS the answer, the twist, or the real reason. The viewer should end up
     knowing or realising something they did not at the start.
The comedy lives in HOW this is described — the exaggeration, the specific detail, the tone.
It must NEVER live in whether the sentence makes sense. A line that cannot be pictured, or that
means nothing when read literally, is a FAILURE no matter how funny the words sound.
BANNED: surreal filler that scans as a joke but states nothing (a body part "in 2029", an object
"losing trust", someone "forgetting knees"). If you cannot say what a line actually MEANS, cut it.
TEST BEFORE ANSWERING: say the premise in one plain sentence, and say what the last beat reveals.
If either is impossible, the script is incoherent — rewrite it.

COMEDY — but only on top of that spine. Match this creator's voice:
- Treat a MUNDANE or ABSURD thing as deadly serious. Escalate the STAKES and the language, never
  the incoherence — every beat must still describe something real and picturable.
- Deadpan, SELF-AWARE narrator that comments on its own video — rhetorical "we all do this" asides,
  mock-scheming about causing chaos, second-guessing whether something will work. Use YOUR OWN
  wording every time; never reuse a stock catchphrase.
- SPECIFICITY is the joke: use exact, weird, concrete details — but INVENT them FRESH from THIS
  video's topic (a specific object, number, or moment the topic implies). Never generic, never recycled.
- Hyperbole (over-the-top exaggeration of tiny stakes).
- ESCALATE INSIDE THE TOPIC'S OWN WORLD. The exaggeration must be built from how the actual
  object behaves — its parts, materials, mechanism, physics, or the room it lives in. Do NOT
  reach for an unrelated authority metaphor to manufacture drama.
  **BANNED ESCALATION DOMAIN — courtroom / legal / government / military.** Measured across past
  scripts, over a quarter of them escalated this way and the phrase "martial law" showed up
  repeatedly. Do not use: martial law, courtroom, trial, verdict, evidence, testify, guilty,
  lawsuit, sue, illegal, crime, treason, felony, committee, government, federal, official policy,
  jurisdiction, territories, citizen, war, army, grenade, bomb, weapon, siege, or the pattern of
  something "declaring", "filing", "reporting", "approving" or "banning" anything.
  These are a rut, not a joke. Find the absurdity in the THING, not in imaginary bureaucracy.
- The last beat ANSWERS the question the first beat raised. It must resolve the video, not add
  one more weird image. A one-word non-sequitur ending ("Soup." / "Peel." / "Cricket.") is
  FORBIDDEN — it reads as a script that gave up, and it is the single clearest sign of a
  machine-written joke. The ending can be short, but it must MEAN something in context.
- fake_comment is the punchline: a viewer who OVER-relates with an unhinged confession, calls out the
  stupidity, or reacts in confusion. Make it land.
- On-screen captions are short and blunt. The SPOKEN line is conversational and complete —
  walking the viewer through a small discovery is the format, not filler to cut.

DO NOT COPY ANY EXAMPLE WORDING — this is critical. Every quoted phrase, catchphrase, object, number
or line anywhere in this prompt is a STYLE ILLUSTRATION ONLY, never content to reuse. These keep
recurring and are BANNED verbatim: "who hasn't done this", "mother of-", "this is worse", "one dorito",
"YES", "BUTT?", "have you been watching me". Also don't default to the example objects (microwave,
giraffe, socks, fridge light, "20th century fox"). Write your OWN captions, punchlines, and specifics
— all grounded in the topic below.

SOUND LIKE A PERSON, NOT AN AI. This is judged as harshly as the comedy.
An AI-written script gives itself away by being too TIDY. Real speech is lopsided and messy.
BANNED — these are the tells:
- Every line the same length with the same rhythm. Real people mix a 3-word fragment with a
  rambling 18-word sentence. Vary line length HARD, on purpose.
- Every line built the same way (all starting with the same word, all "X does Y like Z").
  If two lines share an opening word or a shared simile shape, rewrite one.
- Three-item lists ("it's loud, wet, and confusing"). Humans say one thing, or four.
- "Not just X, but Y" / "it's not X, it's Y" — pure AI cadence.
- A neat concluding sentence that sums up the video. Real people just stop, or trail off.
- Explaining the joke after making it. Say the funny thing and move on.
- Perfect grammar everywhere. Use contractions (it's, doesn't, I'm). Start a line with "and"
  or "but". Let one line be a fragment.
REQUIRED: at least one line noticeably shorter than the rest, and at least one that runs long
and slightly out of breath. If every line could be swapped with another without noticing, it is
too uniform — rewrite it.

MEASURED TICS — these were counted across past scripts and must be actively avoided:
- The word "once" appeared in 41% of all scripts. Do not open or pivot a line with it.
- "mother" (from the banned "mother of-") still appeared in 14%. Drop it entirely.
- Legal/bureaucratic escalation appeared in 28% — see the BANNED ESCALATION DOMAIN above.
Before answering, reread your script and replace any word that feels like a house style rather
than a choice you made for THIS topic. Two different videos should not share a signature phrase.

PICK the FORMAT that fits the topic (set `format` to the slug). These describe STRUCTURE ONLY —
you write all the actual words yourself, fresh for the topic:
- "single_visual_reveal": one comic, diagram, image, or scene progressively reveals its joke.
- "relatable_chaos": a mundane setup escalates beat-by-beat into a full-blown crisis, peaks on a
  panicked reaction, then lands a one-word payoff plus an over-relating viewer comment.
- "relatable_list": a rapid-fire list of oddly-specific universal behaviors, capped by a viewer who
  over-relates. (The "[subject] be like" opener fits here.)
- "absurd_debate": draw a symmetric object/animal and pose a dumb TWO-option debate about it with
  numbered choices, treat it as deeply serious, a commenter pushes back, then escalate to an even
  dumber version. Object drawings, not faces.
- "chart_gag": DISABLED — do NOT choose this format. The native chart renderer does not yet match
  the reference channel's look and is switched off. If a topic feels chart-shaped, cover it with
  a different format instead (usually "single_visual_reveal").
- "escalating_corrections": a wrong answer gets corrected harder and harder, each fix more incredulous
  than the last as something obvious keeps getting overlooked.
- "internal_monologue": narrate the anxious unspoken rules of a mundane moment as if the stakes were
  enormous.
- "character_dialogue": TWO characters talk to each other and the conversation itself is the joke —
  usually an origin story for why some everyday thing is the way it is. Captions are their spoken
  lines, STRICTLY ALTERNATING speaker A, B, A, B... starting with A. Give them ordinary names and
  let them talk like real people (greetings, filler, interruptions). One physical action or reveal
  near the end delivers the punchline. `drawing` (beat 1 only) must show BOTH characters together,
  side by side, with no speech bubbles drawn in the image — the bubbles are added by the renderer.
  Every caption is a line of dialogue, so write them as speech, not as labels.

Style rules:
- The video is a sequence of "beats". Each beat = one white panel, one short `caption` (the
  ON-SCREEN label, max ~8 words), and one `tts` line (what is SPOKEN).
- `caption` and `tts` are NOT the same text. This is the single most important script rule.
  * `caption` = the short label printed on the panel. Terse. Label-like.
  * `tts` = a COMPLETE, NATURAL SPOKEN SENTENCE. It may be noticeably longer than the caption
    and must include the connective words real speech has (so, but, because, well, actually,
    and then, okay, oh). It should sound like a person talking, never like a headline.
  Writing telegraphic fragments in `tts` is the most common failure — it produces narration
  that sounds like someone reading bullet points aloud instead of telling you something.

NARRATION VOICE — copy how the reference channel actually TALKS:
- There is a PERSON narrating with a stake in it. Use first person and real reactions, or let
  two named characters talk to each other. Never a detached label-reader.
- Every video tells ONE tiny story with an arc: set something up, complicate it, then reveal
  the twist. A list of separate jokes with no connection between beats is the failure mode.
- Open by grounding the viewer in something they already know or something happening to you
  right now, THEN break it. The reversal only lands if the setup was ordinary.
- Explaining is GOOD. The biggest reference videos walk the viewer through a small discovery —
  the "wait, actually..." turn is the joke. Do not strip explanation out for brevity.
- Let the delivery carry emotion: surprise, indignation, panic, smugness, resignation. Flat
  declarative lines read as lifeless no matter how clever the wording is.
- The final beat is the payoff and is spoken aloud — a reaction, a verdict, or the fake comment
  read out. It must resolve the story, not just add one more observation.
- Write `tts` in NORMAL SENTENCE CASE. Do NOT write spoken lines in ALL CAPS: the voice engine
  reads caps as sustained shouting, and a whole video delivered at one volume is exhausting and
  sounds nothing like the reference. Emotion belongs in the `emotion` field and in word choice,
  not in capitalisation. (`caption` MAY be caps — it is a printed label, not speech.)
- The hook must land in beat 1. Beat 1 must work as the first frame by itself:
  instant premise, one big visual, no slow setup, no vague teasing.
- Use 3-6 beats total. WORD BUDGET, count it before you answer: aim for about 8-12 spoken words
  per beat, so 4 beats x ~10 words = ~40 spoken words for the WHOLE video. **75 spoken words
  total is a HARD LIMIT and the storyboard is rejected above it.** (Measured across 12 reference
  videos: 27-74 spoken words, mean ~40, mean length 12.1s.)
  Long enough to sound like a real sentence, short enough that the video never drags. Spend the
  budget on ONE story told properly, not on more panels.
  Never stretch a premise merely to create more panels. A beat may contain at most 22 spoken
  words — enough for a real sentence, not enough for a paragraph.
- The narration READS THE ON-SCREEN TEXT. On the reference channel the spoken line and the panel
  label are the same words — the voice is reading the artifact aloud, not commenting on it from
  outside. Do not write narration that describes a panel the viewer can already see.
- Build the entire short around ONE concrete visual artifact: one comic, chart, comparison, list,
  diagram, fake sign, product, photo setup, or single scene. Do not invent unrelated panels.
- ONLY BEAT 1's `drawing` IS RENDERED. It becomes the single piece of artwork shown for the whole
  video, with each later beat's caption appearing as a line of text beneath it. So beat 1's
  `drawing` must carry the entire premise on its own. Later beats still need a `drawing` field
  (describe the same artifact), but it is not drawn again — put the progression in the CAPTIONS.
CAPTIONS ARE THE PANEL'S VISIBLE CONTENT — write them as a LIST, not as narration fragments.
They stack up under the artwork and ALL stay on screen together to the end, so the finished panel
must read as one coherent labelled list, top to bottom.
- Each caption must be a complete, meaningful statement about the subject. 3-7 words, no verb-less
  noun stubs. A reader seeing only the panel, with no audio, must understand the whole point.
- Read your captions back as a plain list before answering. If two lines next to each other do
  not connect, or a line means nothing on its own, rewrite it.
- BANNED: disconnected label-fragments strung together (a real failure: "USB LOGIC / BACK TO
  START / TINY PLASTIC TONGUE" — three stubs that say nothing as a list), and joke-noun endings
  like "NOZZLE YOGA" that resolve nothing.
- The LAST caption must land the answer, in plain words. It is the line the viewer is left
  staring at, so it carries the whole video.
- The `tts` for each beat is the spoken version of that same line — conversational and longer,
  but saying the SAME thing. Caption and tts must never contradict.
- Beat 1 states the premise immediately. Middle beats add specific information. The final beat
  reveals the funniest contradiction, reclassification, consequence, or absurd detail.
- Write only what the visual needs. No greeting, intro, outro, moral, recap, engagement request,
  "let me explain", or commentary about making a video.
- Use a recognizable, concrete observation. Random surreal events are not a substitute for a joke.
- `drawing` must repeat enough of the SAME visual anchor in every beat for independent image
  generation to preserve continuity, then describe exactly what NEW detail is revealed.

PACKAGING RULES:
- `youtube_title` must be short, punchy, curiosity-driven, and not too literal.
  Avoid "Experience" unless that exact word is the funniest possible wording.
  Prefer title energy like: "Which Mascot Wins?", "The Secret Behind Snack Bags",
  "Why Loading Takes Forever", "Real Life Vs Game Logic", "How Bad Ideas Evolve".
- End `youtube_title` with exactly one relevant emoji from 😂, 🤣, 💀, 🤔, or 😭.
  The emoji is the final character. Never include `#Shorts` or another hashtag in the title.
- `thumbnail_text` is REQUIRED in practice: 2-4 words, ALL CAPS, readable at tiny
  grid size, and directly tied to the joke. No full sentences.
- Every `drawing` should name ONE clear focal object/scene/action. When useful,
  include one or two bold accent colors for the important object, arrow, warning
  mark, label, circle, or funniest part.

MIX THE VISUALS:
- Every beat must set `visual_style` to exactly one of: `photo`, `graphic`, or `doodle`.
- DEFAULT TO `photo` OR `graphic`. The reference channel's biggest videos are built from
  real-looking product photography, clean labelled diagrams, charts, and flowcharts — NOT
  hand-drawn art. Crude drawings consistently look cheap next to that.
- `photo`: a real-looking object, food, animal, product, or small scene, shot on white.
  Use this whenever the premise is about a physical thing. Never ask for a real photograph,
  brand logo, packaging design, celebrity, creator, or copyrighted character.
- `graphic`: clean flat vector art, stock-style clipart, a labelled diagram, a flowchart, a
  chart with axes, a comparison layout, a sign, or a meter. Use this for anything explanatory.
- `doodle`: crude MS-Paint/rage-comic art. Use SPARINGLY — at most one beat per video, and only
  when the crudeness itself is the joke. Never make a whole video out of doodles.
- Choose ONE visual style for the core artifact and keep it consistent across every reveal.
- The first beat must choose the clearest, biggest style for the hook. `drawing` still
  describes the literal subject no matter which style is chosen.

PANEL ART — THIS IS THE MOST IMPORTANT RULE:
- Each panel must SHOW THE LITERAL THING the caption is about — the object, the scene,
  or the action. Put that in `drawing`, described plainly so it can be rendered in ANY
  visual_style. Examples: "a microwave showing 0:05", "a shoe with an ankle sock slipping
  off", "two parents asleep in a bed", "a hand reaching for a fridge".
  Describe WHAT is shown, never HOW it is drawn — `visual_style` decides that.
- `rage_face` is a REACTION, used VERY SPARINGLY — at most ONE beat per video, only at a real
  emotional peak, and only on a `doodle` beat. Pick from: [{{RageFaceList}}].
  The reference channel's top videos contain NO rage faces at all; prefer null.
- A standalone face with NO drawing should be RARE (the single biggest climax beat at most).
- Do NOT make most beats just a face. The reference style shows the subject every beat;
  faces are the exception, not the default. If unsure, write a `drawing` and leave rage_face null.

- `fake_comment` should normally be null. Only use it when a viewer reaction is genuinely
  the visual's final reveal; do not force a second punchline after the payoff.
- For "absurd_debate" beats use the `options` array for the two numbered choices.

VOICE EMOTION — the narrator must PERFORM, not flatly read. Give EVERY beat an `emotion`: one
  short delivery word the voice acts out. Match it to the beat and make it SWING for comedy —
  build tension then explode. Use words like: deadpan, excited, nervous, whispering, panicked,
  shouting, screaming, annoyed, smug, sarcastic, confused, hesitant, mock-serious, manic, defeated.
  e.g. a quiet setup = "whispering", the build = "nervous", the climax ("MOTHER OF-") = "screaming",
  the over-it ending = "deadpan". Do NOT put the emotion in the caption or tts text — only the field.

Topic: "{{topic}}"
MANDATORY MEME VOICE: set every beat's `emotion` to exactly "shouts". The narrator is loudly,
urgently shouting from the first phrase through the final phrase; never become calm, deadpan,
quiet, whispering, defeated, or conversational on later beats.
Preferred format: {{fmt}}
{{titleStyleBlock}}
Also produce:
- `youtube_title`: a click-worthy title (<= ~70 chars) in the style above (or punchy/relatable if no examples). Short, curious, not a literal summary.
- `thumbnail_text`: a required 2-4 word ALL-CAPS hook for the thumbnail (e.g. "WAIT WHAT?", "EVERY TIME"). It must be readable and specific to this joke.

Return JSON matching this schema exactly:
{
  "title": string,
  "youtube_title": string,
  "thumbnail_text": string,
  "format": "single_visual_reveal" | "relatable_chaos" | "relatable_list" | "absurd_debate" | "chart_gag" | "escalating_corrections" | "internal_monologue" | "character_dialogue",
  "beats": [
    { "caption": string, "tts": string, "emotion": string, "rage_face": string|null,
      "drawing": string|null, "visual_style": "doodle"|"photo"|"graphic",
      "options": [string,string]|null, "duration_s": number,
      "visual": null }
  ],
  "fake_comment": { "username": string, "text": string } | null
}

WHEN format IS "chart_gag", EVERY BEAT MUST INSTEAD SUPPLY `visual` AND SET `drawing` TO null:
  "visual": { "type": "chart",
              "chart": { "kind": "line"|"bar", "unit": string|null,
                         "points": [{"label": string, "value": number}, ...] } }
The chart is drawn natively, so the AXIS LABELS (the `label` of each point) MUST BE IDENTICAL in
every beat — only the VALUES and the beat's caption change. That is the whole joke: one unchanging
chart frame, re-labelled and re-plotted to mean something new each time (the reference channel's
"lines of life" video plots one x-axis of life stages against a different quantity per beat).
Use 5-7 points, same labels throughout, and make each beat's curve shape genuinely different.
""";
    }

    public static string FinanceStoryboard(string topic, IReadOnlyList<string>? styleTitles = null)
    {
        var titleStyleBlock = "";
        if (styleTitles is { Count: > 0 })
        {
            var examples = string.Join("\n", styleTitles.Take(25).Select(t => "  - " + t));
            titleStyleBlock = $$"""

TITLE STYLE — `youtube_title` should match the tone of these real titles from the target channel:
{{examples}}
""";
        }

        return $$"""
You are a scriptwriter for vertical (9:16) finance/money short-form videos. The visuals are
rendered as clean motion graphics INSIDE a white card (no photos, no meme faces). Each beat
has a caption, a TTS line (== caption), and ONE data-driven `visual`.

Output ONLY a single valid JSON object. No markdown, no commentary.

NUMBERS — be rigorous (this is the most important rule):
- Pick ONE scenario for the whole video and keep EVERY number consistent with it.
- Do the math correctly. If you show contributions, growth, and a total, then
  contributions + growth = total (exactly). A chart's parts must sum to its total.
- Use realistic assumptions (e.g. 7-10% annual return) and state them. Round sensibly.
- A "stat" reveal and any "chart"/"comparison" in the same video must agree with each other.
- Double-check the arithmetic before output. Do NOT invent figures that contradict each other.

Rules:
- 4-8 beats. EDUCATIONAL: teach ONE clear money concept well. Each beat builds logically on the
  previous; the viewer should learn something concrete and end with an actionable takeaway.
- INTRO (beat 1) = get straight to the point and make the TITLE's premise clear, so a viewer who
  didn't read the title still knows exactly what this video is about. Tie beat 1 to `youtube_title`:
  if the title is a question, answer/pose it directly; if it's a claim, state it plainly.
  e.g. title "Why Broke People Stay Broke?" -> beat 1 tts: "Broke people stay broke because of one
  habit: they spend first and save what's left." No vague teasing, no "let me explain" filler.
- VARY THE VISUALS. Each beat's `visual.type` is one of: "stat", "chart", "comparison", "tip".
  Do NOT use the same type twice in a row, and use at least 3 DIFFERENT types across the video.
  Lean on charts — use 2+ chart beats with DIFFERENT `chart.kind` each time.
- `chart.kind` options (pick the one that fits the data, and vary them):
  "bar" (compare amounts), "line" (growth over time), "donut"/"pie" (share of a whole),
  "progress" (how something splits into %), "ranking" (top items, high to low).
- PACK EACH VISUAL WITH DATA — never a single bare number. Fill the card:
  * stat: big number + a `delta` badge (e.g. "+8%/yr", "20% saved") + a `breakdown` of 2-3
    {label,value} rows that add up to the number. e.g. to:"$620,000", breakdown: contributions/growth.
  * chart: 3-5 `points` with real values; parts should sum to any total you mention.
  * comparison: 3-4 short items per side AND a one-line `result` takeaway banner. ORDER MATTERS:
    `left`/`leftTitle` = the WORSE / "don't do this" option (renders RED); `right`/`rightTitle`
    = the BETTER / recommended option (renders GREEN). Each item is a short fact (not a checkmark).
  * tip: a headline `text`, an `icon` emoji, AND 2-3 short supporting `points`.
- Prefer the data-rich types (stat-with-breakdown, chart, comparison). Use at most one bare "tip".
- Captions are short (<= ~8 words). The TTS may be a touch longer/spoken-friendly.
- End with one fake_comment (a plausible viewer reaction).

Topic: "{{topic}}"
{{titleStyleBlock}}
Also produce:
- `youtube_title`: a click-worthy finance title (<= ~70 chars).
- `thumbnail_text`: a 2-4 word ALL-CAPS hook (e.g. "DO THIS NOW", "$1 vs $1M").

Return JSON matching this schema exactly:
{
  "title": string,
  "youtube_title": string,
  "thumbnail_text": string,
  "format": "finance",
  "beats": [
    { "caption": string, "tts": string, "duration_s": number,
      "visual": {
        "type": "stat"|"chart"|"comparison"|"tip",
        "stat": { "from": string|null, "to": string, "label": string, "delta": string|null, "breakdown": [{"label": string, "value": number}] } | null,
        "chart": { "kind": "bar"|"line"|"pie"|"donut"|"progress"|"ranking", "unit": string|null, "points": [{"label": string, "value": number}] } | null,
        "comparison": { "leftTitle": string, "left": [string], "rightTitle": string, "right": [string], "result": string|null } | null,
        "tip": { "text": string, "icon": string, "points": [string] } | null
      }
    }
  ],
  "fake_comment": { "username": string, "text": string }
}
""";
    }

    /// <summary>
    /// Ask Codex to brainstorm N fresh video topics for hands-off batch generation.
    /// Output is a single JSON object {"topics":[...]} so it parses with FirstJsonObject.
    /// </summary>
    /// <summary>
    /// Title a reposted post. The post text is the ONLY subject matter — the job here is
    /// packaging, not invention, so the prompt leans hard on the reference channel's real
    /// titles and forbids spoiling the punchline (the whole video is the reveal).
    /// </summary>
    /// <summary>
    /// Invent a LIST SHORT: a run of lines, each with the picture that should be on screen.
    ///
    /// The reference titles are there to aim at SUBJECTS that do well in this niche — word
    /// oddities, puns, trivia about everyday objects. It is told plainly to write a new video
    /// rather than a version of any listed one, because the interesting thing about those
    /// titles is the subject matter, not the specific joke.
    /// </summary>
    /// <summary>
    /// One plain picture of a thing, for when Wikimedia has nothing usable. It has to sit on
    /// the same card as real photographs, so it asks for a photograph rather than an
    /// illustration — a cartoon among photos looks like a mistake.
    /// </summary>
    public static string StockImage(string subject) => $$"""
Use image_gen to draw ONE image: a clean, realistic photograph of {{subject}}.

STYLE — it has to sit alongside real photographs without looking out of place:
- A believable PHOTOGRAPH: natural light, real materials, real depth of field.
- NOT an illustration, NOT a cartoon, NOT a diagram, NOT a 3D render, NOT clip art.
- The subject fills most of the frame and is unmistakably the thing named above.
- Plain uncluttered surroundings. Nothing else competing for attention.

REQUIREMENTS:
- Landscape, roughly 1536x1024.
- NO text, NO labels, NO captions, NO watermark, NO logo, NO signature anywhere.
- No recognisable real person's face, no brand marks, no copyrighted character.
- Do not reproduce any existing photograph; invent this one.

Save the PNG and tell me the exact path you saved it to.
""";

    public static string ListIdea(IReadOnlyList<string> winningTitles, string? seedTopic,
        IReadOnlyList<string>? avoid = null, string? steer = null)
    {
        // Same 40-title cap as the other prompts: this goes to the CLI as a command-line
        // argument and Windows kills the process past ~32k characters.
        var examples = string.Join("\n", winningTitles.Take(40).Select(t => "  - " + t));
        var seed = string.IsNullOrWhiteSpace(seedTopic)
            ? "Pick the subject yourself."
            : $"The subject must be: {seedTopic.Trim()}";

        // Without these two blocks the prompt is byte-identical on every call, so Codex returns
        // the same idea forever - pressing "discard" then "suggest" gave the same video back.
        var alreadyDone = avoid is { Count: > 0 }
            ? "\n\nALREADY MADE OR ALREADY REJECTED - do not suggest any of these subjects again,\nand do not suggest a near-variation of one:\n"
              + string.Join("\n", avoid.Take(40).Select(a => "  - " + a))
            : "";

        var nudge = string.IsNullOrWhiteSpace(steer)
            ? ""
            : $"\n\nFor THIS one, take the subject from around here: {steer}. "
              + "Only a starting point - go wherever in that area is most surprising.";

        return $$"""
Write a short narrated video: a run of 5 to 7 spoken lines, each with ONE picture on screen
while it is read.

These are recent titles from a channel in this niche. They show WHAT KIND OF SUBJECT does well
here — language oddities, puns, surprising trivia about ordinary objects, geography and
internet nostalgia. Do NOT rewrite, retell or adapt any of these specific videos; write a
brand-new one on a subject of the same kind:
{{examples}}

{{seed}}{{alreadyDone}}{{nudge}}

Rules measured off four of this channel's own videos, not guessed at:

LENGTH. His two biggest hits (3.2M views each) are 7.4 and 8.8 SECONDS — 26 and 32 words
TOTAL, across the whole video. His 21-second explainers did far worse (2.5M and 654k).
Write 4 to 5 lines totalling **30 to 45 words**. Count them before you answer. If you are
over 45, cut a line — do not shorten every line to squeeze one more in.

PACE. Measured 186-219 words a minute, mean 204. Write lines that are said fast.

OPENING. The first sentence IS the hook — there is no preamble, no "have you ever wondered".
His real openings: "British Coins are cursed!", "Louisiana is apparently the drunkest state.",
"Did you know that Canada, the US, and Mexico all come together to form a special acronym?"

VOICE. He is a person reacting, not a narrator. EVERY one of his scripts has a personal aside
in it: "I don't know what that's about", "I don't know if I can say that", "God, I love
English", "End video!". Put at least one in.

THE ENDING. The payoff lands on the PICTURE, and the narration often refuses to finish the
joke — "They call it co— hahaha I don't know if I can say that", "put them together and it
makes a HUH!". Do not explain the punchline in words when the last picture can carry it.

PICTURES — and this constrains the SUBJECT, not just the wording.

The pictures come from Wikimedia Commons, which is an archive of PHOTOGRAPHS and scans. It is
good at physical things that exist in the world: coins, animals, buildings, machines, food,
maps, flags, vehicles, tools, places. It is bad at abstractions, typography, wordplay and
concepts — a script about the history of the ampersand returned a Malayalam alphabet chart
and a photograph of a Victorian murder suspect, because no plain photograph of that idea
exists.

So: pick a subject where EVERY line can be pointed at a physical object or place. Before you
answer, check each line — if the best picture you can name for it is a diagram of an idea, a
letterform, or a screenshot of text, change the subject.

Each image field is plain search words for one thing: "two pence coin obverse" works, "the
concept of irony" does not. Two or three words beat five — a long phrase matches nothing and
the search has to fall back.

NEVER use a person's name as an image search term, not even a historical one. Searching
"Robert McCulloch" returned a press photo of a DIFFERENT, living man of the same name, which
would have put an uninvolved person in the video. Search the thing they made or the place
instead.

TRUTH. Everything stated must be TRUE. This format's appeal is that the facts check out.

Return ONLY this JSON, no prose:
{"title": "...", "items": [{"text": "spoken line", "image": "plain search words"}]}

TITLE. Measured across 300 of his videos (median 830k views), and his top 30 differ from his
bottom 30 in specific ways:
- Median title is 6 words / 30 characters. Keep it SHORT.
- 83% of his top titles put ONE or TWO words in ALL CAPS. Not the whole title.
- Only 3% of his top titles end in a question mark — 10% of his worst do. Avoid "??".
- A trailing "..." is common in the top set.
- 67% carry a single emoji, almost always 😭 or 🤣, at the end.
- Crucially, the winning titles TEASE and withhold the subject rather than stating the fact:
  "Internet Explorer's being kinda quiet... 😭", "THE 4TH WALL WILL NEVER BE SAFE 😭",
  "The Zombie Apocalypse ain't so bad...", "Firefox's DARK FUTURE...". A title that gives
  away the payoff removes the reason to watch.
""";
    }

    public static string RepostTitle(string postText, IReadOnlyList<string>? styleTitles = null)
    {
        var styleBlock = "";
        if (styleTitles is { Count: > 0 })
        {
            // Same 40-title cap as TopicBrainstorm: the prompt goes to the Codex CLI as a
            // command-line argument, and Windows kills the process past ~32k characters.
            var examples = string.Join("\n", styleTitles.Take(40).Select(t => "  - " + t));
            styleBlock = $$"""

REAL recent titles from the target channel. Match this voice, length and energy:
{{examples}}
""";
        }

        return $$"""
You are titling a YouTube Short. The video shows this social post being read out loud,
one line at a time:

---
{{(postText.Length > 1200 ? postText[..1200] + "…" : postText)}}
---
{{styleBlock}}

RULES:
- 3 to 8 words. Short enough to read at a glance on a phone.
- Title Case.
- Make someone want to watch. Curiosity, not a summary.
- DO NOT give away the punchline or the final number — the video IS the reveal.
- No hashtags, no quote marks, no emoji (those get added later), no clickbait-caps.

Reply with ONLY this JSON and nothing else:
{"title": "..."}
""";
    }

    public static string TopicBrainstorm(int count, string contentType, IReadOnlyList<string>? styleTitles = null,
        IReadOnlyList<string>? avoidTopics = null, PerformanceSnapshot? performance = null)
    {
        var isFinance = contentType.Equals("finance", StringComparison.OrdinalIgnoreCase);

        var styleBlock = "";
        if (styleTitles is { Count: > 0 })
        {
            // Capped at 40, NOT 400: the prompt is passed to the Codex CLI as a command-line
            // argument, and Windows kills the process with Win32Exception 206 ("filename or
            // extension too long") once the whole command exceeds ~32k characters. 400 reference
            // titles blew that budget on their own. 40 is plenty to convey a titling style.
            var examples = string.Join("\n", styleTitles.Take(40).Select(t => "  - " + t));
            styleBlock = $$"""

These are REAL recent titles from the target channel — match this vibe/topic space, but
come up with NEW ideas (do NOT reuse or lightly reword these):
{{examples}}

LEARNED TOPIC/TITLE SHAPES from those examples:
- Concrete object/situation first: animal, food, product, chart, daily action, body/hand
  thing, school/work/home moment, game-vs-real-life comparison, mascot, note, sign, phone.
- Then one simple twist: challenge, fake explanation, secret, evolution, choice, danger
  scale, cursed chart, "vs", or "what it looks like".
- Reliable formats: "How ___", "Why ___", "This Is Why ___", "Which ___ Would You Pick?",
  "What ___ Looks Like", "Secret Behind ___", "Evolution of ___", "___ Explained",
  "Real World vs ___", "Most/Goofiest/Coolest/Worst/Funniest ___".
- Avoid repeating the same main object with different endings in one batch.
""";
        }

        // REAL view counts from this channel. This outranks every stylistic rule below it —
        // those are educated guesses, this is measured evidence about this exact audience.
        var performanceBlock = "";
        if (performance is { HasSignal: true })
        {
            var winners = string.Join("\n", performance.Winners
                .Select(w => $"  - {w.Views:N0} views ({w.Multiple:0.#}x the channel average): {w.Label}"));

            var flopBlock = performance.Flops.Count == 0 ? "" : $$"""


THESE FLOPPED on this same channel — the subject was invented/abstract with nothing the viewer
has personally experienced. Do NOT generate topics that work like these:
{{string.Join("\n", performance.Flops.Select(f => $"  - {f.Views:N0} views: {f.Label}"))}}
""";

            performanceBlock = $$"""


=== REAL PERFORMANCE DATA (highest-priority instruction) ===
These are ACTUAL view counts from this channel. Where this section conflicts with any stylistic
guidance below, THIS WINS — it is measured, the rest is theory.

THESE BROKE OUT — typical video gets ~{{performance.MedianViews:N0}} views, these vastly beat it:
{{winners}}{{flopBlock}}

WHAT THE WINNERS HAVE IN COMMON — apply this to every topic you generate:
- The subject is a REAL thing the viewer has personally been annoyed by or fought with —
  a physical object, a machine, a game, a household system. Never an invented abstraction.
- There is a gap between how the thing SHOULD behave and how it actually behaves. That gap
  IS the joke. The viewer should feel "this happened to me" before any joke lands.
- The subject is instantly nameable in 1-2 words (wifi, magnets, a maze, self-checkout).
- Mundane and specific beats clever and abstract. A wordplay/geometry gag that sounds smart
  reliably underperforms a plain frustrating object on this channel.
STRONGEST TEST: would a stranger scrolling past think "ugh, YES, that thing"? If they'd instead
think "huh, that's a weird idea" — cut it, no matter how clever it is.

The winners above are already published. Reuse their UNDERLYING PATTERN — a familiar thing that
misbehaves — on completely different subjects. Do NOT reuse, reword, or narrowly vary any of
those exact topics.
""";
        }

        var avoidBlock = "";
        if (avoidTopics is { Count: > 0 })
        {
            // Also capped for the command-line length budget — see the style-titles note above.
            var made = string.Join("\n", avoidTopics.Take(30).Select(t => "  - " + t));
            avoidBlock = $$"""

ALREADY MADE — these videos already exist. Do NOT repeat, rephrase, or make anything that
overlaps with these; pick genuinely DIFFERENT subjects:
{{made}}
""";
        }

        var flavor = isFinance
            ? "short-form finance/money explainer videos that teach ONE concrete money concept (saving, investing, compound growth, budgeting, debt, common mistakes)."
            : "MS-Paint meme shorts with a deadpan narrator about FAMILIAR things behaving wrongly — the humour comes from a shared experience the viewer instantly recognizes, not from inventing a clever abstraction.";

        // Good/bad are described by SHAPE, not by quotable phrases: any sample line put in this
        // prompt gets copied verbatim into real scripts (learned the hard way).
        var goodBad = isFinance
            ? "Good: \"why your savings never grow\", \"$100/mo becomes $1M\". Bad: vague categories (\"money tips\")."
            : "GOOD = a specific real object/machine/game/household system that misbehaves in a way "
              + "the viewer has personally suffered; nameable in one or two words; the premise lands in under a second. "
              + "BAD = an invented or abstract subject (impossible shapes, personified moods, concepts doing paperwork), "
              + "a wordplay or geometry puzzle, or anything whose humour needs explaining. Clever-but-unfamiliar "
              + "is the single most reliable way to underperform on this channel. Plain, mundane and universally "
              + "annoying beats witty and original every time. Everyday frustrations are ENCOURAGED — they are this "
              + "channel's best-performing category — provided the topic names the specific thing that misbehaves.";

        // Ordered by measured performance: the relatable-frustration bucket produces this
        // channel's breakouts, and the abstract/wordplay bucket has never produced one.
        var memeRange = isFinance ? "" : """

SUBJECT PRIORITY (roughly this mix per batch):
- MOST of the batch: a familiar object, machine, appliance, game, or household system that
  behaves wrongly, unfairly, or illogically in a way nearly everyone has run into.
- SOME: the same kind of familiar subject framed as a comparison, a fake explainer, a secret,
  a ranking, or a chart/diagram/meter — the frame is decoration, the familiar subject carries it.
- AT MOST ONE OR TWO: a genuinely absurd or surreal idea. These reliably underperform here, so
  treat them as variety, never as the backbone of a batch.

FORMAT SPREAD (this matters — a batch of near-identical phrasings burns the format out):
- No single sentence pattern may cover more than about a quarter of the batch. Do NOT produce a
  long run of one template with the noun swapped; vary the framing across the batch.
- Never reuse the same main object twice in one batch, even with different endings.

Every topic should have one obvious focal object with a strong color or silhouette so the
thumbnail reads instantly. The subject must be recognizable BEFORE the joke — recognition first,
comedy second.
""";

        return $$"""
You are the content strategist for a faceless YouTube Shorts channel that makes {{flavor}}

Brainstorm {{count}} DISTINCT, specific, scroll-stopping video topics. Each topic is a short
phrase (3-8 words) naming the subject of ONE video — concrete and specific, not a title and
not a category. {{goodBad}} Make them varied — no two about the same thing, and do not reuse
the same main object/subject with different endings.
{{memeRange}}{{performanceBlock}}{{styleBlock}}{{avoidBlock}}
Output ONLY a single valid JSON object, no markdown fences, no commentary:
{ "topics": [ "topic one", "topic two", ... ] }
Exactly {{count}} topics.
""";
    }

    public static string PanelImage(Beat beat, int width, int height)
    {
        // Drawing (the literal scene) is the PRIMARY subject; a rage face is only a small
        // reaction beside it. A standalone big face happens only when there's no drawing.
        bool hasDrawing = !string.IsNullOrWhiteSpace(beat.Drawing);
        bool hasFace = !string.IsNullOrWhiteSpace(beat.RageFace);
        var face = hasFace ? beat.RageFace!.Replace('_', ' ') : "";

        var subjectParts = new List<string>();
        if (hasDrawing) subjectParts.Add(beat.Drawing!);
        if (beat.Options is { Count: 2 })
            subjectParts.Add($"drawn as two side-by-side versions, each labelled with a big circled number (1 = \"{beat.Options[0]}\", 2 = \"{beat.Options[1]}\")");

        if (hasFace && (hasDrawing || beat.Options is { Count: 2 }))
            subjectParts.Add($"with a small \"{face}\" rage-comic reaction face off to the side");
        else if (hasFace)
            subjectParts.Add($"a single large \"{face}\" rage-comic face, centered");

        if (subjectParts.Count == 0)
            subjectParts.Add("the scene the caption describes");
        var subject = string.Join(", ", subjectParts);

        var visualStyle = beat.VisualStyle?.Trim().ToLowerInvariant() switch
        {
            "photo" => "photo",
            "graphic" => "graphic",
            _ => "doodle",
        };

        // The style block is the SOLE authority on how the panel looks. It used to be injected
        // as an "override" into a base spec that already demanded crude MS-Paint art, so photo
        // and graphic panels received contradictory instructions and came out as doodles anyway.
        var styleDirection = visualStyle switch
        {
            "photo" => """
STYLE — PHOTOREALISTIC PRODUCT SHOT. Render the subject as a real photograph taken for this
video: a genuine-looking object, food, animal, or small scene, studio-lit on a plain white
seamless background, sharp focus, natural shadows, catalog/product-photography quality. It must
read as a real photo, NOT as an illustration, render, or drawing. No cartoon outlines and no
rage-comic face. Invent the subject — never reproduce an actual photograph, video frame, brand
logo, packaging design, celebrity, creator, or copyrighted character.
""",
            "graphic" => """
STYLE — CLEAN FLAT GRAPHIC / DIAGRAM. Render crisp, professional flat vector artwork on white:
clean stock-clipart illustration, a labelled diagram, a flowchart, a simple chart with axes, a
comparison layout, a sign, or a meter. Even black line weights, flat fills, generous spacing,
and neat legible labels — the look of a textbook figure or stock clipart, NOT hand-drawn art.
Two or three colors maximum. Invent any product or packaging; never copy a real brand or logo.
""",
            _ => """
STYLE — CRUDE MS-PAINT / RAGE-COMIC. Thick black hand-drawn outlines, intentionally lazy and
ugly, flat fills, at most 1-2 bold accent colors. Use this look ONLY for this panel.
""",
        };

        // Steer Codex to its built-in image_gen tool (NOT scripts — the Windows sandbox
        // blocks process spawning). We locate the saved PNG afterwards in C#.
        return $$"""
Use your built-in image_gen tool to generate ONE image, 1024x1024 square.
Do NOT write or run any scripts or shell commands — use the built-in image_gen tool directly.

{{styleDirection}}
Image spec:
- Plain PURE WHITE background with no environment, floor, or backdrop behind the subject.
- Subject: {{subject}}.
- This is the ONE piece of artwork for the entire video — it stays on screen from start to
  finish while short lines of text appear beneath it. So it must read on its own and support
  the whole premise, not just one moment of it. Do NOT include narration text in the image.
- Make the subject large and readable, filling roughly 70-85% of the image.
- Short readable labels are allowed when they are part of the joke, chart, diagram, debate,
  or option labels. Avoid tiny text.
- No borders, no frame, no watermark, no gradient.

Generate the image now with image_gen. You do not need to move, rename, or verify the file.
""";
    }
}
