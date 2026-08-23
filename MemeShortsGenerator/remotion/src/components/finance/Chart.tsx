import { interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { FIN_FONT } from "./font";

type Pt = { label: string; value: number };
type ChartData = { kind: "bar" | "line" | "pie" | "donut" | "progress" | "ranking"; unit?: string | null; points: Pt[] };
const COLORS = ["#2563eb", "#16a34a", "#f59e0b", "#e11d48", "#7c3aed", "#0891b2"];

export const Chart: React.FC<{ chart: ChartData; startFrame: number }> = ({ chart, startFrame }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const local = Math.max(0, frame - startFrame);
  const p = interpolate(local, [0, Math.max(1, fps * 0.8)], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });

  const pts = (chart.points ?? []).slice(0, 5);
  if (pts.length === 0) return null;
  const unit = chart.unit ?? "";
  const kind = chart.kind ?? "bar";

  return (
    <div style={{ width: "100%", height: "100%", padding: "24px 44px 28px", boxSizing: "border-box", fontFamily: FIN_FONT }}>
      {kind === "pie" ? <Pie pts={pts} p={p} donut={false} />
        : kind === "donut" ? <Pie pts={pts} p={p} donut={true} />
        : kind === "line" ? <Line pts={pts} p={p} unit={unit} />
        : kind === "ranking" ? <Ranking pts={pts} p={p} unit={unit} />
        : kind === "progress" ? <Progress pts={pts} p={p} unit={unit} />
        : <Bars pts={pts} p={p} unit={unit} />}
    </div>
  );
};

const fmt = (v: number, unit: string) => (Math.abs(v) >= 1000 ? (v / 1000).toFixed(v % 1000 === 0 ? 0 : 1) + "k" : String(Math.round(v))) + unit;

const Bars: React.FC<{ pts: { label: string; value: number }[]; p: number; unit: string }> = ({ pts, p, unit }) => {
  const max = Math.max(...pts.map((x) => x.value), 1);
  const grid = [0.25, 0.5, 0.75, 1];
  return (
    <div style={{ position: "relative", height: "100%", display: "flex", flexDirection: "column" }}>
      <div style={{ position: "relative", flex: 1 }}>
        {/* gridlines */}
        {grid.map((g, i) => (
          <div key={i} style={{ position: "absolute", left: 0, right: 0, bottom: `${g * 82 + 8}%`, borderTop: "2px dashed #e5e9f0" }} />
        ))}
        {/* bars */}
        <div style={{ position: "absolute", inset: 0, display: "flex", alignItems: "flex-end", justifyContent: "space-around", gap: 22, paddingBottom: "8%" }}>
          {pts.map((pt, i) => {
            const h = (pt.value / max) * 82 * p;
            return (
              <div key={i} style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "flex-end", height: "100%" }}>
                <div style={{ fontSize: 30, fontWeight: 900, color: "#0f172a", marginBottom: 8 }}>{fmt(pt.value * p, unit)}</div>
                <div style={{ width: "72%", height: `${h}%`, background: COLORS[i % COLORS.length], borderRadius: "12px 12px 0 0", boxShadow: "0 6px 16px rgba(0,0,0,0.12)" }} />
              </div>
            );
          })}
        </div>
      </div>
      <div style={{ display: "flex", justifyContent: "space-around", gap: 22, marginTop: 10 }}>
        {pts.map((pt, i) => <div key={i} style={{ flex: 1, fontSize: 25, fontWeight: 800, color: "#475569", textAlign: "center" }}>{pt.label}</div>)}
      </div>
    </div>
  );
};

const Line: React.FC<{ pts: { label: string; value: number }[]; p: number; unit: string }> = ({ pts, p, unit }) => {
  const max = Math.max(...pts.map((x) => x.value), 1);
  const W = 720, H = 460, pad = 56;
  const xs = pts.map((_, i) => pad + (i * (W - 2 * pad)) / Math.max(1, pts.length - 1));
  const ys = pts.map((pt) => H - pad - (pt.value / max) * (H - 2 * pad));
  const visible = Math.max(2, Math.ceil(pts.length * p));
  const line = xs.slice(0, visible).map((x, i) => `${i === 0 ? "M" : "L"} ${x} ${ys[i]}`).join(" ");
  const area = `${line} L ${xs[visible - 1]} ${H - pad} L ${xs[0]} ${H - pad} Z`;
  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: "100%", height: "100%" }}>
      {[0.25, 0.5, 0.75].map((g, i) => <line key={i} x1={pad} x2={W - pad} y1={H - pad - g * (H - 2 * pad)} y2={H - pad - g * (H - 2 * pad)} stroke="#e5e9f0" strokeWidth={2} strokeDasharray="6 6" />)}
      <path d={area} fill="#16a34a22" />
      <path d={line} fill="none" stroke="#16a34a" strokeWidth={8} strokeLinecap="round" strokeLinejoin="round" />
      {xs.slice(0, visible).map((x, i) => (
        <g key={i}>
          <circle cx={x} cy={ys[i]} r={9} fill="#16a34a" />
          <text x={x} y={ys[i] - 18} fontSize={24} fontWeight={900} textAnchor="middle" fill="#0f172a">{fmt(pts[i].value, unit)}</text>
        </g>
      ))}
      {pts.map((pt, i) => <text key={i} x={xs[i]} y={H - 16} fontSize={24} fontWeight={800} textAnchor="middle" fill="#475569">{pt.label}</text>)}
    </svg>
  );
};

const Pie: React.FC<{ pts: { label: string; value: number }[]; p: number; donut: boolean }> = ({ pts, p, donut }) => {
  const total = pts.reduce((a, b) => a + b.value, 0) || 1;
  const R = 165, cx = 210, cy = 230;
  const top = [...pts].sort((a, b) => b.value - a.value)[0];
  let angle = -Math.PI / 2;
  return (
    <svg viewBox="0 0 720 460" style={{ width: "100%", height: "100%" }}>
      {pts.map((pt, i) => {
        const slice = (pt.value / total) * Math.PI * 2 * p;
        const x1 = cx + R * Math.cos(angle), y1 = cy + R * Math.sin(angle);
        angle += slice;
        const x2 = cx + R * Math.cos(angle), y2 = cy + R * Math.sin(angle);
        const large = slice > Math.PI ? 1 : 0;
        return <path key={i} d={`M ${cx} ${cy} L ${x1} ${y1} A ${R} ${R} 0 ${large} 1 ${x2} ${y2} Z`} fill={COLORS[i % COLORS.length]} stroke="#fff" strokeWidth={4} />;
      })}
      {donut ? (
        <>
          <circle cx={cx} cy={cy} r={R * 0.58} fill="#fff" />
          <text x={cx} y={cy - 4} fontSize={52} fontWeight={800} textAnchor="middle" fill="#0f172a">{Math.round((top.value / total) * 100)}%</text>
          <text x={cx} y={cy + 36} fontSize={22} fontWeight={600} textAnchor="middle" fill="#64748b">{top.label}</text>
        </>
      ) : null}
      {pts.map((pt, i) => (
        <g key={i}>
          <rect x={420} y={64 + i * 56} width={30} height={30} fill={COLORS[i % COLORS.length]} rx={7} />
          <text x={460} y={88 + i * 56} fontSize={22} fontWeight={600} fill="#0f172a">
            {(pt.label.length > 14 ? pt.label.slice(0, 14) + "…" : pt.label)} · {Math.round((pt.value / total) * 100)}%
          </text>
        </g>
      ))}
    </svg>
  );
};

/** Ranking: items sorted high→low as a numbered leaderboard with bars. */
const Ranking: React.FC<{ pts: Pt[]; p: number; unit: string }> = ({ pts, p, unit }) => {
  const sorted = [...pts].sort((a, b) => b.value - a.value);
  const max = Math.max(...sorted.map((x) => x.value), 1);
  return (
    <div style={{ display: "flex", flexDirection: "column", justifyContent: "center", height: "100%", gap: 14 }}>
      {sorted.map((pt, i) => (
        <div key={i} style={{ display: "flex", alignItems: "center", gap: 14 }}>
          <div style={{ width: 46, height: 46, borderRadius: "50%", background: COLORS[i % COLORS.length], color: "#fff",
            fontWeight: 800, fontSize: 26, display: "flex", alignItems: "center", justifyContent: "center", flex: "0 0 auto" }}>{i + 1}</div>
          <div style={{ flex: 1 }}>
            <div style={{ display: "flex", justifyContent: "space-between", fontSize: 26, fontWeight: 700, color: "#0f172a", marginBottom: 5 }}>
              <span>{pt.label}</span><span>{fmt(pt.value, unit)}</span>
            </div>
            <div style={{ height: 22, background: "#eef2f7", borderRadius: 8, overflow: "hidden" }}>
              <div style={{ width: `${(pt.value / max) * 100 * p}%`, height: "100%", background: COLORS[i % COLORS.length], borderRadius: 8 }} />
            </div>
          </div>
        </div>
      ))}
    </div>
  );
};

/** Progress: horizontal bars as a share of a whole (e.g. how income splits). */
const Progress: React.FC<{ pts: Pt[]; p: number; unit: string }> = ({ pts, p, unit }) => {
  const total = pts.reduce((a, b) => a + b.value, 0) || 1;
  return (
    <div style={{ display: "flex", flexDirection: "column", justifyContent: "center", height: "100%", gap: 22 }}>
      {pts.map((pt, i) => (
        <div key={i}>
          <div style={{ display: "flex", justifyContent: "space-between", fontSize: 27, fontWeight: 700, color: "#0f172a", marginBottom: 6 }}>
            <span>{pt.label}</span><span>{Math.round((pt.value / total) * 100)}% · {fmt(pt.value, unit)}</span>
          </div>
          <div style={{ height: 32, background: "#eef2f7", borderRadius: 10, overflow: "hidden" }}>
            <div style={{ width: `${(pt.value / total) * 100 * p}%`, height: "100%", background: COLORS[i % COLORS.length], borderRadius: 10 }} />
          </div>
        </div>
      ))}
    </div>
  );
};
