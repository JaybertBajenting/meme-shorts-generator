import type { Job, UploadState } from "./api";
import { Badge } from "@/components/ui/badge";

/**
 * Every destination this job has actually been sent to. "none" means the platform isn't
 * configured (or wasn't attempted), so it's left out of the rollup entirely.
 */
export const destinationStates = (job: Job): UploadState[] =>
  [job.upload, job.facebook.state, job.instagram.state].filter((s) => s !== "none");

/** A single badge describing where a job is in its lifecycle (state -> review -> destinations). */
export function StatusBadge({ job }: { job: Job }) {
  if (job.state === "failed") return <Badge variant="destructive">failed</Badge>;
  if (job.state === "queued") return <Badge variant="outline">queued</Badge>;
  if (job.state === "running") return <Badge variant="warning">generating</Badge>;

  // state === done — roll every destination up into one word.
  const dests = destinationStates(job);
  if (dests.includes("uploading")) return <Badge variant="warning">uploading</Badge>;
  if (dests.length > 0) {
    const failed = dests.filter((s) => s === "failed").length;
    if (failed === dests.length) return <Badge variant="destructive">failed</Badge>;
    if (failed > 0) return <Badge variant="warning">partly posted</Badge>;
    if (dests.includes("scheduled")) return <Badge variant="success">scheduled</Badge>;
    return <Badge variant="success">uploaded</Badge>;
  }

  if (job.review === "rejected") return <Badge variant="destructive">rejected</Badge>;
  if (job.review === "approved") return <Badge variant="success">approved</Badge>;
  if (job.review === "pending") return <Badge variant="default">needs review</Badge>;
  return <Badge variant="secondary">ready</Badge>;
}

export function formatWhen(iso?: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleString(undefined, {
    weekday: "short",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  });
}

/** Jobs finished and waiting for a human decision. */
export const isPendingReview = (j: Job) =>
  j.state === "done" && j.hasVideo && j.review === "pending";
