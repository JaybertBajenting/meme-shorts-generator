import { AbsoluteFill, Audio, Loop, Sequence, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { Background } from "./components/Background";
import { PanelBoard } from "./components/PanelBoard";
import { PostCard } from "./components/PostCard";
import { FakeComment } from "./components/FakeComment";
import { FinanceStage } from "./components/finance/FinanceStage";
import { ListStage } from "./components/ListStage";
import { RenderProps } from "./types";

// THE PUNCHLINE SHRINK, measured on CobbleCat's Shorts (2026-09-26): on 8 of his 20 latest and
// on ALL FOUR of his biggest hits (5.2M, 3.3M, 2.9M, 2.8M), the moment the final reaction line
// starts ("god dangit!", "AAAAH!", "What is that?") the WHOLE frame - gameplay and card together
// - drops to 0.70-0.85 of its size inside black, in ONE frame (1080 -> 864 between two frames),
// and holds there to the end, 0.7-2.3s. Median scale ~0.79.
const PUNCH_SCALE = 0.79;
const PUNCH_MAX_MS = 2600;

export const MemeShort: React.FC<RenderProps> = (props) => {
  const { fps } = useVideoConfig();
  const frame = useCurrentFrame();
  const msToF = (ms: number) => Math.round((ms / 1000) * fps);

  // Only when the last line is a short reaction. A long final beat is not a punchline, and
  // sitting shrunk for five seconds would read as a mistake.
  const lastBeat = props.beats[props.beats.length - 1];
  const punch =
    props.contentType === "list" &&
    lastBeat != null &&
    props.beats.length > 2 &&
    lastBeat.endMs - lastBeat.startMs <= PUNCH_MAX_MS &&
    frame >= msToF(lastBeat.startMs);

  return (
    <AbsoluteFill style={{ backgroundColor: "#000" }}>
      {/* overflow:hidden so the frame is cut at its edge BEFORE it shrinks. The background is
          blurred and over-scaled 1.08x, and without the clip its overflow showed round the
          shrunk frame - measured 0.90 of the width instead of 0.79, with a soft edge where his
          is a hard one. */}
      <AbsoluteFill style={punch ? { transform: `scale(${PUNCH_SCALE})`, overflow: "hidden" } : { overflow: "hidden" }}>
      <Background
        src={props.background}
        loopFrames={props.backgroundLoopFrames}
        blur={props.backgroundBlur}
      />

      {/* Background music, mixed quietly under the narration. */}
      {props.music ? (
        props.musicLoopFrames && props.musicLoopFrames > 0 ? (
          <Loop durationInFrames={props.musicLoopFrames}>
            <Audio src={staticFile(props.music)} volume={props.musicVolume ?? 0.18} />
          </Loop>
        ) : (
          <Audio src={staticFile(props.music)} volume={props.musicVolume ?? 0.18} />
        )
      ) : null}

      {props.post ? (
        // Repost: the post card IS the whole visual. No panel, no captions, no fake comment —
        // the post's own last line is the punchline.
        <PostCard post={props.post} />
      ) : props.contentType === "list" ? (
        // List short: one picture per spoken line on a drifting card, karaoke captions over it.
        <ListStage
          beats={props.beats}
          captions={props.captions}
          mascot={props.mascot}
          mascotPoses={props.mascotPoses}
          visualSeed={props.visualSeed}
        />
      ) : props.contentType === "finance" ? (
        // Finance: one clean centered card (no tilt/stack), clean caption — a distinct look.
        <FinanceStage beats={props.beats} captions={props.captions} />
      ) : (
        <>
          {/*
            Meme: one growing panel that accumulates rows. It owns the title, artwork, per-beat
            lines AND the comment, so CaptionTrack/FakeComment are deliberately not rendered here
            — they would draw the same text a second time, floating off the panel.
          */}
          <PanelBoard
            beats={props.beats}
            title={props.title}
            format={props.format}
            fakeComment={props.fakeComment}
          />
        </>
      )}
      </AbsoluteFill>

      {/* List: one take for the whole script. Everything else: one clip per beat. */}
      {props.narration ? (
        <Audio src={staticFile(props.narration)} />
      ) : (
        props.beats.map((beat) => {
          if (!beat.audio) return null;
          const from = msToF(beat.startMs);
          const dur = Math.max(1, msToF(beat.endMs) - from);
          return (
            <Sequence key={beat.index} from={from} durationInFrames={dur} name={`vo ${beat.index}`}>
              <Audio src={staticFile(beat.audio)} />
            </Sequence>
          );
        })
      )}

      {/* Finance only — the meme panel draws the comment as its own final row. */}
      {props.contentType === "finance" && props.fakeComment ? (
        <FakeComment comment={props.fakeComment} />
      ) : null}
    </AbsoluteFill>
  );
};
