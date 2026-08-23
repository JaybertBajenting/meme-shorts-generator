import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Facebook, Instagram, CheckCircle2, AlertCircle, RotateCcw, Save, Mic } from "lucide-react";
import { api, type MetaPlatformSettings } from "@/lib/api";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Spinner } from "@/components/ui/spinner";

/**
 * Facebook / Instagram credentials, editable without restarting the app.
 *
 * The API only ever returns a masked token tail, so the password field starts empty and an
 * empty submit means "keep the stored token" — the user can change the Page id alone without
 * having to paste the token again.
 */
export function SettingsView() {
  const qc = useQueryClient();
  const { data, isLoading } = useQuery({ queryKey: ["metaSettings"], queryFn: api.metaSettings });

  const [verify, setVerify] = React.useState<{ platform: string; ok: boolean; message: string }[] | null>(null);

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["metaSettings"] });
    qc.invalidateQueries({ queryKey: ["health"] });
  };

  const save = useMutation({ mutationFn: api.saveMetaSettings, onSuccess: refresh });
  const reset = useMutation({
    mutationFn: (p: "facebook" | "instagram") => api.resetMetaSettings(p),
    onSuccess: refresh,
  });
  const check = useMutation({
    mutationFn: api.verifyMeta,
    onSuccess: (r) => setVerify(r.results),
  });

  if (isLoading || !data)
    return (
      <div className="flex items-center gap-2 text-muted-foreground">
        <Spinner /> Loading settings…
      </div>
    );

  return (
    <div className="min-w-0 space-y-6">
      <div>
        <h2 className="text-sm font-semibold uppercase tracking-wide text-muted-foreground">
          Publishing accounts
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Approved videos post to every enabled account at the same scheduled time as YouTube.
        </p>
      </div>

      <VoiceCard />

      <PlatformCard
        title="Facebook Page"
        icon={<Facebook className="size-5" />}
        idLabel="Page ID"
        idPlaceholder="e.g. 1234567890"
        settings={data.facebook}
        currentId={data.facebook.pageId ?? ""}
        busy={save.isPending}
        onSave={(id, token) => save.mutate({ facebook: { pageId: id, token: token || undefined } })}
        onToggle={(enabled) => save.mutate({ facebook: { enabled } })}
        onReset={() => reset.mutate("facebook")}
        note="Facebook holds the post itself until the scheduled time, exactly like YouTube."
      />

      <PlatformCard
        title="Instagram"
        icon={<Instagram className="size-5" />}
        idLabel="Instagram Business account ID"
        idPlaceholder="not the Page ID, and not the @handle"
        settings={data.instagram}
        currentId={data.instagram.igUserId ?? ""}
        busy={save.isPending}
        onSave={(id, token) => save.mutate({ instagram: { igUserId: id, token: token || undefined } })}
        onToggle={(enabled) => save.mutate({ instagram: { enabled } })}
        onReset={() => reset.mutate("instagram")}
        note="Instagram's API can't schedule, so the app publishes at the slot itself — it must be running then."
      />

      <Card>
        <CardContent className="space-y-3 py-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <p className="text-sm font-medium">Check credentials</p>
              <p className="text-xs text-muted-foreground">
                Resolves each token to the account it controls. Posts nothing.
              </p>
            </div>
            <Button variant="outline" disabled={check.isPending} onClick={() => check.mutate()}>
              {check.isPending ? <Spinner /> : <CheckCircle2 className="size-4" />} Check now
            </Button>
          </div>
          {verify?.map((r) => (
            <div
              key={r.platform}
              className={`flex items-start gap-2 rounded-md border p-3 text-xs ${
                r.ok ? "border-emerald-500/40 bg-emerald-500/10" : "border-destructive/40 bg-destructive/10"
              }`}
            >
              {r.ok ? (
                <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-400" />
              ) : (
                <AlertCircle className="mt-0.5 size-4 shrink-0 text-destructive" />
              )}
              <div className="min-w-0">
                <span className="font-medium capitalize">{r.platform}: </span>
                <span className="break-words">{r.message}</span>
              </div>
            </div>
          ))}
        </CardContent>
      </Card>

      <p className="text-xs text-muted-foreground">
        Tokens are stored locally in this app's database and are never shown again in full — only
        the last four characters. Setup steps are in{" "}
        <span className="font-mono text-foreground">docs/META_SETUP.md</span>.
      </p>
    </div>
  );
}

/**
 * ElevenLabs voice, changeable without a restart. Auditioning is the point: set a voice here,
 * then hit "New voice" on any card in Review to re-render that short with it.
 */
function VoiceCard() {
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ["voiceSettings"], queryFn: api.voiceSettings });
  const [voiceId, setVoiceId] = React.useState("");
  const [rate, setRate] = React.useState("");

  React.useEffect(() => {
    if (data) {
      setVoiceId(data.voiceId ?? "");
      setRate(String(data.speechRate ?? 1));
    }
  }, [data]);

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["voiceSettings"] });
    qc.invalidateQueries({ queryKey: ["health"] });
  };
  const save = useMutation({ mutationFn: api.saveVoiceSettings, onSuccess: refresh });
  const reset = useMutation({ mutationFn: api.resetVoiceSettings, onSuccess: refresh });

  const dirty = !!data && (voiceId !== data.voiceId || rate !== String(data.speechRate));

  return (
    <Card>
      <CardContent className="space-y-4 py-4">
        <div className="flex items-center gap-3">
          <div className="flex size-9 items-center justify-center rounded-lg bg-primary/15 text-primary">
            <Mic className="size-5" />
          </div>
          <div>
            <p className="text-sm font-medium">Narration voice</p>
            <div className="mt-0.5 flex items-center gap-2">
              {data?.savedInApp && <Badge variant="outline">saved here</Badge>}
            </div>
          </div>
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <label className="space-y-1 sm:col-span-2">
            <span className="text-xs text-muted-foreground">ElevenLabs voice ID</span>
            <input
              value={voiceId}
              onChange={(e) => setVoiceId(e.target.value)}
              placeholder="paste a voice ID from ElevenLabs"
              className="w-full rounded-md border border-border bg-background px-3 py-2 font-mono text-sm outline-none focus:border-primary"
            />
          </label>
          <label className="space-y-1">
            <span className="text-xs text-muted-foreground">Speed (atempo)</span>
            <input
              value={rate}
              onChange={(e) => setRate(e.target.value)}
              placeholder="1.05"
              className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm outline-none focus:border-primary"
            />
          </label>
        </div>

        <p className="text-[11px] text-muted-foreground">
          Applies to the next render — no restart needed. A new voice usually needs its speed
          retuned; start at 1.0 and adjust. Use <strong>New voice</strong> on a Review card to
          re-render an existing short with it (costs voice + render only, not new artwork).
        </p>

        <div className="flex flex-wrap gap-2">
          <Button
            disabled={save.isPending || !dirty}
            onClick={() =>
              save.mutate({ voiceId: voiceId.trim(), speechRate: Number(rate) || undefined })
            }
          >
            {save.isPending ? <Spinner /> : <Save className="size-4" />} Save voice
          </Button>
          {data?.savedInApp && (
            <Button variant="ghost" disabled={reset.isPending} onClick={() => reset.mutate()}>
              <RotateCcw className="size-4" /> Reset to .env
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function PlatformCard({
  title,
  icon,
  idLabel,
  idPlaceholder,
  settings,
  currentId,
  busy,
  note,
  onSave,
  onToggle,
  onReset,
}: {
  title: string;
  icon: React.ReactNode;
  idLabel: string;
  idPlaceholder: string;
  settings: MetaPlatformSettings;
  currentId: string;
  busy: boolean;
  note: string;
  onSave: (id: string, token: string) => void;
  onToggle: (enabled: boolean) => void;
  onReset: () => void;
}) {
  const [id, setId] = React.useState(currentId);
  const [token, setToken] = React.useState("");

  // Keep the field in sync when the server value changes (e.g. after a reset).
  React.useEffect(() => setId(currentId), [currentId]);

  const dirty = id !== currentId || token.length > 0;
  // Everything needed to publish is present; only the on/off switch is missing.
  const credsPresent = currentId.trim().length > 0 && settings.tokenSet;

  return (
    <Card>
      <CardContent className="space-y-4 py-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <div className="flex size-9 items-center justify-center rounded-lg bg-primary/15 text-primary">
              {icon}
            </div>
            <div>
              <p className="text-sm font-medium">{title}</p>
              <div className="mt-0.5 flex items-center gap-2">
                {settings.usable ? (
                  <Badge variant="success">ready</Badge>
                ) : credsPresent ? (
                  <Badge variant="warning">turned off</Badge>
                ) : (
                  <Badge variant="secondary">not configured</Badge>
                )}
                {settings.savedInApp && <Badge variant="outline">saved here</Badge>}
              </div>
            </div>
          </div>
          <Switch checked={settings.enabled} onCheckedChange={onToggle} disabled={busy} />
        </div>

        {/* Credentials filled in but the toggle left off silently skips this platform on every
            approval — the exact trap that made a batch upload to YouTube only. */}
        {credsPresent && !settings.enabled && (
          <div className="flex items-start gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-3">
            <AlertCircle className="mt-0.5 size-4 shrink-0 text-amber-400" />
            <p className="text-xs text-amber-200/90">
              Credentials are saved but publishing is <strong>off</strong> — approved videos will
              NOT post here. Flip the switch above to enable it.
            </p>
          </div>
        )}

        <div className="grid gap-3 sm:grid-cols-2">
          <label className="space-y-1">
            <span className="text-xs text-muted-foreground">{idLabel}</span>
            <input
              value={id}
              onChange={(e) => setId(e.target.value)}
              placeholder={idPlaceholder}
              className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm outline-none focus:border-primary"
            />
          </label>
          <label className="space-y-1">
            <span className="text-xs text-muted-foreground">
              Access token{" "}
              {settings.tokenSet && (
                <span className="font-mono text-foreground/70">({settings.tokenMasked})</span>
              )}
            </span>
            <input
              type="password"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              placeholder={settings.tokenSet ? "leave blank to keep current" : "paste the long-lived token"}
              className="w-full rounded-md border border-border bg-background px-3 py-2 text-sm outline-none focus:border-primary"
            />
          </label>
        </div>

        <p className="text-[11px] text-muted-foreground">{note}</p>

        <div className="flex flex-wrap gap-2">
          <Button
            disabled={busy || !dirty}
            onClick={() => {
              onSave(id.trim(), token.trim());
              setToken("");
            }}
          >
            {busy ? <Spinner /> : <Save className="size-4" />} Save
          </Button>
          {settings.savedInApp && (
            <Button
              variant="ghost"
              disabled={busy}
              onClick={() => {
                if (window.confirm(`Clear the ${title} credentials saved in the app and fall back to .env?`))
                  onReset();
              }}
            >
              <RotateCcw className="size-4" /> Reset to .env
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}
