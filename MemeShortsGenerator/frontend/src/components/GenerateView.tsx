import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Sparkles, Wand2, Send, Hash, Play, ImagePlus, Layers, ScanText, TrendingUp, X,
} from "lucide-react";
import { api, type Channel, type Health } from "@/lib/api";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select } from "@/components/ui/select";
import { Spinner } from "@/components/ui/spinner";
import { Badge } from "@/components/ui/badge";

const FORMATS = [
  { value: "", label: "Auto (let Codex choose)" },
  { value: "single_visual_reveal", label: "Single visual reveal" },
  { value: "relatable_chaos", label: "Relatable chaos" },
  { value: "relatable_list", label: "Relatable list" },
  { value: "absurd_debate", label: "Absurd debate" },
  { value: "chart_gag", label: "Chart / diagram gag" },
  { value: "escalating_corrections", label: "Escalating corrections" },
  { value: "internal_monologue", label: "Internal monologue" },
];

/** What each mode actually does, in one line, so the choice is readable without clicking it. */
const MODE_COPY: Record<string, { icon: typeof Wand2; blurb: string }> = {
  meme: { icon: Wand2, blurb: "Codex writes the script and draws the artwork from a topic." },
  repost: { icon: ImagePlus, blurb: "You paste a screenshot. It gets read out word for word." },
  finance: { icon: TrendingUp, blurb: "Codex writes it, rendered with data visuals." },
  list: { icon: Layers, blurb: "A run of pictures, one line of script each, read over gameplay." },
};

/**
 * Pull the server's error message out of an axios error, falling back to a plain Error's own
 * text before the generic line.
 *
 * Batch submits catch each picture's failure and rethrow it as a plain Error, which has no
 * axios `response` â€” so a perfectly clear "Already made a video from â€¦" was being reported as
 * "Is the API running?", sending everyone to check a server that was fine.
 */
function errMsg(e: unknown, fallback: string): string {
  const anyE = e as { response?: { data?: { error?: string } }; message?: string };
  return anyE?.response?.data?.error ?? (anyE?.message || undefined) ?? fallback;
}

export function GenerateView({ health }: { health?: Health }) {
  const qc = useQueryClient();
  const { data: channels } = useQuery({ queryKey: ["channels"], queryFn: api.channels });
  const { data: settings } = useQuery({ queryKey: ["settings"], queryFn: api.settings });

  const [channel, setChannel] = React.useState<string>("");
  const [format, setFormat] = React.useState<string>("");
  const [topic, setTopic] = React.useState<string>("");
  const [count, setCount] = React.useState<number>(3);
  const [lastBatch, setLastBatch] = React.useState<{ id: string; topic: string }[] | null>(null);

  // Repost channels take a post you found instead of a topic â€” no Codex, no image generation.
  const [postText, setPostText] = React.useState<string>("");
  const [postTitle, setPostTitle] = React.useState<string>("");
  // A STAGING LIST, not one picture. Screenshots get collected a few at a time, and each one
  // becomes its own video â€” so pasting a second must add to the pile rather than replace it.
  const [postImages, setPostImages] = React.useState<File[]>([]);
  const [postPreviews, setPostPreviews] = React.useState<string[]>([]);
  const [dragging, setDragging] = React.useState(false);
  const [batchDone, setBatchDone] = React.useState<number | null>(null);
  const imageInput = React.useRef<HTMLInputElement>(null);

  // Typed text and a typed title belong to ONE post. With several staged there is no way to
  // say which they apply to, so a batch is read off each picture instead.
  const isBatch = postImages.length > 1;

  // Open on the mode last used. Switching tabs unmounts this view, so without a persisted
  // value every trip back to Generate reset the picker to the first channel.
  React.useEffect(() => {
    if (channel || !channels || channels.length === 0) return;
    const saved = settings?.channel;
    setChannel(saved && channels.some((c: Channel) => c.id === saved) ? saved : channels[0].id);
  }, [channels, channel, settings?.channel]);

  // Invalidate settings on save: the view reads the remembered channel from react-query's
  // cache when it remounts, so without this the cache still holds the pre-save value and
  // coming back to the tab silently reverted to the first channel.
  const rememberChannel = useMutation({
    mutationFn: api.setChannel,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["settings"] }),
  });

  const pickChannel = (id: string) => {
    setChannel(id);
    rememberChannel.mutate(id);
  };

  const contentType = (channels ?? []).find((c: Channel) => c.id === channel)?.contentType;
  const isRepost = contentType === "repost";
  /** List shorts stage pictures the same way, but pair each one with its own line of script. */
  const isList = contentType === "list";
  const usesPictures = isRepost || isList;

  // One line of script per picture â€” the format pairs them, so a mismatch is worth catching
  // in the form rather than as a server error after the upload.
  const [listLines, setListLines] = React.useState<string>("");
  const scriptLines = listLines
    .replace(/\r\n/g, "\n")
    .split("\n")
    .map((l) => l.trim())
    .filter((l) => l.length > 0);

  // Ctrl+V anywhere on the page. Screenshotting a post and pasting it is the whole workflow â€”
  // routing that through a file picker would mean saving to disk first for no reason.
  React.useEffect(() => {
    if (!usesPictures) return;
    const onPaste = (e: ClipboardEvent) => {
      const target = e.target as HTMLElement | null;
      // Don't steal a paste meant for the post-text or title boxes.
      if (target && (target.tagName === "TEXTAREA" || target.tagName === "INPUT")) return;
      const files = Array.from(e.clipboardData?.items ?? [])
        .filter((i) => i.type.startsWith("image/"))
        .map((i) => i.getAsFile())
        .filter((f): f is File => f !== null);
      if (files.length === 0) return;
      e.preventDefault();
      addImages(files);
    };
    window.addEventListener("paste", onPaste);
    return () => window.removeEventListener("paste", onPaste);
  }, [usesPictures]);

  // Object URLs leak until revoked, so the previews are tied to the files' lifetime.
  React.useEffect(() => {
    const urls = postImages.map((f) => URL.createObjectURL(f));
    setPostPreviews(urls);
    return () => urls.forEach((u) => URL.revokeObjectURL(u));
  }, [postImages]);

  // Read the words off the picture as soon as it lands, so they don't have to be retyped.
  // The result is a DRAFT â€” it fills the box for correction, and never overwrites typing
  // already in progress.
  const ocr = useMutation({
    mutationFn: (file: File) => api.ocr(file),
    onSuccess: (res) => {
      if (res.text && !postText.trim()) setPostText(res.text);
    },
  });

  const autofill = settings?.ocrAutofill ?? false;

  // The paste listener is registered once (its effect only depends on isRepost), so it closes
  // over whatever `autofill` was at subscribe time. A ref keeps the handler reading the CURRENT
  // value â€” without it, flipping the toggle had no effect until the view remounted.
  const autofillRef = React.useRef(autofill);
  React.useEffect(() => {
    autofillRef.current = autofill;
  }, [autofill]);

  const setAutofill = useMutation({
    mutationFn: api.setOcrAutofill,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["settings"] }),
  });

  /** Reset the file input too, or re-picking the SAME file after removing it fires no change
   *  event and appears to do nothing. */
  const resetFileInput = () => {
    if (imageInput.current) imageInput.current.value = "";
  };

  const clearImages = () => {
    setPostImages([]);
    resetFileInput();
  };

  const removeImageAt = (i: number) => {
    setPostImages((prev) => prev.filter((_, n) => n !== i));
    resetFileInput();
  };

  const addImages = (files: File[]) => {
    if (files.length === 0) return;
    setPostImages((prev) => {
      const next = [...prev, ...files];
      // Only prefill for a lone picture â€” the text box cannot describe a pile of them, and
      // whole-image OCR also picks up the username row, so it fills the box with junk to clear.
      if (next.length === 1 && autofillRef.current) ocr.mutate(next[0]);
      return next;
    });
    resetFileInput();
  };

  // Why "Make the video" is unavailable, in the user's terms. Null means it is ready to go.
  const blockedReason = settings?.generationPaused
    ? "Generation is paused â€” hit Resume generation at the top first."
    : ocr.isPending
      ? "Still reading the pictureâ€¦"
      : postImages.length === 0 && !postText.trim()
        // A picture alone is enough â€” its words are read off it. The text box is only the
        // correction path for when OCR gets them wrong.
        ? "Paste a picture, or type the post text."
        : null;

  // The list form's own gate. The counts have to match, so say WHICH way they are out rather
  // than letting the upload fail server-side after the pictures have gone up.
  const listBlockedReason = settings?.generationPaused
    ? "Generation is paused — hit Resume generation at the top first."
    : postImages.length === 0
      ? "Add the pictures, in the order they should play."
      : scriptLines.length === 0
        ? "Write one line of script per picture."
        : scriptLines.length !== postImages.length
          ? scriptLines.length < postImages.length
            ? `${postImages.length - scriptLines.length} picture(s) have no line yet.`
            : `${scriptLines.length - postImages.length} line(s) have no picture yet.`
          : null;

  const invalidate = () => {
    qc.invalidateQueries({ queryKey: ["jobs"] });
    qc.invalidateQueries({ queryKey: ["calendar"] });
  };

  // A staged draft: Codex's script with a picture found for each line, held for checking before
  // any rendering happens.
  type DraftItem = {
    text: string;
    term: string;
    file: string | null;
    credit: string | null;
    /** Commons page title — what identifies the picture, so a swap can avoid repeats. */
    source: string | null;
    url: string | null;
  };
  const [draft, setDraft] = React.useState<{ id: string; title: string; items: DraftItem[] } | null>(null);
  const [swapping, setSwapping] = React.useState<number | null>(null);

  const suggest = useMutation({
    mutationFn: () => api.listDraft({ topic: topic.trim() || null, channel: channel || null }),
    onSuccess: (d) => setDraft(d),
  });

  const confirmDraft = useMutation({
    mutationFn: () =>
      api.listDraftConfirm(draft!.id, {
        title: draft!.title,
        channel: channel || null,
        items: draft!.items.map((i) => ({ text: i.text, file: i.file, credit: i.credit })),
      }),
    onSuccess: () => {
      setDraft(null);
      setTopic("");
      invalidate();
    },
  });

  /** Re-search one line's picture with whatever term is in its box now. */
  const swapImage = async (index: number, term: string) => {
    if (!draft || !term.trim()) return;
    setSwapping(index);
    try {
      const res = await api.listDraftImage(draft.id, {
        index,
        term: term.trim(),
        // Everything the other lines are using, so the swap can't hand back a picture that is
        // already on screen somewhere else in this video.
        exclude: draft.items
          .filter((it, i) => i !== index && it.source)
          .map((it) => it.source!),
      });
      setDraft((d) =>
        d === null
          ? d
          : {
              ...d,
              items: d.items.map((it, i) =>
                i === index
                  ? {
                      ...it,
                      term,
                      file: res.file,
                      url: res.url ?? null,
                      credit: res.credit ?? null,
                      source: res.source ?? null,
                    }
                  : it,
              ),
            },
      );
    } finally {
      setSwapping(null);
    }
  };

  const listMutation = useMutation({
    mutationFn: () =>
      api.generateList({
        images: postImages,
        lines: scriptLines.join("\n"),
        title: postTitle.trim() || null,
        channel: channel || null,
      }),
    onSuccess: () => {
      setListLines("");
      setPostTitle("");
      clearImages();
      invalidate();
    },
  });

  const single = useMutation({
    mutationFn: () => api.generate({ topic: topic.trim(), format: format || null, channel: channel || null }),
    onSuccess: () => {
      setTopic("");
      invalidate();
    },
  });

  const auto = useMutation({
    mutationFn: () => api.generateAuto({ count, format: format || null, channel: channel || null }),
    onSuccess: (res) => {
      setLastBatch(res.jobs);
      invalidate();
    },
  });

  const repost = useMutation({
    // One job per staged picture, submitted one at a time. A failure on any single picture is
    // collected rather than thrown, so one unreadable screenshot cannot strand the rest of the
    // pile unqueued â€” the pipeline itself already runs the jobs serially from here.
    mutationFn: async () => {
      const images: (File | null)[] = postImages.length > 0 ? postImages : [null];
      const failures: string[] = [];
      let queued = 0;
      setBatchDone(0);

      for (const image of images) {
        try {
          await api.generateRepost({
            text: isBatch ? "" : postText.trim(),
            image,
            title: isBatch ? null : postTitle.trim() || null,
            channel: channel || null,
          });
          queued++;
        } catch (e) {
          failures.push(`${image?.name ?? "post"}: ${errMsg(e, "failed")}`);
        }
        setBatchDone((n) => (n ?? 0) + 1);
      }

      if (queued === 0) throw new Error(failures[0] ?? "Nothing could be queued.");
      return { queued, failures };
    },
    onSuccess: () => {
      setPostText("");
      setPostTitle("");
      clearImages();
      invalidate();
    },
    onSettled: () => setBatchDone(null),
  });

  const resumeGeneration = useMutation({
    mutationFn: api.resumeGeneration,
    onSuccess: () => qc.invalidateQueries({ queryKey: ["settings"] }),
  });

  return (
    <div className="grid gap-6 lg:grid-cols-2">
      {settings?.generationPaused && (
        <div className="flex flex-col gap-3 rounded-md border border-amber-500/40 bg-amber-500/10 p-4 sm:flex-row sm:items-center sm:justify-between lg:col-span-2">
          <div>
            <p className="text-sm font-medium text-amber-300">Generation paused</p>
            <p className="text-sm text-amber-200/80">
              New videos stay disabled until generation is resumed.
            </p>
          </div>
          <Button
            className="shrink-0"
            disabled={resumeGeneration.isPending}
            onClick={() => resumeGeneration.mutate()}
          >
            {resumeGeneration.isPending ? <Spinner /> : <Play className="size-4" />}
            Resume generation
          </Button>
        </div>
      )}
      {/* What kind of video â€” the FIRST decision, so it sits at the top. This used to live in
          the "Settings" card at the bottom of the page, where it was invisible: you had to
          scroll past both generator cards to find it, and the card's title made it look like
          it belonged to the Settings tab. */}
      <Card className="lg:col-span-2">
        <CardHeader className="pb-3">
          <CardTitle className="flex items-center gap-2 text-base">
            <Layers className="size-4 text-primary" /> What are you making?
          </CardTitle>
        </CardHeader>
        <CardContent>
          {/* Buttons, not a dropdown. Picking this changes the entire page â€” which form you get,
              whether Codex runs at all â€” and a collapsed <select> showed only the current value,
              so there was no way to tell the other modes existed. */}
          <div className="grid gap-3 sm:grid-cols-3">
            {(channels ?? []).map((c: Channel) => {
              const active = c.id === channel;
              const kind = MODE_COPY[c.contentType] ?? MODE_COPY.meme;
              const Icon = kind.icon;
              return (
                <button
                  key={c.id}
                  type="button"
                  onClick={() => pickChannel(c.id)}
                  aria-pressed={active}
                  className={`flex flex-col items-start gap-1 rounded-md border-2 p-3 text-left transition-colors ${
                    active
                      ? "border-primary bg-primary/10"
                      : "border-input bg-background/40 hover:border-primary/50"
                  }`}
                >
                  <span className="flex items-center gap-2 text-sm font-semibold">
                    <Icon className={`size-4 ${active ? "text-primary" : "text-muted-foreground"}`} />
                    {c.name}
                  </span>
                  <span className="text-xs leading-snug text-muted-foreground">{kind.blurb}</span>
                </button>
              );
            })}
          </div>
        </CardContent>
      </Card>

      {/* List short: pictures in order, one line of script each. Both halves are supplied, so
          like the repost form this replaces the topic and batch cards entirely. */}
      {isList && (
        <Card className="border-primary/30 lg:col-span-2">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Layers className="size-5 text-primary" /> Build a list short
            </CardTitle>
            <CardDescription>
              Pictures in order, one line of script each. Each picture is on screen while its line
              is read, over blurred gameplay with the words drawn on top.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <div className="flex items-center justify-between gap-2">
                <Label>Pictures — in order</Label>
                {postImages.length > 0 && (
                  <div className="flex items-center gap-3 text-xs">
                    <span
                      className={
                        scriptLines.length === postImages.length
                          ? "text-muted-foreground"
                          : "text-amber-300"
                      }
                    >
                      {postImages.length} picture{postImages.length === 1 ? "" : "s"} ·{" "}
                      {scriptLines.length} line{scriptLines.length === 1 ? "" : "s"}
                    </span>
                    <button
                      type="button"
                      onClick={clearImages}
                      className="text-muted-foreground underline-offset-2 hover:text-destructive hover:underline"
                    >
                      clear all
                    </button>
                  </div>
                )}
              </div>
              <div
                onDragOver={(e) => {
                  e.preventDefault();
                  setDragging(true);
                }}
                onDragLeave={() => setDragging(false)}
                onDrop={(e) => {
                  e.preventDefault();
                  setDragging(false);
                  addImages(Array.from(e.dataTransfer.files).filter((x) => x.type.startsWith("image/")));
                }}
                onClick={() => imageInput.current?.click()}
                className={`relative flex cursor-pointer flex-col items-center justify-center gap-3 rounded-md border-2 border-dashed p-6 text-center transition-colors ${
                  dragging ? "border-primary bg-primary/10" : "border-input bg-background/40 hover:border-primary/50"
                }`}
              >
                {postImages.length > 0 && (
                  <div className="grid w-full grid-cols-2 gap-2 sm:grid-cols-3 md:grid-cols-4">
                    {postImages.map((file, i) =>
                      postPreviews[i] ? (
                        <div key={`${file.name}-${file.lastModified}-${i}`} className="group relative">
                          <img
                            src={postPreviews[i]}
                            alt={file.name}
                            className="h-28 w-full rounded border border-border bg-background object-contain"
                          />
                          {/* The number is the running order, and the line it pairs with. */}
                          <span className="absolute left-1 top-1 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-medium text-white">
                            {i + 1}
                          </span>
                          <button
                            type="button"
                            aria-label={`Remove ${file.name}`}
                            onClick={(e) => {
                              e.stopPropagation();
                              removeImageAt(i);
                            }}
                            className="absolute right-1 top-1 rounded bg-black/70 p-1 text-white hover:bg-destructive"
                          >
                            <X className="size-3.5" />
                          </button>
                        </div>
                      ) : null,
                    )}
                  </div>
                )}
                <div className="flex flex-col items-center gap-1">
                  <ImagePlus className="size-7 text-muted-foreground" />
                  <p className="text-sm font-medium">
                    Press <kbd className="rounded bg-secondary px-1.5 py-0.5 text-xs">Ctrl</kbd>{" "}
                    <kbd className="rounded bg-secondary px-1.5 py-0.5 text-xs">V</kbd> to paste a picture
                  </p>
                  <p className="text-xs text-muted-foreground">
                    or drop them here, or click to browse — they play in the order shown
                  </p>
                </div>
              </div>
              <input
                ref={imageInput}
                type="file"
                multiple
                accept="image/png,image/jpeg,image/webp,image/gif"
                className="hidden"
                onChange={(e) => addImages(Array.from(e.target.files ?? []))}
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="list-lines">Script — one line per picture</Label>
              <textarea
                id="list-lines"
                rows={7}
                placeholder={"Alaska is apparently the coldest state.\nAccording to this poll, which state is the biggest?\nTexas! Now which one has the best beaches?"}
                value={listLines}
                onChange={(e) => setListLines(e.target.value)}
                className="flex w-full rounded-md border border-input bg-background/40 px-3 py-2 text-sm ring-offset-background placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
              <p className="text-xs text-muted-foreground">
                Line 1 is read over picture 1, line 2 over picture 2, and so on. Blank lines are
                ignored, so the counts have to match.
              </p>
            </div>

            <div className="space-y-2">
              <Label htmlFor="list-title">YouTube title (optional)</Label>
              <Input
                id="list-title"
                placeholder="Leave blank — Codex writes one from the script"
                value={postTitle}
                onChange={(e) => setPostTitle(e.target.value)}
              />
            </div>

            {/* Or skip all of the above: let Codex pick the subject and find the pictures. The
                draft is shown for checking first, because the picture search is only right
                about three times in five. */}
            <div className="space-y-3 rounded-md border border-dashed border-input p-4">
              <div className="flex items-center gap-2">
                <Sparkles className="size-4 text-primary" />
                <span className="text-sm font-medium">Or let Codex make one</span>
              </div>
              <div className="flex gap-2">
                <Input
                  placeholder="Subject (optional) — blank lets it choose"
                  value={topic}
                  onChange={(e) => setTopic(e.target.value)}
                />
                <Button
                  variant="secondary"
                  disabled={suggest.isPending}
                  onClick={() => suggest.mutate()}
                >
                  {suggest.isPending ? <Spinner /> : <Wand2 className="size-4" />}
                  {suggest.isPending ? "Writing…" : "Suggest"}
                </Button>
              </div>
              {suggest.isError && (
                <p className="text-sm text-destructive">
                  {errMsg(suggest.error, "Codex couldn't come up with one.")}
                </p>
              )}

              {draft && (
                <div className="space-y-3">
                  <div className="flex items-center justify-between gap-2">
                    <p className="text-sm font-semibold">{draft.title}</p>
                    <button
                      type="button"
                      onClick={() => setDraft(null)}
                      className="text-xs text-muted-foreground underline-offset-2 hover:text-destructive hover:underline"
                    >
                      discard
                    </button>
                  </div>

                  {draft.items.map((item, i) => (
                    <div key={i} className="flex gap-3 rounded-md bg-background/40 p-2">
                      <div className="w-28 shrink-0">
                        {item.url ? (
                          <div className="relative">
                            <img
                              src={item.url}
                              alt={item.term}
                              className="h-20 w-28 rounded border border-border object-cover"
                            />
                            {/* Say which pictures are DRAWN rather than found, so a generated
                                one is never mistaken for a photograph of a real thing. */}
                            {item.source?.startsWith("generated:") && (
                              <span className="absolute bottom-0 left-0 right-0 rounded-b bg-primary/85 px-1 py-0.5 text-center text-[9px] font-medium text-primary-foreground">
                                drawn
                              </span>
                            )}
                          </div>
                        ) : (
                          // Say WHICH line has no picture, and what will happen if it is left
                          // that way — the line is kept and the previous picture is held over.
                          <div className="flex h-20 w-28 items-center justify-center rounded border border-dashed border-amber-500/60 p-1 text-center text-[10px] leading-tight text-amber-300">
                            {i === 0 ? "no picture found" : "none — holds the one above"}
                          </div>
                        )}
                      </div>
                      <div className="min-w-0 flex-1 space-y-1">
                        <p className="text-sm">{item.text}</p>
                        <div className="flex gap-1">
                          <Input
                            value={item.term}
                            onChange={(e) =>
                              setDraft((d) =>
                                d === null
                                  ? d
                                  : {
                                      ...d,
                                      items: d.items.map((it, n) =>
                                        n === i ? { ...it, term: e.target.value } : it,
                                      ),
                                    },
                              )
                            }
                            className="h-8 text-xs"
                          />
                          <Button
                            size="sm"
                            variant="secondary"
                            className="h-8"
                            disabled={swapping === i}
                            onClick={() => swapImage(i, item.term)}
                          >
                            {swapping === i ? <Spinner /> : "Search"}
                          </Button>
                        </div>
                      </div>
                    </div>
                  ))}

                  <Button
                    className="w-full"
                    disabled={confirmDraft.isPending || draft.items.every((i) => !i.file)}
                    onClick={() => confirmDraft.mutate()}
                  >
                    {confirmDraft.isPending ? <Spinner /> : <Play className="size-4" />}
                    Make this video
                  </Button>
                  {confirmDraft.isError && (
                    <p className="text-sm text-destructive">
                      {errMsg(confirmDraft.error, "Couldn't queue it.")}
                    </p>
                  )}
                </div>
              )}
              {confirmDraft.isSuccess && !draft && (
                <p className="text-sm text-success">Queued — see the Review tab.</p>
              )}
            </div>

            <Button
              className="w-full"
              size="lg"
              disabled={Boolean(listBlockedReason) || listMutation.isPending}
              onClick={() => listMutation.mutate()}
            >
              {listMutation.isPending ? <Spinner /> : <Layers className="size-4" />}
              {listMutation.isPending ? "Uploading…" : "Make the video"}
            </Button>

            {listBlockedReason && !listMutation.isPending && (
              <p className="text-center text-sm text-amber-300">{listBlockedReason}</p>
            )}
            {listMutation.isSuccess && (
              <p className="text-sm text-success">Queued — see the Review tab.</p>
            )}
            {listMutation.isError && (
              <p className="text-sm text-destructive">
                {errMsg(listMutation.error, "Couldn't start it. Is the API running?")}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Repost: you supply the post. Replaces both Codex cards, because neither a topic nor a
          batch means anything here â€” the post IS the content. */}
      {isRepost && (
        <Card className="border-primary/30 lg:col-span-2">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <ImagePlus className="size-5 text-primary" /> Repost a post you found
            </CardTitle>
            <CardDescription>
              Drop in the picture and paste the words. The voice reads the text out verbatim and it
              appears one line at a time over the background â€” no Codex, no generated artwork.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <div className="flex items-center justify-between gap-2">
                <Label>Pictures</Label>
                {postImages.length > 0 && (
                  <div className="flex items-center gap-3 text-xs">
                    <span className="text-muted-foreground">
                      {postImages.length} queued â€” one video each
                    </span>
                    <button
                      type="button"
                      onClick={clearImages}
                      className="text-muted-foreground underline-offset-2 hover:text-destructive hover:underline"
                    >
                      clear all
                    </button>
                  </div>
                )}
              </div>
              <div
                onDragOver={(e) => {
                  e.preventDefault();
                  setDragging(true);
                }}
                onDragLeave={() => setDragging(false)}
                onDrop={(e) => {
                  e.preventDefault();
                  setDragging(false);
                  addImages(Array.from(e.dataTransfer.files).filter((x) => x.type.startsWith("image/")));
                }}
                onClick={() => imageInput.current?.click()}
                className={`relative flex cursor-pointer flex-col items-center justify-center gap-3 rounded-md border-2 border-dashed p-6 text-center transition-colors ${
                  dragging ? "border-primary bg-primary/10" : "border-input bg-background/40 hover:border-primary/50"
                }`}
              >
                {/* Zip against the previews rather than indexing them. The preview URLs are
                    derived in an effect, so they survive one render longer than the files do â€”
                    on a successful submit the files clear first, and reading .size off a stale
                    index there crashed the whole page to blank. */}
                {postImages.length > 0 && (
                  <div className="grid w-full grid-cols-2 gap-2 sm:grid-cols-3 md:grid-cols-4">
                    {postImages.map((file, i) =>
                      postPreviews[i] ? (
                        <div key={`${file.name}-${file.lastModified}-${i}`} className="group relative">
                          <img
                            src={postPreviews[i]}
                            alt={file.name}
                            className="h-28 w-full rounded border border-border bg-background object-contain"
                          />
                          <span className="absolute left-1 top-1 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-medium text-white">
                            {i + 1}
                          </span>
                          <button
                            type="button"
                            aria-label={`Remove ${file.name}`}
                            title="Remove"
                            // The dropzone opens the file browser on click, so this must not bubble.
                            onClick={(e) => {
                              e.stopPropagation();
                              removeImageAt(i);
                            }}
                            className="absolute right-1 top-1 rounded bg-black/70 p-1 text-white hover:bg-destructive"
                          >
                            <X className="size-3.5" />
                          </button>
                        </div>
                      ) : null,
                    )}
                  </div>
                )}
                <div className="flex flex-col items-center gap-1">
                  <ImagePlus className="size-7 text-muted-foreground" />
                  <p className="text-sm font-medium">
                    Press <kbd className="rounded bg-secondary px-1.5 py-0.5 text-xs">Ctrl</kbd>{" "}
                    <kbd className="rounded bg-secondary px-1.5 py-0.5 text-xs">V</kbd> to paste
                    {postImages.length > 0 ? " another screenshot" : " your screenshot"}
                  </p>
                  <p className="text-xs text-muted-foreground">
                    or drop them here, or click to browse â€” add as many as you like
                  </p>
                </div>
              </div>
              <input
                ref={imageInput}
                type="file"
                multiple
                accept="image/png,image/jpeg,image/webp,image/gif"
                className="hidden"
                onChange={(e) => addImages(Array.from(e.target.files ?? []))}
              />
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between gap-2">
                <Label htmlFor="post-text">Post text</Label>
                <div className="flex items-center gap-2">
                  {ocr.isPending && (
                    <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
                      <Spinner /> reading the pictureâ€¦
                    </span>
                  )}
                  {postImages.length === 1 && !ocr.isPending && (
                    <button
                      type="button"
                      onClick={() => ocr.mutate(postImages[0])}
                      className="flex items-center gap-1 text-xs text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
                    >
                      <ScanText className="size-3.5" /> read the words from the picture
                    </button>
                  )}
                </div>
              </div>
              <textarea
                id="post-text"
                rows={8}
                disabled={isBatch}
                placeholder={"Paste a picture above and its words land here automatically.\n\nOr type them yourself. Blank lines separate paragraphs â€” each one gets the wider gap, exactly like the original."}
                value={postText}
                onChange={(e) => setPostText(e.target.value)}
                className="flex w-full rounded-md border border-input bg-background/40 px-3 py-2 text-sm ring-offset-background placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              />
              <p className="text-xs text-muted-foreground">
                {isBatch
                  ? "With several pictures queued there's no way to say which post this belongs to, so every one is read straight off its own picture. Queue a single picture to correct its words."
                  : "Leave this empty and each line is read straight off the picture. Fill it in only to correct a word â€” it changes the voiceover, never what's on screen."}
              </p>

              <label className="flex cursor-pointer items-center gap-2 pt-1 text-xs text-muted-foreground">
                <input
                  type="checkbox"
                  checked={autofill}
                  disabled={setAutofill.isPending}
                  onChange={(e) => setAutofill.mutate(e.target.checked)}
                  className="size-3.5 accent-primary"
                />
                Fill this box automatically when I paste a picture
              </label>
            </div>

            <div className="space-y-2">
              <Label htmlFor="post-title">YouTube title (optional)</Label>
              <Input
                id="post-title"
                disabled={isBatch}
                placeholder="Leave blank â€” Codex writes one in the channel's style"
                value={postTitle}
                onChange={(e) => setPostTitle(e.target.value)}
              />
              <p className="text-xs text-muted-foreground">
                {isBatch
                  ? "Each queued post gets its own title, written from its own words."
                  : "Left blank, the title is written from the post and modelled on @YosherYaps's real titles. Type one here only to override it."}
              </p>
            </div>

            <Button
              className="w-full"
              size="lg"
              disabled={Boolean(blockedReason) || repost.isPending}
              onClick={() => repost.mutate()}
            >
              {repost.isPending ? <Spinner /> : <ImagePlus className="size-4" />}
              {repost.isPending
                ? isBatch
                  ? `Queueing ${(batchDone ?? 0) + 1} of ${postImages.length}â€¦`
                  : "Uploadingâ€¦"
                : isBatch
                  ? `Make ${postImages.length} videos`
                  : "Make the video"}
            </Button>

            {/* A disabled button with no explanation just reads as broken â€” say what is missing. */}
            {blockedReason && !repost.isPending && (
              <p className="text-center text-sm text-amber-300">{blockedReason}</p>
            )}

            {repost.isSuccess && (
              <div className="space-y-1">
                <p className="text-sm text-success">
                  {repost.data.queued === 1
                    ? "Queued â€” see the Review tab."
                    : `${repost.data.queued} queued â€” they build one at a time. See the Review tab.`}
                </p>
                {/* A picture that failed must be named, or it looks like it queued with the rest. */}
                {repost.data.failures.length > 0 && (
                  <p className="text-sm text-destructive">
                    {repost.data.failures.length} couldn't be queued: {repost.data.failures.join("; ")}
                  </p>
                )}
              </div>
            )}
            {repost.isError && (
              <p className="text-sm text-destructive">
                {errMsg(repost.error, "Couldn't start the repost. Is the API running?")}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Hands-off batch generate */}
      {!usesPictures && (
      <Card className="border-primary/30">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Wand2 className="size-5 text-primary" /> Auto-generate
          </CardTitle>
          <CardDescription>
            One click. Codex invents the topics, builds each video, and drops them in the review
            queue for you to approve or reject.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="count">How many videos?</Label>
            <div className="flex items-center gap-3">
              <div className="relative">
                <Hash className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  id="count"
                  type="number"
                  min={1}
                  max={20}
                  value={count}
                  onChange={(e) => setCount(Math.max(1, Math.min(20, Number(e.target.value) || 1)))}
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
          </div>

          <Button
            className="w-full"
            size="lg"
            disabled={auto.isPending || !channel || settings?.generationPaused}
            onClick={() => auto.mutate()}
          >
            {auto.isPending ? <Spinner /> : <Sparkles className="size-4" />}
            {auto.isPending ? "Thinking of topicsâ€¦" : `Generate ${count} video${count > 1 ? "s" : ""}`}
          </Button>

          {auto.isError && (
            <p className="text-sm text-destructive">
              {errMsg(auto.error, "Couldn't start the batch. Is the API running?")}
            </p>
          )}

          {lastBatch && (
            <div className="space-y-2 rounded-md border border-border bg-secondary/40 p-3">
              <p className="text-sm font-medium">Queued {lastBatch.length}:</p>
              <ul className="space-y-1">
                {lastBatch.map((j) => (
                  <li key={j.id} className="flex items-center gap-2 text-sm text-muted-foreground">
                    <Badge variant="outline">{j.id}</Badge>
                    <span className="truncate">{j.topic}</span>
                  </li>
                ))}
              </ul>
              <p className="text-xs text-muted-foreground">
                Track them in the <span className="text-foreground">Review</span> tab.
              </p>
            </div>
          )}
        </CardContent>
      </Card>
      )}

      {/* Manual single topic */}
      {!usesPictures && (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Send className="size-5" /> Make one from a topic
          </CardTitle>
          <CardDescription>Type a specific idea and generate a single short.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="topic">Topic</Label>
            <Input
              id="topic"
              placeholder="e.g. microwaving food at night"
              value={topic}
              onChange={(e) => setTopic(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && topic.trim() && !single.isPending) single.mutate();
              }}
            />
          </div>
          <Button
            className="w-full"
            variant="secondary"
            disabled={single.isPending || !topic.trim() || !channel || settings?.generationPaused}
            onClick={() => single.mutate()}
          >
            {single.isPending ? <Spinner /> : <Send className="size-4" />}
            {single.isPending ? "Queuingâ€¦" : "Generate short"}
          </Button>
          {single.isSuccess && (
            <p className="text-sm text-success">Queued â€” see the Review tab.</p>
          )}
          {single.isError && (
            <p className="text-sm text-destructive">
              {errMsg(single.error, "Couldn't start generation. Is the API running?")}
            </p>
          )}
        </CardContent>
      </Card>
      )}

      {/* Shared settings */}
      <Card className="lg:col-span-2">
        <CardHeader>
          <CardTitle>Settings</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          {/* Formats are hints passed to Codex, and reposts never reach Codex â€” showing the
              dropdown there would imply a choice that does nothing. */}
          {!usesPictures && (
            <div className="space-y-2">
              <Label htmlFor="format">Format</Label>
              <Select id="format" value={format} onChange={(e) => setFormat(e.target.value)}>
                {FORMATS.map((f) => (
                  <option key={f.value} value={f.value}>
                    {f.label}
                  </option>
                ))}
              </Select>
            </div>
          )}
          <div className="sm:col-span-2">
            <UploadNotice health={health} />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

function UploadNotice({ health }: { health?: Health }) {
  if (!health) return null;

  // Approving posts to every configured destination, so name the ones that are actually live.
  const destinations = [
    health.uploadConfigured ? "YouTube" : null,
    health.facebookConfigured ? "Facebook" : null,
    health.instagramConfigured ? "Instagram" : null,
  ].filter((d): d is string => d !== null);

  if (destinations.length === 0)
    return (
      <p className="text-sm text-amber-400">
        âš  No upload destination is configured â€” approved videos can't be posted anywhere yet (add
        YouTube OAuth client secrets, or Facebook/Instagram credentials). You can still generate
        and review.
      </p>
    );

  const list = destinations.join(" + ");

  // Saved credentials + switch off = silently skipped on approval. Worth calling out here,
  // where it is seen before approving rather than after.
  const off = [
    health.facebookOffWithCreds ? "Facebook" : null,
    health.instagramOffWithCreds ? "Instagram" : null,
  ].filter((d): d is string => d !== null);

  if (off.length > 0)
    return (
      <div className="space-y-1">
        <p className="text-sm text-amber-400">
          âš  {off.join(" and ")} {off.length === 1 ? "has" : "have"} saved credentials but{" "}
          {off.length === 1 ? "is" : "are"} switched <strong>off</strong> â€” approved videos go to{" "}
          {list || "nowhere"} only. Turn {off.length === 1 ? "it" : "them"} on in Settings.
        </p>
      </div>
    );

  if (health.autoUpload)
    return (
      <p className="text-sm text-muted-foreground">
        Auto-upload is <span className="text-foreground">on</span>: approved videos post to{" "}
        <span className="text-foreground">{list}</span> automatically. Turn it off in appsettings to
        require manual approval for every video.
      </p>
    );
  return (
    <p className="text-sm text-muted-foreground">
      Finished videos wait in <span className="text-foreground">Review</span>. Approve â†’ posts to{" "}
      <span className="text-foreground">{list}</span> (scheduled). Reject â†’ discarded.
    </p>
  );
}


