---
name: remotion-render-memory
description: "Remotion renders on this machine fail from memory pressure, not code bugs — check free RAM and orphaned renderer processes before debugging the composition."
metadata: 
  node_type: memory
  type: project
  originSessionId: d67c83dc-2bfe-406a-80da-eef6a94772eb
  modified: 2026-08-17T02:34:25.396Z
---

**CHECK FREE DISK FIRST.** On 2026-08-17 `C:` was down to 0.25 GB, Windows could not grow its
paging file, and allocations failed system-wide — surfacing as compositor OOM, `x264 malloc of
size … failed`, and "Chrome ran out of memory or disk space". RAM was fine at 4.8 GB. Three
rounds went into tuning concurrency and caches before the disk was checked. The tell: PowerShell
itself started failing with "the paging file is too small". Renders now fall back to the project
drive for scratch when the system drive is under 3 GB.

Renders on Matthew's machine (16 GB, Brave routinely holding ~4 GB) also fail for genuine
memory reasons that LOOK like composition bugs. Before debugging the React component, check:

1. **Free RAM** — `(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory`. Under ~4 GB free,
   renders start failing.
2. **Orphaned `remotion` processes** — `Get-Process remotion`. Each holds ~500 MB.

**The trap:** a failed render did not always take its Rust compositor down with it, so every
failure leaked ~500 MB and made the NEXT render likelier to fail. Retries then reproduced the
failure and it looked like the code. Confirmed on 2026-08-16: a job died at frame 306 with a
composition that had rendered fine minutes earlier; killing one 505 MB orphan recovered 1.3 GB
and the next render succeeded first try.

Fixed in `RemotionRenderer` — it snapshots live compositor PIDs before starting and reaps any
that appear and outlive the render, on both the success and failure paths, logging "Reaped N
orphaned renderer process(es)". Only processes it started are touched.

Two failure signatures worth telling apart:
- `memory allocation of N bytes failed` / exit 3221226505 → genuine OOM, backs off concurrency.
- `waiting for the page to render the React component at frame N failed: timeout` → usually the
  same memory starvation one step removed, NOT a hang in the component.

Also: drawing several full-size copies of the post image as overlapping layers (one crop per
panel) OOMs outright. Use one copy and cover what should not be visible yet.
See [[yosheryaps-repost-format]].

## render-tmp leaks, and a full disk reads as a broken video (2026-09-10)

`render-tmp` under the API's content root is set as TEMP/TMP for child processes, deliberately, to
keep Remotion's bundles and frames off C:. **Nothing ever swept it.** Remotion cleans up when a
render ENDS; when one is killed - OOM, out of disk, a restart - the folders simply stay. They had
been piling up since 2 August: **376 folders, 7.4GB**, which filled the D: drive to 0 bytes.

Every later render then died at its FIRST file copy (an 18MB `bg.mp4` into the webpack bundle),
which surfaced as "Remotion render failed after 3 attempt(s) (exit 1)" and read like a fault in
the video - "what is this man, why was there no video produced".

Two fixes, both in the repo now:

- **Startup sweep** in `Program.cs`: anything in `render-tmp` older than 6 hours is deleted,
  best-effort, skipping what will not delete. A live render's folder is far newer than the cutoff.
- **`RemotionRenderer` kept the wrong end of STDERR.** `Tail()` returns the LAST 1500 chars, but
  Node puts the diagnosis on the FIRST line ("ENOSPC: no space left on device, copyfile ...") and
  a dozen stack frames after it - so the cause was discarded and the noise kept. `Ends()` now
  keeps both ends, and `LooksLikeOutOfDisk` adds "THE DISK IS FULL - this is not a fault in the
  video", alongside the existing out-of-memory hint.

**Check free space on BOTH drives before believing a render failure.** C: sits around 3GB free and
D: was at zero; neither is comfortable for this pipeline.

