import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Check, X, Youtube, RefreshCw, Inbox, Zap, Trash2, Pause, Play, Facebook, Instagram, Mic,
  Maximize2, AlertTriangle, CloudUpload, CloudDownload, Cloud,
} from "lucide-react";
import { api, type Job, type PlatformStatus, videoUrl, thumbnailUrl, type Health } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";
import { Pagination, paginate } from "@/components/ui/pagination";
import { StatusBadge, isPendingReview, formatWhen, destinationStates } from "@/lib/status";
import { friendlyError } from "@/lib/errors";

const PENDING_PER_PAGE = 6;
const HISTORY_PER_PAGE = 8;
const AUTH_WARNING_DISMISSED_AT = "meme-shorts:youtube-auth-warning-dismissed-at";

export function ReviewView({ health }: { health?: Health }) {
  const qc = useQueryClient();
  const [authWarningDismissedAt, setAuthWarningDismissedAt] = React.useState(() => {
    const stored = window.localStorage.getItem(AUTH_WARNING_DISMISSED_AT);
    return stored ? Number(stored) || 0 : 0;
  });

  const dismissAuthWarning = React.useCallback(() => {
    const now = Date.now();
    window.localStorage.setItem(AUTH_WARNING_DISMISSED_AT, String(now));
    setAuthWarningDismissedAt(now);
  }, []);
  const { data: jobs, isLoading } = useQuery({
    queryKey: ["jobs"],
    queryFn: api.jobs,
    refetchInterval: 2000,
  });
  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: api.settings,
    refetchInterval: 10000,
  });

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["jobs"] });
    qc.invalidateQueries({ queryKey: ["calendar"] });
    qc.invalidateQueries({ queryKey: ["settings"] });
  };

  const approve = useMutation({ mutationFn: (id: string) => api.approve(id), onSuccess: refresh });
  const reject = useMutation({ mutationFn: (id: string) => api.reject(id), onSuccess: refresh });
  const retry = useMutation({ mutationFn: (id: string) => api.upload(id), onSuccess: refresh });
  const revoice = useMutation({ mutationFn: (id: string) => api.revoice(id), onSuccess: refresh });
  const reconnect = useMutation({
    mutationFn: () => api.authorize(),
    onSuccess: (result) => {
      if (result.ok) dismissAuthWarning();
      refresh();
    },
  });
  const acceptCheck = useMutation({ mutationFn: api.acceptCheck, onSuccess: refresh });
  const cancelUnfinished = useMutation({ mutationFn: api.cancelUnfinished, onSuccess: refresh });
  const pauseGeneration = useMutation({ mutationFn: api.pauseGeneration, onSuccess: refresh });
  const resumeGeneration = useMutation({ mutationFn: api.resumeGeneration, onSuccess: refresh });
  const setAuto = useMutation({
    mutationFn: (v: boolean) => api.setAutoApprove(v),
    onSuccess: refresh,
  });

  const list = jobs ?? [];
  const pending = list.filter(isPendingReview);
  const working = list.filter((j) => j.state === "queued" || j.state === "running" || j.state === "paused");

  // Failed jobs get their own section rather than being buried in History. A video that fails its
  // own check is the one most likely to need a look — the check's one-line reason rarely says
  // enough on its own — and it is kept watchable so the fault can be judged and fixed.
  const failed = list.filter((j) => j.state === "failed");
  const settled = list.filter(
    (j) =>
      !isPendingReview(j) &&
      j.state !== "queued" &&
      j.state !== "running" &&
      j.state !== "paused" &&
      j.state !== "failed",
  );

  const authErrorJobs = list.filter(
    (j) => j.upload === "failed" && /invalid_grant|expired|revoked|token/i.test(j.uploadError ?? ""),
  );
  const latestAuthErrorJobAt = Math.max(
    0,
    ...authErrorJobs.map((job) => Date.parse(job.createdAt) || 0),
  );
  const authError = latestAuthErrorJobAt > authWarningDismissedAt;

  const [pendingPage, setPendingPage] = React.useState(1);
  const [historyPage, setHistoryPage] = React.useState(1);
  const pend = paginate(pending, pendingPage, PENDING_PER_PAGE);
  const hist = paginate(settled, historyPage, HISTORY_PER_PAGE);

  const autoOn = settings?.autoApprove ?? false;
  const generationPaused = settings?.generationPaused ?? false;

  // Approving fans out to every configured destination, so the copy names them all.
  const destinations = [
    health?.uploadConfigured ? "YouTube" : null,
    health?.facebookConfigured ? "Facebook" : null,
    health?.instagramConfigured ? "Instagram" : null,
  ].filter((d): d is string => d !== null);

  if (isLoading)
    return (
      <div className="flex items-center gap-2 text-muted-foreground">
        <Spinner /> Loading jobs…
      </div>
    );

  return (
    <div className="min-w-0 space-y-8">
      {/* Auto-approve toggle */}
      <Card>
        <CardContent className="flex flex-wrap items-center justify-between gap-4 py-4">
          <div className="flex items-center gap-3">
            <div className="flex size-9 items-center justify-center rounded-lg bg-primary/15 text-primary">
              <Zap className="size-5" />
            </div>
            <div>
              <p className="text-sm font-medium">Auto-approve</p>
              <p className="text-xs text-muted-foreground">
                {autoOn
                  ? "On — new videos are approved & uploaded automatically."
                  : "Off — you approve each video before it uploads."}
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            {setAuto.isPending && <Spinner className="text-muted-foreground" />}
            <Switch
              checked={autoOn}
              disabled={setAuto.isPending}
              onCheckedChange={(v) => setAuto.mutate(v)}
            />
          </div>
        </CardContent>
      </Card>

      {authError && (
        <div className="relative flex flex-col gap-3 rounded-lg border border-amber-500/40 bg-amber-500/10 p-4 pr-12 sm:flex-row sm:items-center sm:justify-between">
          <div className="min-w-0 text-sm">
            <p className="font-medium text-amber-300">YouTube sign-in expired</p>
            <p className="text-amber-200/80">
              An upload failed because the saved Google token was revoked/expired. Reconnect, then
              retry the upload.
            </p>
          </div>
          <Button
            variant="outline"
            className="shrink-0 border-amber-500/50"
            disabled={reconnect.isPending}
            onClick={() => reconnect.mutate()}
          >
            {reconnect.isPending ? <Spinner /> : <Youtube className="size-4" />}
            {reconnect.isPending ? "Waiting for consent…" : "Reconnect YouTube"}
          </Button>
          <Button
            variant="ghost"
            size="icon"
            className="absolute right-2 top-2"
            onClick={dismissAuthWarning}
            title="Dismiss resolved sign-in warning"
            aria-label="Dismiss resolved sign-in warning"
          >
            <X className="size-4" />
          </Button>
        </div>
      )}

      {/* Needs review — card grid */}
      <section className="space-y-4">
        <SectionTitle icon={<Inbox className="size-4" />} title="Needs your review" count={pending.length} />
        {pending.length === 0 ? (
          <EmptyHint
            text={
              autoOn
                ? "Auto-approve is on, so finished videos upload automatically — nothing to review."
                : "Nothing waiting. Generated videos show up here to approve or reject."
            }
          />
        ) : (
          <>
            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
              {pend.slice.map((job) => (
                <ReviewCard
                  key={job.id}
                  job={job}
                  destinations={destinations}
                  busy={
                    (approve.isPending && approve.variables === job.id) ||
                    (reject.isPending && reject.variables === job.id)
                  }
                  onApprove={() => approve.mutate(job.id)}
                  onReject={() => reject.mutate(job.id)}
                  onRevoice={() => revoice.mutate(job.id)}
                  revoicing={revoice.isPending && revoice.variables === job.id}
                />
              ))}
            </div>
            <Pagination page={pend.safePage} pageCount={pend.pageCount} onPage={setPendingPage} />
          </>
        )}
      </section>

      {/* Generating */}
      {working.length > 0 && (
        <section className="space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <SectionTitle title={generationPaused ? "Paused" : "Generating"} count={working.length} />
            <div className="flex items-center gap-2">
              {generationPaused ? (
                <Button size="sm" disabled={resumeGeneration.isPending} onClick={() => resumeGeneration.mutate()}>
                  {resumeGeneration.isPending ? <Spinner /> : <Play className="size-4" />} Resume all
                </Button>
              ) : (
                <Button variant="outline" size="sm" disabled={pauseGeneration.isPending} onClick={() => pauseGeneration.mutate()}>
                  {pauseGeneration.isPending ? <Spinner /> : <Pause className="size-4" />} Pause all
                </Button>
              )}
              <Button
                variant="destructive"
                size="sm"
                disabled={cancelUnfinished.isPending}
                onClick={() => {
                  if (window.confirm(`Cancel and delete all ${working.length} unfinished job${working.length === 1 ? "" : "s"}?`))
                    cancelUnfinished.mutate();
                }}
              >
                {cancelUnfinished.isPending ? <Spinner /> : <Trash2 className="size-4" />} Cancel all
              </Button>
            </div>
          </div>
          <div className="grid gap-3">
            {working.map((job) => (
              <Card key={job.id}>
                <CardContent className="flex items-center gap-4 py-4">
                  <Badge variant="outline" className="shrink-0">{job.id}</Badge>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-baseline justify-between gap-3">
                      <p className="truncate text-sm font-medium">{job.youTubeTitle || job.title || job.topic}</p>
                      <span className="shrink-0 text-sm font-semibold tabular-nums text-primary">
                        {job.percent}%
                      </span>
                    </div>
                    <div className="mt-2 flex items-center gap-3">
                      <Progress value={job.percent} className="flex-1" />
                      <span className="shrink-0 truncate text-xs text-muted-foreground">{job.stage}</span>
                    </div>
                  </div>
                </CardContent>
              </Card>
            ))}
          </div>
        </section>
      )}

      {/* Failed — kept out of History so a broken video is never mixed in with the published ones */}
      {failed.length > 0 && (
        <section className="space-y-4">
          <SectionTitle
            icon={<AlertTriangle className="size-4 text-destructive" />}
            title="Failed"
            count={failed.length}
          />
          <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
            {failed.map((job) => (
              <FailedCard
                key={job.id}
                job={job}
                busy={
                  (revoice.isPending && revoice.variables === job.id) ||
                  (acceptCheck.isPending && acceptCheck.variables === job.id)
                }
                onRevoice={() => revoice.mutate(job.id)}
                onAccept={() => acceptCheck.mutate(job.id)}
              />
            ))}
          </div>
        </section>
      )}

      {/* History */}
      <section className="space-y-4">
        <div className="flex items-center justify-between">
          <SectionTitle title="History" count={settled.length} />
          <Button variant="ghost" size="sm" onClick={refresh}>
            <RefreshCw className="size-4" /> Refresh
          </Button>
        </div>
        {settled.length === 0 ? (
          <EmptyHint text="No finished jobs yet." />
        ) : (
          <>
            <div className="grid gap-2">
              {hist.slice.map((job) => (
                <HistoryRow
                  key={job.id}
                  job={job}
                  busy={retry.isPending && retry.variables === job.id}
                  onRetry={() => retry.mutate(job.id)}
                />
              ))}
            </div>
            <Pagination page={hist.safePage} pageCount={hist.pageCount} onPage={setHistoryPage} />
          </>
        )}
      </section>
    </div>
  );
}

function ReviewCard({
  job,
  destinations,
  busy,
  onApprove,
  onReject,
  onRevoice,
  revoicing,
}: {
  job: Job;
  destinations: string[];
  busy: boolean;
  onApprove: () => void;
  onReject: () => void;
  onRevoice: () => void;
  revoicing: boolean;
}) {
  const canUpload = destinations.length > 0;
  const destinationList = destinations.join(" + ");
  const videoRef = React.useRef<HTMLVideoElement>(null);

  const goFullscreen = () => {
    const el = videoRef.current;
    if (!el) return;
    // webkitEnterFullscreen is the iOS Safari path — it only exists on the video element and
    // is the only thing that works there, since iOS has no Element.requestFullscreen.
    const anyEl = el as HTMLVideoElement & { webkitEnterFullscreen?: () => void };
    if (el.requestFullscreen) void el.requestFullscreen();
    else anyEl.webkitEnterFullscreen?.();
  };

  return (
    <Card className="flex flex-col overflow-hidden">
      <div className="group relative flex justify-center bg-black">
        <video
          ref={videoRef}
          className="aspect-[9/16] max-h-[340px] w-auto object-contain"
          src={videoUrl(job.id)}
          poster={job.hasThumbnail ? thumbnailUrl(job.id) : undefined}
          controls
          playsInline
          preload="metadata"
          onDoubleClick={goFullscreen}
        />
        {/* Top-right so it clears the native control bar along the bottom. */}
        <button
          type="button"
          onClick={goFullscreen}
          title="Full screen (or double-click the video)"
          aria-label="Full screen"
          className="absolute right-2 top-2 rounded-md bg-black/60 p-2 text-white opacity-0 transition-opacity hover:bg-black/80 focus-visible:opacity-100 group-hover:opacity-100"
        >
          <Maximize2 className="size-4" />
        </button>
      </div>
      <CardContent className="flex flex-1 flex-col gap-3 pt-4">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold" title={job.youTubeTitle ?? undefined}>
            {job.youTubeTitle || job.title || job.topic}
          </p>
          <p className="truncate text-xs text-muted-foreground">{job.topic}</p>
        </div>

        {/* Sent here despite its own check — say so, so approving it stays a deliberate choice. */}
        {job.checkOverride && (
          <p className="flex gap-1.5 rounded border border-destructive/40 bg-destructive/10 p-2 text-[11px] leading-snug text-muted-foreground">
            <AlertTriangle className="mt-px size-3.5 shrink-0 text-destructive" />
            <span className="whitespace-pre-line break-words">
              You passed this despite its check:{"\n"}
              {job.checkOverride.replace(/^The finished video did not pass.*?:\n/s, "")}
            </span>
          </p>
        )}

        {(job.youTubeDescription || (job.youTubeTags && job.youTubeTags.length > 0)) && (
          <details className="group text-xs">
            <summary className="cursor-pointer list-none text-muted-foreground hover:text-foreground">
              <span className="inline-flex items-center gap-1">
                <Youtube className="size-3.5" /> YouTube details
              </span>
            </summary>
            <div className="mt-2 space-y-2">
              {job.youTubeDescription && (
                <pre className="max-h-24 overflow-auto whitespace-pre-wrap break-words rounded bg-secondary/40 p-2 text-[11px] leading-snug text-foreground/90">
                  {job.youTubeDescription}
                </pre>
              )}
              {job.youTubeTags && job.youTubeTags.length > 0 && (
                <div className="flex flex-wrap gap-1">
                  {job.youTubeTags.map((t) => (
                    <Badge key={t} variant="secondary" className="text-[10px]">
                      {t}
                    </Badge>
                  ))}
                </div>
              )}
            </div>
          </details>
        )}

        <div className="mt-auto flex gap-2 pt-1">
          <Button
            variant="success"
            className="flex-1"
            disabled={busy || !canUpload}
            onClick={onApprove}
            title={canUpload ? `Approve → post to ${destinationList}` : "No upload destination configured"}
          >
            {busy ? <Spinner /> : <Check className="size-4" />} Approve
          </Button>
          <Button variant="destructive" className="flex-1" disabled={busy} onClick={onReject}>
            <X className="size-4" /> Reject
          </Button>
        </div>
        {/* Re-renders this short with whatever voice is currently set in Settings. Reuses the
            script and artwork, so it only costs voice + render. */}
        <Button
          variant="outline"
          size="sm"
          disabled={busy || revoicing}
          onClick={onRevoice}
          title="Re-render with the voice currently set in Settings"
        >
          {revoicing ? <Spinner /> : <Mic className="size-4" />} New voice
        </Button>
        {canUpload ? (
          <p className="text-[11px] text-muted-foreground">Posts to {destinationList}.</p>
        ) : (
          <p className="text-[11px] text-amber-400">
            Approving marks it approved, but no upload destination is configured yet.
          </p>
        )}
      </CardContent>
    </Card>
  );
}

/**
 * A job that failed, with the video it produced.
 *
 * The video is playable here even though it can never be published from this card: judging
 * whether a fault is real means watching it, and the check's reason on its own ("Rows 178..254
 * are drawn by two different slices") does not say which picture it happened to, or how bad it
 * looks. Re-render puts it back through the pipeline once the cause is fixed.
 */
function FailedCard({
  job,
  busy,
  onRevoice,
  onAccept,
}: {
  job: Job;
  busy: boolean;
  onRevoice: () => void;
  onAccept: () => void;
}) {
  // The check lists every fault on its own line; keep them all rather than collapsing to one.
  const reason = job.error || job.uploadError || "Failed with no reason recorded.";
  const videoRef = React.useRef<HTMLVideoElement>(null);

  // Same full-screen path as the review card: a fault worth judging is usually too small to see
  // in a card this size.
  const goFullscreen = () => {
    const el = videoRef.current;
    if (!el) return;
    const anyEl = el as HTMLVideoElement & { webkitEnterFullscreen?: () => void };
    if (el.requestFullscreen) void el.requestFullscreen();
    else anyEl.webkitEnterFullscreen?.();
  };

  return (
    <Card className="flex flex-col overflow-hidden border-destructive/40">
      {job.hasVideo ? (
        <div className="group relative flex justify-center bg-black">
          <video
            ref={videoRef}
            className="aspect-[9/16] max-h-[340px] w-auto object-contain"
            src={videoUrl(job.id)}
            poster={job.hasThumbnail ? thumbnailUrl(job.id) : undefined}
            controls
            playsInline
            preload="metadata"
            onDoubleClick={goFullscreen}
          />
          <button
            type="button"
            onClick={goFullscreen}
            title="Full screen (or double-click the video)"
            aria-label="Full screen"
            className="absolute right-2 top-2 rounded-md bg-black/60 p-2 text-white opacity-0 transition-opacity hover:bg-black/80 focus-visible:opacity-100 group-hover:opacity-100"
          >
            <Maximize2 className="size-4" />
          </button>
        </div>
      ) : (
        <div className="flex aspect-[9/16] max-h-[180px] items-center justify-center bg-secondary text-xs text-muted-foreground">
          No video was produced
        </div>
      )}
      <CardContent className="flex flex-1 flex-col gap-3 pt-4">
        <div className="flex items-start justify-between gap-2">
          <p className="min-w-0 flex-1 truncate text-sm font-medium">
            {job.youTubeTitle || job.title || job.topic}
          </p>
          <Badge variant="outline" className="shrink-0">{job.id}</Badge>
        </div>
        <p className="whitespace-pre-line break-words text-xs text-muted-foreground">{reason}</p>
        <div className="mt-auto flex gap-2">
          <Button variant="outline" size="sm" className="flex-1" disabled={busy} onClick={onRevoice}>
            {busy ? <Spinner /> : <RefreshCw className="size-4" />} Re-render
          </Button>
          {/* The check errs towards failing. Watching it is the last word — but this only sends
              it to the review queue; approving it is still a separate, deliberate press. */}
          {job.hasVideo && (
            <Button size="sm" className="flex-1" disabled={busy} onClick={onAccept}>
              {busy ? <Spinner /> : <Check className="size-4" />} Looks fine
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function HistoryRow({ job, busy, onRetry }: { job: Job; busy: boolean; onRetry: () => void }) {
  const dests = destinationStates(job);
  const rawError =
    job.error || job.uploadError || job.facebook.error || job.instagram.error || undefined;

  // A live/scheduled post is the useful headline; only fall back to an error once nothing landed.
  const anyLanded = dests.some((s) => s === "uploaded" || s === "scheduled");
  const subtitle = anyLanded
    ? `Publishes ${formatWhen(job.publishAt)}`
    : rawError
      ? friendlyError(rawError)
      : job.stage;

  // Retry re-runs only the destinations that didn't succeed — never duplicates a live post.
  const canRetry = dests.includes("failed") && job.review !== "rejected";

  return (
    <Card>
      <CardContent className="flex items-center gap-3 py-3">
        {job.hasThumbnail ? (
          <img src={thumbnailUrl(job.id)} alt="" className="h-9 w-16 shrink-0 rounded object-cover" />
        ) : (
          <div className="h-9 w-16 shrink-0 rounded bg-secondary" />
        )}
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-medium">{job.youTubeTitle || job.title || job.topic}</p>
          <p className="truncate text-xs text-muted-foreground" title={rawError}>
            {subtitle}
          </p>
        </div>
        {canRetry && (
          <Button variant="outline" size="sm" className="shrink-0" disabled={busy} onClick={onRetry}>
            {busy ? <Spinner /> : <RefreshCw className="size-4" />} Retry
          </Button>
        )}
        {job.youTubeVideoId && (
          <a href={`https://youtu.be/${job.youTubeVideoId}`} target="_blank" rel="noreferrer" className="shrink-0">
            <Button variant="ghost" size="icon" title="Watch on YouTube">
              <Youtube className="size-4" />
            </Button>
          </a>
        )}
        <PlatformLink platform={job.facebook} label="Facebook" icon={<Facebook className="size-4" />} />
        <PlatformLink platform={job.instagram} label="Instagram" icon={<Instagram className="size-4" />} />
        <DriveControl job={job} />
        <div className="shrink-0">
          <StatusBadge job={job} />
        </div>
      </CardContent>
    </Card>
  );
}

/**
 * Where a finished video's files are: a transfer in progress, "In Drive" with Bring back, or a
 * Move to Drive button while the files are still here. The server decides whether a move is
 * allowed (never while a destination still needs the file) and says why not.
 */
function DriveControl({ job }: { job: Job }) {
  const qc = useQueryClient();
  const [error, setError] = React.useState<string | null>(null);
  const onDone = {
    onSuccess: () => {
      setError(null);
      qc.invalidateQueries({ queryKey: ["jobs"] });
      qc.invalidateQueries({ queryKey: ["drive"] });
    },
    onError: (e: unknown) =>
      setError((e as { response?: { data?: { error?: string } } })?.response?.data?.error ?? "Couldn't start it."),
  };
  const move = useMutation({ mutationFn: () => api.moveToDrive(job.id), ...onDone });
  const bringBack = useMutation({ mutationFn: () => api.bringBackFromDrive(job.id), ...onDone });
  const problem = error ?? job.driveError ?? null;

  if (job.driveTransfer)
    return (
      <Badge variant="secondary" className="shrink-0 gap-1">
        <Spinner /> {job.driveTransfer === "moving" ? "Moving to Drive…" : "Bringing back…"}
      </Badge>
    );

  if (job.driveRemote)
    return (
      <div className="flex shrink-0 items-center gap-1">
        <Badge variant="default" className="gap-1" title={`${job.driveRemote} · frees ${formatMb(job.driveBytes)} here`}>
          <Cloud className="size-3" /> In Drive
        </Badge>
        <Button
          variant="ghost"
          size="icon"
          title={problem ? `Bring back failed: ${problem}` : "Bring back to this PC"}
          disabled={bringBack.isPending}
          onClick={() => bringBack.mutate()}
        >
          {bringBack.isPending ? <Spinner /> : <CloudDownload className={`size-4 ${problem ? "text-amber-400" : ""}`} />}
        </Button>
      </div>
    );

  if (!job.hasVideo) return null;
  return (
    <Button
      variant="ghost"
      size="icon"
      className="shrink-0"
      title={problem ? `Last move failed (nothing was deleted): ${problem}` : "Move to Google Drive"}
      disabled={move.isPending}
      onClick={() => {
        if (
          window.confirm(
            "Move this video to Google Drive?\n\nIt's uploaded and checked file by file first; only then are its files removed from this PC. Bring back copies it home again.",
          )
        )
          move.mutate();
      }}
    >
      {move.isPending ? <Spinner /> : <CloudUpload className={`size-4 ${problem ? "text-amber-400" : ""}`} />}
    </Button>
  );
}

function formatMb(bytes: number): string {
  return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GB` : `${Math.round(bytes / 1024 ** 2)} MB`;
}

/**
 * One destination's icon in a history row: a link once the post is live, a plain dimmed icon
 * while it's waiting for its slot, and nothing at all when the platform isn't configured.
 */
function PlatformLink({
  platform,
  label,
  icon,
}: {
  platform: PlatformStatus;
  label: string;
  icon: React.ReactNode;
}) {
  if (platform.state === "none") return null;

  if (platform.state === "uploaded" && platform.url)
    return (
      <a href={platform.url} target="_blank" rel="noreferrer" className="shrink-0">
        <Button variant="ghost" size="icon" title={`View on ${label}`}>
          {icon}
        </Button>
      </a>
    );

  const title =
    platform.state === "scheduled"
      ? `${label}: publishes ${formatWhen(platform.publishAt)}`
      : platform.state === "failed"
        ? `${label}: ${friendlyError(platform.error ?? "failed")}`
        : `${label}: ${platform.state}`;

  return (
    <span
      className={`shrink-0 px-2 ${platform.state === "failed" ? "text-destructive" : "text-muted-foreground"}`}
      title={title}
    >
      {icon}
    </span>
  );
}

function SectionTitle({
  title,
  count,
  icon,
}: {
  title: string;
  count: number;
  icon?: React.ReactNode;
}) {
  return (
    <h2 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-muted-foreground">
      {icon}
      {title}
      <Badge variant="secondary">{count}</Badge>
    </h2>
  );
}

function EmptyHint({ text }: { text: string }) {
  return (
    <div className="rounded-lg border border-dashed border-border p-8 text-center text-sm text-muted-foreground">
      {text}
    </div>
  );
}
