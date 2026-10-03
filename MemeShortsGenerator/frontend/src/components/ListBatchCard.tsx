import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Hash, Sparkles, Square, Newspaper, TriangleAlert } from "lucide-react";
import { api, type ListBatch } from "@/lib/api";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Spinner } from "@/components/ui/spinner";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";

const MAX = 30;

function errMsg(e: unknown, fallback: string): string {
  const anyE = e as { response?: { data?: { error?: string; detail?: string } }; message?: string };
  return anyE?.response?.data?.error ?? anyE?.response?.data?.detail ?? anyE?.message ?? fallback;
}

/**
 * "Make N videos" for list channels. The batch runs on the API (ListBatchRunner), one video after
 * another, so closing this page does not stop it - the card just shows its progress when opened.
 * It stops by itself when Codex runs out of quota instead of making videos with missing pictures.
 */
export function ListBatchCard({ channel, paused }: { channel: string; paused?: boolean }) {
  const qc = useQueryClient();
  const [count, setCount] = React.useState<number>(5);

  const { data: batch } = useQuery({
    queryKey: ["listBatch"],
    queryFn: api.listBatch,
    refetchInterval: (q) => {
      const s = (q.state.data as ListBatch | undefined)?.state;
      return s === "running" || s === "stopping" ? 4000 : 30000;
    },
  });

  // Each finished script becomes a job, so the Review tab should see it without a reload.
  const made = batch?.videos.length ?? 0;
  React.useEffect(() => {
    if (made > 0) qc.invalidateQueries({ queryKey: ["jobs"] });
  }, [made, qc]);

  const start = useMutation({
    mutationFn: () => api.startListBatch({ count, channel }),
    onSuccess: (data) => qc.setQueryData(["listBatch"], data),
  });
  const stop = useMutation({
    mutationFn: api.stopListBatch,
    onSuccess: (data) => qc.setQueryData(["listBatch"], data),
  });

  const busy = batch?.state === "running" || batch?.state === "stopping";
  const finished = batch && !busy && batch.state !== "idle";
  const pct = batch && batch.requested > 0 ? (made / batch.requested) * 100 : 0;

  return (
    <Card className="border-primary/30 lg:col-span-2">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Sparkles className="size-5 text-primary" /> Make several videos
        </CardTitle>
        <CardDescription>
          Codex picks each subject, writes the script, draws the pictures and queues the video for
          Review, one after another. It runs on the server, so you can close this page. It stops by
          itself if Codex runs out of drawings.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!busy && (
          <>
            <div className="space-y-2">
              <Label htmlFor="list-batch-count">How many videos?</Label>
              <div className="flex flex-wrap items-center gap-3">
                <div className="relative">
                  <Hash className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                  <Input
                    id="list-batch-count"
                    type="number"
                    min={1}
                    max={MAX}
                    value={count}
                    onChange={(e) => setCount(Math.max(1, Math.min(MAX, Number(e.target.value) || 1)))}
                    className="w-28 pl-9"
                  />
                </div>
                <div className="flex flex-wrap gap-1">
                  {[1, 3, 5, 10].map((n) => (
                    <Button
                      key={n}
                      variant={count === n ? "default" : "outline"}
                      size="sm"
                      onClick={() => setCount(n)}
                    >
                      {n}
                    </Button>
                  ))}
                </div>
              </div>
              <p className="text-xs text-muted-foreground">
                About 5 minutes each to write and draw, then they render one at a time. Each video
                uses about 12 Codex drawings.
              </p>
            </div>
            <Button
              className="w-full"
              size="lg"
              disabled={start.isPending || !channel || paused}
              onClick={() => start.mutate()}
            >
              {start.isPending ? <Spinner /> : <Sparkles className="size-4" />}
              {paused ? "Generation is paused" : `Make ${count} video${count > 1 ? "s" : ""}`}
            </Button>
            {start.isError && (
              <p className="text-sm text-destructive">{errMsg(start.error, "Couldn't start the batch.")}</p>
            )}
          </>
        )}

        {busy && batch && (
          <div className="space-y-3">
            <div className="flex items-center justify-between gap-3 text-sm">
              <span className="flex items-center gap-2">
                <Spinner /> {batch.step ?? "Working"}
              </span>
              <span className="text-muted-foreground">
                {made} / {batch.requested} made
              </span>
            </div>
            <Progress value={pct} />
            <Button
              variant="outline"
              className="w-full"
              disabled={stop.isPending || batch.state === "stopping"}
              onClick={() => stop.mutate()}
            >
              {stop.isPending || batch.state === "stopping" ? <Spinner /> : <Square className="size-4" />}
              {batch.state === "stopping" ? "Stopping…" : "Stop the batch"}
            </Button>
          </div>
        )}

        {finished && batch?.message && (
          <p
            className={`flex items-start gap-2 text-sm ${
              batch.state === "finished" ? "text-success" : "text-amber-400"
            }`}
          >
            {batch.state !== "finished" && <TriangleAlert className="mt-0.5 size-4 shrink-0" />}
            {batch.message}
          </p>
        )}

        {batch && batch.videos.length > 0 && (
          <div className="space-y-2 rounded-md border border-border bg-secondary/40 p-3">
            <p className="text-sm font-medium">
              {busy ? "Made so far" : "Made in this batch"} ({batch.videos.length}):
            </p>
            <ul className="space-y-1">
              {batch.videos.map((v) => (
                <li key={v.jobId} className="flex items-center gap-2 text-sm text-muted-foreground">
                  <Badge variant="outline">{v.jobId}</Badge>
                  <span className="min-w-0 truncate">{v.title}</span>
                  {v.source && (
                    <a href={v.source} target="_blank" rel="noreferrer" title="News source - check it before approving">
                      <Badge variant="default" className="gap-1">
                        <Newspaper className="size-3" /> news
                      </Badge>
                    </a>
                  )}
                  {v.droppedLines > 0 && (
                    <Badge variant="warning">{v.droppedLines} picture{v.droppedLines === 1 ? "" : "s"} missing</Badge>
                  )}
                </li>
              ))}
            </ul>
            <p className="text-xs text-muted-foreground">
              They land in the <span className="text-foreground">Review</span> tab as each one finishes
              rendering.
            </p>
          </div>
        )}

        {batch && batch.errors.length > 0 && (
          <details className="text-xs text-muted-foreground">
            <summary className="cursor-pointer">
              {batch.errors.length} attempt{batch.errors.length === 1 ? "" : "s"} didn't make a video
            </summary>
            <ul className="mt-1 list-disc space-y-1 pl-5">
              {batch.errors.map((e, i) => (
                <li key={i}>{e}</li>
              ))}
            </ul>
          </details>
        )}
      </CardContent>
    </Card>
  );
}
