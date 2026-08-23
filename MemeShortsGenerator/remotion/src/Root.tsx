import { Composition } from "remotion";
import { MemeShort } from "./MemeShort";
import { defaultProps, RenderProps } from "./types";

export const RemotionRoot: React.FC = () => {
  return (
    <Composition
      id="MemeShort"
      component={MemeShort}
      durationInFrames={defaultProps.durationInFrames}
      fps={defaultProps.fps}
      width={defaultProps.width}
      height={defaultProps.height}
      defaultProps={defaultProps}
      // The real metadata comes from the props.json the .NET pipeline writes.
      calculateMetadata={({ props }: { props: RenderProps }) => ({
        durationInFrames: Math.max(1, props.durationInFrames || defaultProps.durationInFrames),
        fps: props.fps || defaultProps.fps,
        width: props.width || defaultProps.width,
        height: props.height || defaultProps.height,
      })}
    />
  );
};
