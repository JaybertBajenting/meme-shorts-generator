import { AbsoluteFill, Loop, OffthreadVideo, interpolate, staticFile, useCurrentFrame, useVideoConfig } from "remotion";

/**
 * The "brainrot" background. Uses the provided gameplay clip (looped, muted) if
 * present; otherwise an animated gradient so the project always renders.
 *
 * Remotion's video components don't have a `loop` prop in this version, so we wrap
 * the clip in <Loop> using its measured length (passed from the .NET pipeline).
 */
export const Background: React.FC<{
  src?: string | null;
  loopFrames?: number | null;
  /** Blur radius in px. List shorts blur the gameplay so the card carries the eye. */
  blur?: number | null;
}> = ({ src, loopFrames, blur }) => {
  if (!src) return <GradientFallback />;

  const video = (
    <AbsoluteFill>
      <OffthreadVideo
        src={staticFile(src)}
        muted
        style={{
          width: "100%",
          height: "100%",
          objectFit: "cover",
          // Scaled up slightly: blurring samples past the frame edge and would otherwise
          // leave a soft transparent border all the way round.
          ...(blur ? { filter: `blur(${blur}px)`, transform: "scale(1.08)" } : {}),
        }}
      />
    </AbsoluteFill>
  );

  return (
    <AbsoluteFill>
      {loopFrames && loopFrames > 0 ? <Loop durationInFrames={loopFrames}>{video}</Loop> : video}
      {/* slight darken so the white cards pop */}
      <AbsoluteFill style={{ backgroundColor: "rgba(0,0,0,0.18)" }} />
    </AbsoluteFill>
  );
};

const GradientFallback: React.FC = () => {
  const frame = useCurrentFrame();
  const { durationInFrames } = useVideoConfig();
  const hue = interpolate(frame, [0, durationInFrames], [220, 320]);
  return (
    <AbsoluteFill
      style={{
        background: `linear-gradient(135deg, hsl(${hue},40%,16%), hsl(${hue + 40},45%,9%))`,
      }}
    />
  );
};
