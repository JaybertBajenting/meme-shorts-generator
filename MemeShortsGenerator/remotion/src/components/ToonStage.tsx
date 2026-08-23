import { type Caption } from "@remotion/captions";
import { loadFont } from "@remotion/google-fonts/Baloo2";
import { Img, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { RenderBeat } from "../types";

const { fontFamily: FONT } = loadFont("normal", { weights: ["800"] });

/**
 * CUTOUT animation: one character, drawn once as a sheet of poses, moved by code.
 *
 * This is not frame-by-frame animation and does not pretend to be. It is the technique South
 * Park uses — a fixed set of drawings, repositioned, scaled and swapped on the beat. What makes
 * it work is that the drawings are IDENTICAL between shots (they come from a single generated
 * pose sheet), so the character never drifts the way separately generated images do.
 *
 * The mouth flap is a two-frame swap between the idle and talking poses, driven by the same
 * word timings the captions use — so the character's mouth moves only while a word is being
 * said, which is what reads as "talking" rather than "vibrating".
 */

const CARD = 940;
const CARD_CENTRE_Y = 860;

export type ToonPose = "idle" | "talk" | "shock" | "point";

export const ToonStage: React.FC<{
  beats: RenderBeat[];
  captions: Caption[];
  /** Folder inside the public dir holding idle/talk/shock/point PNGs. */
  toonDir?: string | null;
  /** One pose per beat; falls back to a talking character. */
  poses?: ToonPose[] | null;
}> = ({ beats, captions, toonDir, poses }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const nowMs = (frame / fps) * 1000;
  const t = frame / fps;

  const beatIndex = beats.findIndex((b) => nowMs >= b.startMs && nowMs < b.endMs);
  const beat = beats[beatIndex] ?? beats[beats.length - 1] ?? beats[0];
  if (!beat) return null;

  const dir = toonDir ?? "toon";
  const basePose = poses?.[Math.max(beatIndex, 0)] ?? "talk";

  // Is a word being spoken RIGHT NOW? That drives the mouth, nothing else.
  const speaking = captions.some((c) => nowMs >= c.startMs && nowMs < c.endMs);
  // ~8 flaps a second: fast enough to read as speech, slow enough not to strobe.
  const mouthOpen = speaking && Math.floor(t * 8) % 2 === 0;
  const pose: ToonPose =
    basePose === "talk" ? (mouthOpen ? "talk" : "idle") : basePose;

  // Entrance + idle motion. Each beat re-enters so the cut has some snap to it.
  const since = Math.max(0, (nowMs - beat.startMs) / 1000);
  const pop = Math.min(1, since / 0.18);
  const settle = 1 + (1 - pop) * 0.12;                 // slight overshoot on the cut
  const bob = Math.sin(t * 3.1) * 10;
  const sway = Math.sin(t * 1.3) * 6;
  const lean = Math.sin(t * 0.9) * 1.5;

  const page = [...captions]
    .reverse()
    .find((c) => nowMs >= c.startMs - 1);

  return (
    <>
      {/* The scene card */}
      <div
        style={{
          position: "absolute",
          left: (1080 - CARD) / 2,
          top: CARD_CENTRE_Y - CARD / 2,
          width: CARD,
          height: CARD,
          borderRadius: 44,
          overflow: "hidden",
          boxShadow: "0 24px 60px rgba(0,0,0,0.45)",
          background: "linear-gradient(#bfe6ff 0%, #bfe6ff 62%, #8fd06a 62%, #7cc257 100%)",
        }}
      >
        {/* Character, standing on the ground line */}
        <Img
          src={staticFile(`${dir}/${pose}.png`)}
          style={{
            position: "absolute",
            left: "50%",
            bottom: 70,
            height: CARD * 0.66,
            transform: `translateX(-50%) translateX(${sway}px) translateY(${bob}px) scale(${settle}) rotate(${lean}deg)`,
            transformOrigin: "50% 100%",
          }}
        />
      </div>

      {/* Caption sits under the character, inside the card, like the reference channels */}
      {page ? (
        <div
          style={{
            position: "absolute",
            left: 0,
            right: 0,
            top: CARD_CENTRE_Y + CARD / 2 - 150,
            display: "flex",
            justifyContent: "center",
            padding: "0 60px",
          }}
        >
          <span
            style={{
              fontFamily: `${FONT}, "Comic Sans MS", system-ui, sans-serif`,
              fontSize: 104,
              fontWeight: 800,
              color: "#fff",
              textAlign: "center",
              WebkitTextStroke: "13px #000",
              paintOrder: "stroke fill",
              letterSpacing: -1,
            }}
          >
            {page.text.trim()}
          </span>
        </div>
      ) : null}
    </>
  );
};
