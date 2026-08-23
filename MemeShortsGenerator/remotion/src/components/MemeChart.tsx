import { ChartPoint } from "../types";

/**
 * The reference channel's chart style, matched frame-by-frame against its "Lines of Life" video.
 *
 * Deliberately NOT the finance chart (which stays as-is for that channel). Compared side by side,
 * his charts are far barer and every difference below was a visible mismatch:
 *   - NO value labels — ours printed "64%" over every point; his shows no numbers at all
 *   - NO dots at the data points, and NO shaded fill under the curve
 *   - a SMOOTH curve, not straight segments between points
 *   - bare L-shaped axes: one vertical, one horizontal, no box and no gridlines
 *   - x labels rotated diagonally under the axis, no y labels whatsoever
 *   - a DIFFERENT line colour each beat (dark blue, magenta, green, black)
 * The chart carries no title here: the panel prints the beat's own caption above it, because on
 * his channel the changing TITLE is the joke ("Your Sanity", "Frequency of crying").
 */

/** Cycled per beat, matching the colours he actually uses. */
const LINE_COLOURS = ["#1d3fbf", "#b5179e", "#22c55e", "#111111", "#e11d48", "#0891b2"];

/** Cardinal spline through the points — his curves bend, they are not polylines. */
function smoothPath(pts: { x: number; y: number }[]): string {
  if (pts.length < 2) return "";
  const d = [`M ${pts[0].x} ${pts[0].y}`];
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i - 1] ?? pts[i];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const p3 = pts[i + 2] ?? p2;
    // Catmull-Rom -> cubic bezier control points (tension 6 ~= a gentle, hand-drawn bend).
    const c1x = p1.x + (p2.x - p0.x) / 6;
    const c1y = p1.y + (p2.y - p0.y) / 6;
    const c2x = p2.x - (p3.x - p1.x) / 6;
    const c2y = p2.y - (p3.y - p1.y) / 6;
    d.push(`C ${c1x} ${c1y}, ${c2x} ${c2y}, ${p2.x} ${p2.y}`);
  }
  return d.join(" ");
}

export const MemeChart: React.FC<{ points: ChartPoint[]; beatIndex: number }> = ({
  points,
  beatIndex,
}) => {
  if (!points || points.length < 2) return null;

  // PORTRAIT plot area (taller than wide), matching the reference: a wide, short plot flattened
  // every curve into a near-straight line and lost the shape that carries the joke.
  const W = 560;
  const H = 660;
  // padL is generous because labels rotate anti-clockwise and extend DOWN-LEFT from their tick:
  // at 46 the first label ("Driveway") was clipped to "riveway" against the panel edge.
  const padL = 92;
  const padR = 20;
  const padT = 14;
  const padB = 110; // room for the rotated labels

  const values = points.map((p) => p.value);
  // Scale from zero so the shape reads honestly; pad the top so peaks don't touch the edge.
  const max = Math.max(...values, 1) * 1.12;

  const plotW = W - padL - padR;
  const plotH = H - padT - padB;
  const xy = points.map((p, i) => ({
    x: padL + (plotW * i) / (points.length - 1),
    y: padT + plotH - (plotH * p.value) / max,
  }));

  const colour = LINE_COLOURS[beatIndex % LINE_COLOURS.length];

  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: "100%", height: "100%" }}>
      {/* Bare L-shaped axes — no box, no grid. */}
      <line x1={padL} y1={padT} x2={padL} y2={padT + plotH} stroke="#111" strokeWidth={5} />
      <line
        x1={padL}
        y1={padT + plotH}
        x2={W - padR}
        y2={padT + plotH}
        stroke="#111"
        strokeWidth={5}
      />

      {/* Thin stroke: his curves are fine lines, not heavy ones. */}
      <path d={smoothPath(xy)} fill="none" stroke={colour} strokeWidth={4} strokeLinecap="round" />

      {/* Tick + diagonal label per point. No values printed anywhere. */}
      {points.map((p, i) => (
        <g key={i}>
          <line
            x1={xy[i].x}
            y1={padT + plotH}
            x2={xy[i].x}
            y2={padT + plotH + 9}
            stroke="#111"
            strokeWidth={3}
          />
          <text
            x={xy[i].x}
            y={padT + plotH + 20}
            transform={`rotate(-38 ${xy[i].x} ${padT + plotH + 20})`}
            textAnchor="end"
            fontSize={21}
            fontWeight={700}
            fill="#111"
            fontFamily="system-ui, sans-serif"
          >
            {p.label}
          </text>
        </g>
      ))}
    </svg>
  );
};
