/** Turn a raw backend/Remotion/ffmpeg error into one short, human-readable line. */
export function friendlyError(raw?: string | null): string {
  if (!raw) return "Failed.";
  const s = raw.toLowerCase();

  if (s.includes("no space left") || s.includes("not enough space") || s.includes("enospc"))
    return "Ran out of disk space.";
  if (s.includes("gdi+")) return "Thumbnail step failed (image error).";

  // Meta errors must be matched BEFORE the YouTube rule below — Graph API token messages
  // contain "revoked"/"expired" too, and would otherwise be blamed on YouTube. The caller
  // already labels which platform this is, so these stay platform-neutral.
  if (s.includes("meta graph api")) {
    if (s.includes("oauthexception") || s.includes("access token"))
      return "Access token expired or invalid — re-mint it (docs/META_SETUP.md).";
    if (s.includes("permission")) return "Missing a permission — re-grant it (docs/META_SETUP.md).";
    const detail = raw.slice(raw.indexOf("): ") + 3).trim();
    if (detail.length > 0 && detail.length < raw.length)
      return detail.length > 80 ? detail.slice(0, 80) + "…" : detail;
    return "Publish failed.";
  }

  if (s.includes("invalid_grant") || s.includes("token has been expired") || s.includes("revoked"))
    return "YouTube sign-in expired — reconnect.";
  if (s.includes("remotion") && s.includes("render")) return "Video render failed.";
  if (s.includes("ffmpeg")) return "Audio/video processing failed.";
  if (s.includes("codex")) return "Script/image generation failed.";
  if (s.includes("timeout") || s.includes("timed out")) return "Timed out.";
  if (s.includes("elevenlabs")) return "Voice generation failed.";

  // Fallback: first sentence / first 80 chars, single line.
  const firstLine = raw.split(/[\n\r]/)[0].trim();
  return firstLine.length > 80 ? firstLine.slice(0, 80) + "…" : firstLine;
}
