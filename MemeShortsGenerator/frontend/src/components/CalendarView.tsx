import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, CalendarDays, Youtube } from "lucide-react";
import { api, type Job, thumbnailUrl } from "@/lib/api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { formatWhen } from "@/lib/status";

const DOW = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

function ymd(d: Date) {
  return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
}

export function CalendarView() {
  const { data: jobs } = useQuery({
    queryKey: ["calendar"],
    queryFn: api.calendar,
    refetchInterval: 5000,
  });

  const [cursor, setCursor] = React.useState(() => {
    const n = new Date();
    return new Date(n.getFullYear(), n.getMonth(), 1);
  });

  // Bucket scheduled jobs by their local publish day.
  const byDay = React.useMemo(() => {
    const map = new Map<string, Job[]>();
    for (const j of jobs ?? []) {
      if (!j.publishAt) continue;
      const d = new Date(j.publishAt);
      if (Number.isNaN(d.getTime())) continue;
      const key = ymd(d);
      (map.get(key) ?? map.set(key, []).get(key)!).push(j);
    }
    for (const arr of map.values())
      arr.sort((a, b) => (a.publishAt! < b.publishAt! ? -1 : 1));
    return map;
  }, [jobs]);

  const year = cursor.getFullYear();
  const month = cursor.getMonth();
  const first = new Date(year, month, 1);
  const startPad = first.getDay();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const todayKey = ymd(new Date());

  const cells: (Date | null)[] = [];
  for (let i = 0; i < startPad; i++) cells.push(null);
  for (let d = 1; d <= daysInMonth; d++) cells.push(new Date(year, month, d));
  while (cells.length % 7 !== 0) cells.push(null);

  const upcoming = (jobs ?? [])
    .filter((j) => j.publishAt && new Date(j.publishAt) >= new Date())
    .sort((a, b) => (a.publishAt! < b.publishAt! ? -1 : 1))
    .slice(0, 8);

  const monthLabel = cursor.toLocaleString(undefined, { month: "long", year: "numeric" });

  return (
    <div className="grid gap-6 lg:grid-cols-[1fr_320px]">
      <Card>
        <CardHeader className="flex-row items-center justify-between space-y-0">
          <CardTitle className="flex items-center gap-2">
            <CalendarDays className="size-5" /> {monthLabel}
          </CardTitle>
          <div className="flex items-center gap-1">
            <Button variant="outline" size="icon" onClick={() => setCursor(new Date(year, month - 1, 1))}>
              <ChevronLeft className="size-4" />
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => {
                const n = new Date();
                setCursor(new Date(n.getFullYear(), n.getMonth(), 1));
              }}
            >
              Today
            </Button>
            <Button variant="outline" size="icon" onClick={() => setCursor(new Date(year, month + 1, 1))}>
              <ChevronRight className="size-4" />
            </Button>
          </div>
        </CardHeader>
        <CardContent>
          <div className="grid grid-cols-7 gap-1">
            {DOW.map((d) => (
              <div key={d} className="pb-2 text-center text-xs font-medium text-muted-foreground">
                {d}
              </div>
            ))}
            {cells.map((date, i) => {
              if (!date) return <div key={i} className="min-h-24 rounded-md" />;
              const key = ymd(date);
              const items = byDay.get(key) ?? [];
              const isToday = key === todayKey;
              return (
                <div
                  key={i}
                  className={`min-h-24 rounded-md border p-1.5 ${
                    isToday ? "border-primary/60 bg-primary/5" : "border-border/60"
                  }`}
                >
                  <div className={`mb-1 text-xs ${isToday ? "font-bold text-primary" : "text-muted-foreground"}`}>
                    {date.getDate()}
                  </div>
                  <div className="space-y-1">
                    {items.slice(0, 3).map((j) => (
                      <DayPill key={j.id} job={j} />
                    ))}
                    {items.length > 3 && (
                      <div className="px-1 text-[10px] text-muted-foreground">+{items.length - 3} more</div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Upcoming uploads</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          {upcoming.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              Nothing scheduled yet. Approve videos in the Review tab and they'll appear here with
              their publish time.
            </p>
          ) : (
            upcoming.map((j) => (
              <div key={j.id} className="flex gap-3">
                {j.hasThumbnail ? (
                  <img
                    src={thumbnailUrl(j.id)}
                    alt=""
                    className="h-12 w-20 shrink-0 rounded object-cover"
                  />
                ) : (
                  <div className="h-12 w-20 shrink-0 rounded bg-secondary" />
                )}
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{j.youTubeTitle || j.title || j.topic}</p>
                  <p className="text-xs text-muted-foreground">{formatWhen(j.publishAt)}</p>
                </div>
                {j.youTubeVideoId && (
                  <a href={`https://youtu.be/${j.youTubeVideoId}`} target="_blank" rel="noreferrer">
                    <Youtube className="size-4 text-muted-foreground hover:text-foreground" />
                  </a>
                )}
              </div>
            ))
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function DayPill({ job }: { job: Job }) {
  const uploaded = job.upload === "uploaded";
  const time = job.publishAt
    ? new Date(job.publishAt).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" })
    : "";
  return (
    <div
      className={`truncate rounded px-1.5 py-0.5 text-[10px] leading-tight ${
        uploaded ? "bg-success/15 text-success" : "bg-primary/15 text-primary"
      }`}
      title={`${time} · ${job.youTubeTitle || job.title || job.topic}`}
    >
      {time} {job.youTubeTitle || job.title || job.topic}
    </div>
  );
}
