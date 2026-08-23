import { Img, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { RenderPost } from "../types";

/**
 * The reference channel's OTHER format: a real social post, read out verbatim, revealed one
 * wrapped line at a time. There is no written script and no generated artwork here — the post
 * IS the content. Measured frame-by-frame off "Genius Anti-Theft Method" (1080x1920, 60fps):
 *
 *  - the card is exactly 960px wide and horizontally LOCKED (x0/x1 stdev ~0 across 232 frames);
 *    it never scales, never tilts
 *  - the card's top creeps UPWARD at a dead-constant 10 px/s from ~1.2s until the last line
 *    lands, then holds — tracked by template-matching the avatar (470px -> 289px over 18s)
 *  - body text appears one WRAPPED LINE at a time as a HARD CUT (no fade), synced to narration
 *  - the post's image is flush to the card's left/right/bottom edges and is PUSHED DOWN as
 *    lines appear; the text has horizontal padding, the image does not (exactly like LinkedIn)
 *
 * Because the card grows downward from its top edge, positioning by `top` alone reproduces the
 * measured geometry of BOTH edges: bottom = top + height(t).
 */

/** Card geometry in the 1080x1920 frame, measured off the reference. */
export const POST_CARD = {
  left: 58,
  width: 960,
  /** Where the card's top edge sits before the drift starts. */
  topStart: 466,
  /** Where the drift stops. */
  topEnd: 285,
  driftStartMs: 1200,
  driftPxPerSec: 10,
};

/** Body type metrics, measured: 36px between wrapped lines, 71px between paragraphs. */
const LINE_STEP = 36;
const PARAGRAPH_EXTRA = 35;

export const PostCard: React.FC<{ post: RenderPost }> = ({ post }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;

  const topStart = post.topStart ?? POST_CARD.topStart;
  const topEnd = post.topEnd ?? POST_CARD.topEnd;
  const driftStartMs = post.driftStartMs ?? POST_CARD.driftStartMs;
  const pxPerSec = post.driftPxPerSec ?? POST_CARD.driftPxPerSec;

  // Linear pan, clamped at both ends — the reference holds still for ~1.2s, drifts at a
  // constant rate, then parks. An easing curve here would be wrong: the measurement is a
  // straight line (10px every second, i.e. 1px every 6 frames).
  const drifted = topStart - (Math.max(0, nowMs - driftStartMs) / 1000) * pxPerSec;
  const top = Math.max(topEnd, Math.min(topStart, drifted));

  // -1ms of slack: beat/line times are rounded to whole ms, so a line whose time lands exactly
  // on a frame boundary would otherwise flicker in a frame late.
  const lines = post.lines.filter((l) => nowMs >= l.startMs - 1);

  // The social-post header only exists if there is an author to show. Without one the card is
  // bare text + picture, which is what a Reddit meme wants.
  const hasChrome = Boolean(post.authorName || post.headline || post.meta || post.avatar);

  // ---- Slice mode -----------------------------------------------------------------------
  // The card is the screenshot itself, revealed band by band. Two crops of the SAME image:
  // a growing one (header + the caption lines said so far) and the media pinned beneath it.
  // Cropping rather than re-typesetting is the whole point — the pasted screenshot already
  // contains its own words, so drawing them again showed everything twice.
  if (post.imageWidth && post.imageHeight && post.image && post.blocks?.length) {
    const cardW = post.width ?? POST_CARD.width;
    const scale = cardW / post.imageWidth;

    // Each block is its own window onto the same screenshot, stacked in order. Consecutive
    // blocks are stretched to meet each other so the original spacing between lines is kept —
    // butting the crops together edge-to-edge squeezed the post into a solid wall of text.
    const visible = post.blocks.filter((b) => nowMs >= b.startMs - 1);

    // No dimming. A column that has been read STAYS fully visible once the other one starts —
    // the reveal itself already shows which half is being read, and blurring the finished side
    // just hid half the joke while the punchline landed.

    /** One crop of the screenshot: a window showing only the block's own rows and columns. */
    const slice = (b: NonNullable<typeof post.blocks>[number], key: string) => {
      const height = b.bottom - b.top;
      if (height <= 0) return null;
      const x0 = b.xLeft ?? 0;
      const x1 = b.xRight ?? post.imageWidth!;
      return (
        <div
          key={key}
          style={{
            position: "relative",
            width: (x1 - x0) * scale,
            height: height * scale,
            overflow: "hidden",
          }}
        >
          <Img
            src={staticFile(post.image!)}
            style={{
              position: "absolute",
              top: -b.top * scale,
              left: -x0 * scale,
              width: post.imageWidth! * scale,
              display: "block",
            }}
          />
        </div>
      );
    };

    // A flow diagram is not a card that grows — the picture keeps its own shape and each panel
    // lights up where it already sits, in reading order.
    //
    // ONE copy of the picture, with the panels not yet reached covered over. Drawing a separate
    // cropped copy per panel put five full-size layers on top of each other and Chrome ran out
    // of memory mid-render; the panels barely overlap, so covering is both cheaper and simpler.
    if (post.diagram) {
      const hidden = post.blocks.filter((b) => nowMs < b.startMs - 1);
      return (
        <div
          style={{
            position: "absolute",
            left: post.left ?? POST_CARD.left,
            width: cardW,
            height: post.imageHeight! * scale,
            top,
            borderRadius: 4,
            boxShadow: "0 10px 34px rgba(0,0,0,0.34)",
            overflow: "hidden",
            background: post.cardBackground ?? "#fff",
          }}
        >
          <Img
            src={staticFile(post.image!)}
            style={{ position: "absolute", top: 0, left: 0, width: cardW, display: "block" }}
          />
          {hidden.map((b, i) => {
            const x0 = b.xLeft ?? 0;
            const x1 = b.xRight ?? post.imageWidth!;
            // A couple of pixels of bleed, or antialiased edges leave a ghost outline behind.
            return (
              <div
                key={`h${i}`}
                style={{
                  position: "absolute",
                  left: x0 * scale - 2,
                  top: b.top * scale - 2,
                  width: (x1 - x0) * scale + 4,
                  height: (b.bottom - b.top) * scale + 4,
                  background: post.cardBackground ?? "#fff",
                }}
              />
            );
          })}
        </div>
      );
    }

    // Blocks sharing a group are a comparison: their two columns sit SIDE BY SIDE, each
    // stacking its own lines. Everything else is a plain vertical stack. Rendering a
    // comparison as full-width rows meant the right column arrived with the left, which is
    // not how the meme reads — one side is finished before the other begins.
    const rendered: React.ReactNode[] = [];
    for (let i = 0; i < visible.length; ) {
      const group = visible[i].group;
      if (group == null) {
        rendered.push(slice(visible[i], `b${i}`));
        i++;
        continue;
      }

      let end = i;
      while (end < visible.length && visible[end].group === group) end++;
      const inGroup = visible.slice(i, end);

      // Every slice of a comparison keeps its ORIGINAL position inside the group's box. Stacking
      // each column instead made the two sides drift apart whenever they held different numbers
      // of lines — a four-panel comic came out with a white band across its middle and its two
      // pictures at different heights.
      const all = post.blocks.filter((b) => b.group === group);
      const groupTop = Math.min(...all.map((b) => b.top));
      const groupBottom = Math.max(...all.map((b) => b.bottom));

      rendered.push(
        <div
          key={`g${group}`}
          style={{ position: "relative", width: "100%", height: (groupBottom - groupTop) * scale }}
        >
          {inGroup.map((b, n) => (
            <div
              key={`g${group}-${n}`}
              style={{
                position: "absolute",
                left: (b.xLeft ?? 0) * scale,
                top: (b.top - groupTop) * scale,
              }}
            >
              {slice(b, `s${n}`)}
            </div>
          ))}
        </div>,
      );
      i = end;
    }

    return (
      <div
        style={{
          position: "absolute",
          left: post.left ?? POST_CARD.left,
          width: cardW,
          top,
          borderRadius: 4,
          boxShadow: "0 10px 34px rgba(0,0,0,0.34)",
          overflow: "hidden",
          // The screenshot's own background, so the growing edge never flashes white on a
          // dark-mode capture.
          background: post.cardBackground ?? "#fff",
        }}
      >
        {rendered}
      </div>
    );
  }

  return (
    <div
      style={{
        position: "absolute",
        left: post.left ?? POST_CARD.left,
        width: post.width ?? POST_CARD.width,
        top,
        background: "#fff",
        borderRadius: 4,
        boxShadow: "0 10px 34px rgba(0,0,0,0.34)",
        overflow: "hidden",
        // LinkedIn's stack; Segoe UI is what the reference screenshot was captured in.
        fontFamily: '"Segoe UI", system-ui, -apple-system, Roboto, Helvetica, Arial, sans-serif',
        color: "rgba(0,0,0,0.9)",
      }}
    >
      {/* Header + body share the card's horizontal padding. The image below does NOT. */}
      <div style={{ padding: "2px 22px 0" }}>
        {/* Chrome is opt-in. A Reddit meme has no author header worth inventing, so with no
            author fields the card is just text + picture and the body starts at the top. */}
        {hasChrome ? (
        // -6: Segoe UI's half-leading puts the name's cap 6px lower than the reference's face
        // does at the same box height. Nudging the whole header row is simpler than fighting
        // per-row leading, and keeps the avatar aligned with the text.
        <div style={{ display: "flex", alignItems: "flex-start", gap: 24, marginTop: -6 }}>
          {post.avatar ? (
            <Img
              src={staticFile(post.avatar)}
              style={{ width: 115, height: 115, borderRadius: "50%", flexShrink: 0, objectFit: "cover" }}
            />
          ) : null}

          {/* Line heights are set in PX, not multipliers: the header rows were matched against
              the reference's measured ink positions (name/headline/link/meta ink tops sit 9 /
              51 / 81 / 113 px below the card's top edge), and a ratio drifts off those as soon
              as a font size changes. */}
          <div style={{ minWidth: 0, flex: 1 }}>
            <div style={{ display: "flex", alignItems: "baseline", gap: 8, lineHeight: "42px" }}>
              <span style={{ fontSize: 40, fontWeight: 600, letterSpacing: -0.2 }}>{post.authorName}</span>
              {post.authorDegree ? (
                <span style={{ fontSize: 26, color: "rgba(0,0,0,0.6)" }}>· {post.authorDegree}</span>
              ) : null}
            </div>
            {post.headline ? (
              <div style={{ fontSize: 29.6, color: "rgba(0,0,0,0.6)", lineHeight: "30px" }}>{post.headline}</div>
            ) : null}
            {post.linkText ? (
              <div style={{ fontSize: 29.6, color: "#0a66c2", fontWeight: 600, lineHeight: "32px" }}>
                {post.linkText}
              </div>
            ) : null}
            {post.meta ? (
              <div style={{ fontSize: 26, color: "rgba(0,0,0,0.6)", lineHeight: "30px" }}>{post.meta}</div>
            ) : null}
          </div>

          <div
            style={{
              fontSize: 40, fontWeight: 600, color: "#0a66c2",
              whiteSpace: "nowrap", lineHeight: "42px", marginRight: 14,
            }}
          >
            + Follow
          </div>
        </div>
        ) : null}

        {/* One div per PRE-WRAPPED line. The backend decides the wrap so the reveal boundaries
            always match what is drawn — relying on CSS auto-wrap here would silently desync
            the line timings from the rendered lines whenever the font metrics shifted. */}
        <div style={{ marginTop: hasChrome ? 37 : 16, paddingBottom: 2 }}>
          {lines.map((l, i) => (
            <div
              key={i}
              style={{
                // 31, not 33: the reference's longest line measures 910px inside a 916px text
                // column, and Segoe UI sets plain text ~2.6% wider than the face in the
                // reference screenshot — at 33px the last character was clipped off two lines.
                // The cliff is hard (overflow is hidden), so this keeps ~20px of slack.
                fontSize: 31,
                lineHeight: `${LINE_STEP}px`,
                marginTop: i > 0 && l.newParagraph ? PARAGRAPH_EXTRA : 0,
                whiteSpace: "pre",
              }}
            >
              {l.text}
            </div>
          ))}
        </div>
      </div>

      {post.image ? (
        <Img src={staticFile(post.image)} style={{ display: "block", width: "100%", marginTop: 34 }} />
      ) : null}
    </div>
  );
};
