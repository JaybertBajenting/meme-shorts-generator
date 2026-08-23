import { Img, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { CARD, RenderBeat, RenderFakeComment } from "../types";
import { FinanceVisual } from "./finance/FinanceVisual";
import { MemeChart } from "./MemeChart";

/**
 * The reference channel's actual panel mechanic, measured frame-by-frame across 12 videos:
 *
 *  - the panel is a document with a FIXED WIDTH that GROWS DOWNWARD as content is added
 *    (giraffe-centaur: x-stdev 0.0 and width-stdev 0.0, but y 21.5 / height 43.2)
 *  - elements ACCUMULATE — already-drawn content stays pixel-identical while a new element
 *    appears beneath it (title + both giraffes unchanged while a comment appears below)
 *  - every change is a HARD CUT of exactly one frame (~17ms at 60fps); there is no fade
 *  - the whole panel stays centred as it grows
 *
 * So this renders a vertical STACK of rows, not one swapped image. Row 0 is the title, then the
 * hero artwork, then one text row per beat as its narration begins, then the comment. Rows never
 * change once placed — which is what makes it read as a single evolving post rather than a
 * slideshow of separate cards (the previous implementation regenerated the whole artifact every
 * beat, so the drawing visibly changed style and composition between beats).
 */
/** Codex sometimes writes the username with a leading "@" already, which rendered as "@@name". */
const handle = (u: string) => u.replace(/^@+/, "");

/**
 * Dialogue captions arrive script-formatted ("Marc: We invented bumpy wallpaper"). Inside a
 * speech bubble the speaker is already shown by side and colour, so the prefix is redundant
 * noise. Only strips a short leading Name: — never a mid-sentence colon.
 */
const stripSpeaker = (line: string) => line.replace(/^\s*[A-Z][\w'-]{0,14}\s*:\s*/, "");

/**
 * Formats where the ARTWORK changes every beat rather than text stacking under one fixed image.
 * Must stay in sync with PipelineService.LayoutNeedsImagePerBeat, which decides how many images
 * are actually generated — if these disagree, beats point at panel files that do not exist.
 */
const SEQUENCE_FORMATS = new Set(["single_visual_reveal", "escalating_corrections", "absurd_debate"]);

/** Chart cards are portrait on the reference channel — see the note on the panel's width. */
const CHART_CARD_W = 620;

export const PanelBoard: React.FC<{
  beats: RenderBeat[];
  title: string;
  format?: string;
  fakeComment?: RenderFakeComment | null;
}> = ({ beats, title, format, fakeComment }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;

  const visible = beats.filter((b) => nowMs >= b.startMs - 1);
  if (visible.length === 0) return null;

  const fmt = (format ?? "").trim().toLowerCase();
  // chart_gag: the chart frame never changes, only its data — so it always renders the CURRENT
  // beat's visual, exactly like a sequence layout, but drawn natively instead of from an image.
  // Native charts are disabled (see PipelineService.NativeChartsEnabled) — the style did not
  // match the reference yet. Flip this back to the commented expression to re-enable.
  const isChart = false; // fmt === "chart_gag" && beats.every((b) => b.visual);
  // Dialogue holds one image of both characters and accumulates speech bubbles under it.
  const isDialogue = fmt === "character_dialogue";
  const isSequence = isChart || SEQUENCE_FORMATS.has(fmt);
  // Sequence layouts show the CURRENT beat's artwork; text-stack layouts hold beat 0's.
  const hero = isSequence ? visible[visible.length - 1] : beats[0];
  // The heading is the storyboard's TITLE, which is written to be a title. Using beat 1's
  // caption here produced headings like "REAL LIFE" — a narration fragment, meaningless as a
  // heading — and also swallowed that beat's line so it never appeared in the stack.
  // Every beat's caption is a stacked row; the newest is highlighted and earlier ones settle.
  // Text-stack layouts accumulate every caption. Sequence layouts show only the current line —
  // the picture is carrying the progression, so stacking old lines under a changed image would
  // caption artwork that is no longer on screen.
  // Charts print nothing under the axes — the caption has already been promoted to the heading,
  // so repeating it below would duplicate the line.
  const rows = isChart ? [] : isSequence ? visible.slice(-1) : visible;
  const commentVisible = fakeComment && nowMs >= fakeComment.startMs - 1;

  return (
    <div
      style={{
        position: "absolute",
        // Chart cards are NARROWER and taller than the standard panel. Measured off the
        // reference: his chart card is portrait (~3:4), which gives the curve vertical room to
        // show its shape. At the full 770px width the plot area was wide and short, flattening
        // every curve into a nearly straight line.
        left: isChart ? CARD.left + (CARD.width - CHART_CARD_W) / 2 : CARD.left,
        width: isChart ? CHART_CARD_W : CARD.width,
        // Grow from a fixed centre so the panel expands symmetrically as rows are added.
        top: CARD.top + CARD.height / 2,
        transform: "translateY(-50%)",
        background: "#fff",
        borderRadius: 6,
        boxShadow: "0 14px 40px rgba(0,0,0,0.40)",
        padding: "30px 30px 26px",
        display: "flex",
        flexDirection: "column",
        gap: 18,
        fontFamily: '"Arial Black", Impact, system-ui, sans-serif',
      }}
    >
      <div
        style={{
          // Smaller on charts: his chart headings are modest relative to the card, and a huge
          // bold title was eating the vertical space the curve needs.
          fontSize: isChart ? 34 : 46,
          lineHeight: 1.06,
          fontWeight: 900,
          color: "#111",
          textAlign: "center",
        }}
      >
        {/* On chart videos the heading IS the changing label — that re-labelling is the joke
            ("Your Sanity", "Frequency of crying" over one unchanging axis). */}
        {isChart ? hero.caption : title}
      </div>

      {/* Hero artwork — generated once and never replaced, so it cannot drift between beats. */}
      <div style={{ display: "flex", justifyContent: "center" }}>
        {isChart && hero.visual?.chart ? (
          // Tall fixed box: portrait plot area, and fixed so the axes stay put while the curve
          // changes beat to beat.
          <div style={{ width: "100%", height: 620 }}>
            <MemeChart points={hero.visual.chart.points} beatIndex={hero.index} />
          </div>
        ) : hero.visual ? (
          <div style={{ width: "100%", height: 460 }}>
            <FinanceVisual visual={hero.visual} startFrame={0} />
          </div>
        ) : (
          <Img
            src={staticFile(hero.panel)}
            style={{ width: "100%", maxHeight: 500, objectFit: "contain" }}
          />
        )}
      </div>

      {rows.map((b, i) => {
        const isNewest = i === rows.length - 1;

        // Dialogue: alternating speech bubbles instead of centred lines. The reference channel's
        // two-character sketches ("How Camels Were Made", "Cereal and Newspaper Guy") alternate
        // strictly A/B, so speaker is derived from the beat index — no schema change needed.
        if (isDialogue) {
          const left = b.index % 2 === 0;
          return (
            <div
              key={b.index}
              style={{ display: "flex", justifyContent: left ? "flex-start" : "flex-end" }}
            >
              <div
                style={{
                  maxWidth: "82%",
                  background: left ? "#f1f5f9" : "#e11d48",
                  color: left ? "#111" : "#fff",
                  border: left ? "3px solid #111" : "3px solid #9f1239",
                  borderRadius: 20,
                  // Flatten the corner nearest the speaker so it reads as a tail.
                  borderBottomLeftRadius: left ? 4 : 20,
                  borderBottomRightRadius: left ? 20 : 4,
                  padding: "14px 20px",
                  fontSize: 30,
                  lineHeight: 1.15,
                  fontWeight: 900,
                  textAlign: "left",
                  opacity: isNewest ? 1 : 0.82,
                }}
              >
                {stripSpeaker(b.caption)}
              </div>
            </div>
          );
        }

        return (
          <div
            key={b.index}
            style={{
              fontSize: 36,
              lineHeight: 1.12,
              fontWeight: 900,
              textAlign: "center",
              color: isNewest ? "#e11d48" : "#111",
            }}
          >
            {b.caption}
          </div>
        );
      })}

      {commentVisible && fakeComment ? (
        <div
          style={{
            display: "flex",
            gap: 14,
            alignItems: "flex-start",
            borderTop: "2px solid #e5e5e5",
            paddingTop: 16,
            fontFamily: "system-ui, -apple-system, Segoe UI, Roboto, sans-serif",
          }}
        >
          <div
            style={{
              width: 52,
              height: 52,
              borderRadius: "50%",
              flexShrink: 0,
              background: "linear-gradient(135deg,#7c3aed,#06b6d4)",
              color: "#fff",
              fontSize: 26,
              fontWeight: 700,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
            }}
          >
            {(handle(fakeComment.username)[0] || "?").toUpperCase()}
          </div>
          <div style={{ minWidth: 0, textAlign: "left" }}>
            <div style={{ fontSize: 22, fontWeight: 700, color: "#111" }}>
              @{handle(fakeComment.username)}
            </div>
            <div style={{ fontSize: 26, lineHeight: 1.25, color: "#111" }}>{fakeComment.text}</div>
          </div>
        </div>
      ) : null}
    </div>
  );
};
