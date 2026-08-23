import { BeatVisual } from "../../types";
import { StatReveal } from "./StatReveal";
import { Chart } from "./Chart";
import { Comparison } from "./Comparison";
import { Tip } from "./Tip";

/** Routes a finance beat's data to the right in-card visual. */
export const FinanceVisual: React.FC<{ visual: BeatVisual; startFrame: number }> = ({ visual, startFrame }) => {
  switch (visual.type) {
    case "stat":
      return visual.stat ? <StatReveal stat={visual.stat} startFrame={startFrame} /> : null;
    case "chart":
      return visual.chart ? <Chart chart={visual.chart} startFrame={startFrame} /> : null;
    case "comparison":
      return visual.comparison ? <Comparison data={visual.comparison} startFrame={startFrame} /> : null;
    case "tip":
      return visual.tip ? <Tip tip={visual.tip} startFrame={startFrame} /> : null;
    default:
      return null;
  }
};
