import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { Wand2, ClipboardCheck, CalendarDays, Brain, CircleDot, SlidersHorizontal } from "lucide-react";
import { api, type Health } from "@/lib/api";
import { isPendingReview } from "@/lib/status";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { GenerateView } from "@/components/GenerateView";
import { ReviewView } from "@/components/ReviewView";
import { CalendarView } from "@/components/CalendarView";
import { SettingsView } from "@/components/SettingsView";

type TabKey = "generate" | "review" | "calendar" | "settings";

const NAV: { key: TabKey; label: string; icon: React.ReactNode }[] = [
  { key: "generate", label: "Generate", icon: <Wand2 className="size-4" /> },
  { key: "review", label: "Review", icon: <ClipboardCheck className="size-4" /> },
  { key: "calendar", label: "Calendar", icon: <CalendarDays className="size-4" /> },
  { key: "settings", label: "Settings", icon: <SlidersHorizontal className="size-4" /> },
];

export default function App() {
  const [tab, setTab] = React.useState<TabKey>("generate");

  const { data: health } = useQuery({
    queryKey: ["health"],
    queryFn: api.health,
    // Re-poll so config changes (e.g. enabling YouTube upload) reflect without a reload.
    refetchInterval: 15000,
  });
  const { data: jobs } = useQuery({
    queryKey: ["jobs"],
    queryFn: api.jobs,
    refetchInterval: 3000,
  });

  const pendingCount = (jobs ?? []).filter(isPendingReview).length;

  return (
    <div className="flex min-h-full">
      {/* Sidebar */}
      <aside className="sticky top-0 flex h-screen w-16 shrink-0 flex-col border-r border-border bg-card/40 p-3 sm:w-60">
        <div className="mb-8 flex items-center gap-3 px-1 pt-1">
          <div className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-primary/15 text-primary">
            <Brain className="size-6" />
          </div>
          <div className="hidden min-w-0 sm:block">
            <h1 className="truncate text-sm font-semibold leading-tight">Meme Shorts Studio</h1>
            <p className="truncate text-xs text-muted-foreground">Generate · review · schedule</p>
          </div>
        </div>

        <nav className="flex flex-col gap-1">
          {NAV.map((item) => {
            const active = tab === item.key;
            return (
              <button
                key={item.key}
                onClick={() => setTab(item.key)}
                title={item.label}
                className={cn(
                  "flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors",
                  "justify-center sm:justify-start",
                  active
                    ? "bg-primary/15 text-primary"
                    : "text-muted-foreground hover:bg-accent hover:text-foreground",
                )}
              >
                {item.icon}
                <span className="hidden sm:inline">{item.label}</span>
                {item.key === "review" && pendingCount > 0 && (
                  <Badge variant="default" className="ml-auto hidden sm:inline-flex">
                    {pendingCount}
                  </Badge>
                )}
                {item.key === "review" && pendingCount > 0 && (
                  <span className="ml-1 size-2 rounded-full bg-primary sm:hidden" />
                )}
              </button>
            );
          })}
        </nav>

        {/* Provider status, pinned to the bottom */}
        {health && (
          <div className="mt-auto hidden flex-col gap-2 border-t border-border pt-4 sm:flex">
            <StatusRow label="TTS" value={health.tts} />
            <StatusRow label="Panels" value={health.panels} />
            <StatusRow
              label="Upload"
              value={uploadSummary(health)}
              ok={
                health.uploadConfigured || health.facebookConfigured || health.instagramConfigured
              }
            />
          </div>
        )}
      </aside>

      {/* Main content */}
      <main className="min-w-0 flex-1">
        <div className="mx-auto max-w-5xl px-4 py-8 sm:px-8">
          <div className="mb-6">
            <h2 className="text-xl font-semibold">{NAV.find((n) => n.key === tab)?.label}</h2>
          </div>
          {tab === "generate" && <GenerateView health={health} />}
          {tab === "review" && <ReviewView health={health} />}
          {tab === "calendar" && <CalendarView />}
          {tab === "settings" && <SettingsView />}
        </div>
      </main>
    </div>
  );
}

/** Compact "where approved videos go" summary, e.g. "YT+IG · auto". */
function uploadSummary(health: Health): string {
  const short = [
    health.uploadConfigured ? "YT" : null,
    health.facebookConfigured ? "FB" : null,
    health.instagramConfigured ? "IG" : null,
  ].filter((s): s is string => s !== null);

  if (short.length === 0) return "off";
  return `${short.join("+")} · ${health.autoUpload ? "auto" : "manual"}`;
}

function StatusRow({ label, value, ok }: { label: string; value: string; ok?: boolean }) {
  return (
    <div className="flex items-center gap-2 px-1 text-xs">
      <CircleDot className={cn("size-3", ok === false ? "text-amber-400" : "text-success")} />
      <span className="text-muted-foreground">{label}</span>
      <span className="ml-auto font-medium">{value}</span>
    </div>
  );
}
