import { AbsoluteFill, Audio, Loop, Sequence, staticFile, useVideoConfig } from "remotion";
import { Background } from "./components/Background";
import { PanelBoard } from "./components/PanelBoard";
import { PostCard } from "./components/PostCard";
import { FakeComment } from "./components/FakeComment";
import { FinanceStage } from "./components/finance/FinanceStage";
import { ListStage } from "./components/ListStage";
import { RenderProps } from "./types";

export const MemeShort: React.FC<RenderProps> = (props) => {
  const { fps } = useVideoConfig();
  const msToF = (ms: number) => Math.round((ms / 1000) * fps);

  return (
    <AbsoluteFill style={{ backgroundColor: "#000" }}>
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
        <ListStage beats={props.beats} captions={props.captions} mascot={props.mascot} />
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

      {/* One voiceover clip per beat, placed on the timeline. */}
      {props.beats.map((beat) => {
        const from = msToF(beat.startMs);
        const dur = Math.max(1, msToF(beat.endMs) - from);
        return (
          <Sequence key={beat.index} from={from} durationInFrames={dur} name={`vo ${beat.index}`}>
            <Audio src={staticFile(beat.audio)} />
          </Sequence>
        );
      })}

      {/* Finance only — the meme panel draws the comment as its own final row. */}
      {props.contentType === "finance" && props.fakeComment ? (
        <FakeComment comment={props.fakeComment} />
      ) : null}
    </AbsoluteFill>
  );
};
