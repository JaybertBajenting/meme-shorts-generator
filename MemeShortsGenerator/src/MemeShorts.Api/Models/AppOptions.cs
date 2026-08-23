namespace MemeShorts.Api.Models;

public sealed class PipelineOptions
{
    /// <summary>Where job folders (panels, audio, props, mp4) are written.</summary>
    public string OutputRoot { get; set; } = "out";
    /// <summary>Folder of background clips. The most recent *.mp4 is used; none = animated fallback.</summary>
    public string BackgroundDir { get; set; } = "assets/backgrounds";
    /// <summary>Folder of background-music tracks. A random one is mixed under the narration; empty = no music.</summary>
    public string MusicDir { get; set; } = "assets/music";
    /// <summary>Music volume under the voiceover (0..1). Keep low so the TTS stays clear.</summary>
    public double MusicVolume { get; set; } = 0.18;
    /// <summary>Pick a random music track per render.</summary>
    public bool MusicRandomize { get; set; } = true;
    public int Fps { get; set; } = 60;
    /// <summary>Output video width in px. 1080 = 1080p vertical (Shorts native). Bump to 1440 to supersample.</summary>
    public int Width { get; set; } = 1080;
    /// <summary>Output video height in px. 1920 = 1080p vertical.</summary>
    public int Height { get; set; } = 1920;
    /// <summary>Panel card size (square) the renderers draw at.</summary>
    public int CardSize { get; set; } = 1080;
    /// <summary>Generate a composite 1280x720 thumbnail (panel art + bold hook text) per short.</summary>
    public bool GenerateThumbnail { get; set; } = true;
    /// <summary>SQLite file for durable state (e.g. background-offset history). Relative to content root.</summary>
    public string StateDbPath { get; set; } = "data/state.db";
    /// <summary>
    /// Max jobs running at once. Renders use headless Chrome + ffmpeg and crash under
    /// contention, so keep this low (1 = fully serial). Extra requests queue.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 1;
    /// <summary>Silence inserted between beats, ms.</summary>
    public int BeatGapMs { get; set; } = 120;
    /// <summary>How long the closing fake-comment card lingers after the last beat, ms.</summary>
    public int OutroMs { get; set; } = 2500;
    /// <summary>ffmpeg executable, used to probe/trim the background clip.</summary>
    public string FfmpegCommand { get; set; } = "ffmpeg";
    /// <summary>Pick a random clip + random start offset per render so backgrounds don't repeat.</summary>
    public bool BackgroundRandomize { get; set; } = true;
    /// <summary>
    /// Minimum seconds between this render's offset and recent ones for the same clip.
    /// 0 = use the segment length (guarantees no overlap). Higher = spread further apart.
    /// </summary>
    public double BackgroundMinGapSec { get; set; } = 0;
    /// <summary>x264 preset for the per-job background trim (ultrafast..slow). Faster = quicker render.</summary>
    public string BackgroundReencodePreset { get; set; } = "veryfast";
    /// <summary>
    /// After a successful render, delete the per-job scratch files (bg.mp4, panels, beat wavs,
    /// props, music) and keep only the final short.mp4 + thumb.jpg. Keeps the out/ folder from
    /// ballooning — important on a tight disk. Set false to keep everything for debugging.
    /// </summary>
    public bool CleanupIntermediates { get; set; } = true;
}

public sealed class TtsOptions
{
    /// <summary>"Windows" (System.Speech) or "ElevenLabs".</summary>
    public string Provider { get; set; } = "Windows";

    /// <summary>
    /// Force one delivery emotion on EVERY beat, overriding the storyboard's per-beat emotion.
    /// Set to a high-energy tag (e.g. "shouting", "screaming", "excited") for constant brainrot
    /// energy. Empty = respect each beat's own emotion. Only affects the ElevenLabs v3 model.
    /// </summary>
    public string ForceEmotion { get; set; } = "";

    // --- Windows / SAPI ---
    /// <summary>Substring of the SAPI voice name, e.g. "Zira" or "David". Empty = system default.</summary>
    public string WindowsVoice { get; set; } = "";
    /// <summary>SAPI rate, -10..10. Brainrot pacing tends to sit around +2..+4.</summary>
    public int WindowsRate { get; set; } = 2;

    // --- ElevenLabs (used when Provider == "ElevenLabs") ---
    public string ElevenLabsApiKey { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "";
    public string ElevenLabsModelId { get; set; } = "eleven_turbo_v2_5";
    /// <summary>
    /// ElevenLabs output format. Default mp3_44100_128 works on all tiers; raw pcm_* and
    /// higher-bitrate mp3 require Pro/Creator tiers. MP3 is transcoded to WAV via ffmpeg so
    /// the rest of the pipeline (duration math, whisper, Remotion) is unchanged.
    /// </summary>
    public string ElevenLabsOutputFormat { get; set; } = "mp3_44100_128";
    /// <summary>
    /// If a synthesized beat is detected as clipped/distorted, re-request it from ElevenLabs
    /// (with a fresh seed) up to this many extra times, keeping the cleanest take. 0 = off.
    /// </summary>
    public int ElevenLabsMaxRerenders { get; set; } = 2;

    // --- Voice settings (sent only if set). Lower stability + higher style = more expressive. ---
    public double? ElevenLabsStability { get; set; }
    public double? ElevenLabsSimilarityBoost { get; set; }
    public double? ElevenLabsStyle { get; set; }
    public bool? ElevenLabsSpeakerBoost { get; set; }
    /// <summary>
    /// Delivery speed, 0.7–1.2 (1.0 = normal).
    ///
    /// MEASURED 2026-07-27: <b>eleven_v3 ignores this</b>. Same line, same voice — speed unset
    /// gave 8.8s, speed 1.1 gave 9.0s (i.e. no speed-up; the difference is run-to-run noise).
    /// The API accepts the field without error, so a silent no-op is the failure mode. It is
    /// left here because the older turbo/multilingual models do honour it — but do not expect
    /// it to change anything while ModelId is eleven_v3.
    ///
    /// Use <see cref="ElevenLabsSpeechRate"/> instead — it works on every model.
    /// </summary>
    public double? ElevenLabsSpeed { get; set; }

    /// <summary>
    /// Post-processing tempo applied with ffmpeg <c>atempo</c> (1.0 = untouched). This is how
    /// delivery speed is actually controlled: it works on ALL models — unlike the API's own
    /// <see cref="ElevenLabsSpeed"/>, which eleven_v3 silently ignores — and it preserves pitch,
    /// so the voice speeds up without turning into a chipmunk and v3's emotion tags survive.
    ///
    /// Measured in a real render: untouched narration ran 121 WPM against the reference
    /// channel's ~190. Applied after synthesis but BEFORE duration is measured, so all
    /// downstream beat timing, whisper captions, and Remotion sequencing stay consistent.
    /// </summary>
    public double ElevenLabsSpeechRate { get; set; } = 1.0;

    /// <summary>
    /// Strip near-silence from the head and tail of every synthesized beat. ElevenLabs pads
    /// each clip, and with one clip per beat that padding accumulates into dead air —
    /// it left a 1.5s tail on a measured render where the reference channel runs under 0.5s.
    /// A small cushion is kept so consonants aren't clipped.
    /// </summary>
    public bool ElevenLabsTrimSilence { get; set; } = true;
}

public sealed class TitleStyleOptions
{
    /// <summary>Study a reference channel's titles (yt-dlp) and have Codex match that style.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Channel URL or @handle, e.g. https://www.youtube.com/@YosherYaps</summary>
    public string ReferenceChannel { get; set; } = "";
    /// <summary>Which channel tab to read titles from: "shorts" or "videos".</summary>
    public string Tab { get; set; } = "shorts";
    public int Count { get; set; } = 25;
    public int CacheHours { get; set; } = 24;
    public string YtDlpPath { get; set; } = "yt-dlp";
    public int TimeoutSeconds { get; set; } = 90;
}

public sealed class CaptionsOptions
{
    /// <summary>"Whisper" (real word timing via faster-whisper) or "Even" (length-weighted spread).</summary>
    public string Source { get; set; } = "Whisper";
    /// <summary>If Whisper transcription fails, fall back to even distribution instead of failing the job.</summary>
    public bool FallbackToEven { get; set; } = true;

    public string PythonPath { get; set; } = "python";
    public string WhisperModel { get; set; } = "small.en";
    public string WhisperDevice { get; set; } = "cpu";
    public string WhisperCompute { get; set; } = "int8";
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class CodexOptions
{
    /// <summary>codex executable. Resolved on PATH if not absolute.</summary>
    public string Command { get; set; } = "codex";

    /// <summary>
    /// Extra args inserted before the prompt for every call (e.g. exec flags).
    /// Empty default on purpose: the config binder APPENDS arrays to non-empty
    /// defaults, which would duplicate flags. See CodexCliClient for the fallback.
    /// </summary>
    public string[] BaseArgs { get; set; } = Array.Empty<string>();

    /// <summary>Used only when BaseArgs is empty (e.g. no appsettings entry).</summary>
    public static readonly string[] DefaultBaseArgs = { "exec", "--skip-git-repo-check" };

    /// <summary>Per-call timeout.</summary>
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>Explicit Codex model slug. Empty uses the Codex CLI/config default.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// Codex reasoning effort for generation ("low"/"medium"/"high"/"xhigh"). Lower = much faster.
    /// The dense finance scripts otherwise reason for minutes at xhigh. Empty = use codex's config default.
    /// </summary>
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>"Codex" to generate panels via the CLI, or "Local" for the built-in fallback renderer.</summary>
    public string PanelSource { get; set; } = "Codex";

    /// <summary>Legacy fallback toggle. Codex-selected meme panels now fail closed before TTS.</summary>
    public bool FallbackOnError { get; set; } = true;

    /// <summary>
    /// $CODEX_HOME — where Codex's built-in image_gen writes to (generated_images/&lt;session&gt;/ig_*.png).
    /// Empty = use the CODEX_HOME env var, else %USERPROFILE%\.codex.
    /// </summary>
    public string CodexHome { get; set; } = "";
    public string GeneratedImagesDir { get; set; } = "generated_images";

    /// <summary>How many panels to generate concurrently (image_gen calls are slow and independent).</summary>
    public int PanelConcurrency { get; set; } = 3;

    /// <summary>
    /// How many times to regenerate a storyboard that fails quality validation (word budget,
    /// cliche escalation, robotic/uniform writing) before giving up on the job.
    ///
    /// Storyboards are cheap next to the render that follows, so looping here is worth it —
    /// but each attempt is a real Codex call, so this is not unbounded.
    /// </summary>
    public int StoryboardAttempts { get; set; } = 5;
}

public sealed class UploadOptions
{
    /// <summary>Master switch. Off until you've set up Google OAuth credentials.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Upload automatically when a short finishes generating.</summary>
    public bool AutoUpload { get; set; } = true;

    /// <summary>Path to the Google OAuth "Desktop app" client secrets JSON.</summary>
    public string ClientSecretsPath { get; set; } = "secrets/youtube_client_secret.json";
    /// <summary>Folder where the OAuth refresh token is cached after first consent.</summary>
    public string TokenStorePath { get; set; } = "secrets/youtube_token";

    // NOTE: array defaults are EMPTY on purpose — the config binder APPENDS appsettings arrays
    // to non-empty defaults (duplicating entries). The Default* fallbacks are used when omitted.
    /// <summary>Daily publish slots (local time, "HH:mm"). One short goes public per slot.</summary>
    public string[] PublishTimesLocal { get; set; } = Array.Empty<string>();
    // Six a day, four hours apart from midnight.
    public static readonly string[] DefaultPublishTimes =
        { "00:00", "04:00", "08:00", "12:00", "16:00", "20:00" };
    /// <summary>IANA/Windows timezone id for the slots. Empty = server local time.</summary>
    public string TimeZoneId { get; set; } = "";

    public string TitleSuffix { get; set; } = " #Shorts";
    public string DescriptionTemplate { get; set; } = "{title}\n\n#Shorts #meme #funny";
    public string[] Tags { get; set; } = Array.Empty<string>();
    public static readonly string[] DefaultTags = { "shorts", "meme", "funny" };
    /// <summary>YouTube category id. 24 = Entertainment, 23 = Comedy.</summary>
    public string CategoryId { get; set; } = "24";
}

/// <summary>
/// Feeds the channel's OWN real view counts back into topic generation, so new topics are
/// modelled on what actually broke out instead of on recency alone.
///
/// View counts come from yt-dlp (public data — no OAuth scope needed). They are joined to the
/// generated topic via the YouTube video id stored on each job, which is what makes this a real
/// feedback loop: it learns which *topic* earned the views, not just which title.
/// </summary>
public sealed class PerformanceOptions
{
    /// <summary>Master switch for performance-informed topic generation.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>The channel to learn from — normally your own. Empty = feature is inert.</summary>
    public string ChannelUrl { get; set; } = "";
    /// <summary>Which tab to read: "shorts" or "videos".</summary>
    public string Tab { get; set; } = "shorts";
    /// <summary>How many videos to pull per refresh.</summary>
    public int Count { get; set; } = 200;
    /// <summary>Re-fetch view counts at most this often.</summary>
    public int CacheHours { get; set; } = 12;

    /// <summary>How many proven winners to show the topic brainstormer.</summary>
    public int WinnerCount { get; set; } = 12;
    /// <summary>How many proven flops to show it as counter-examples. 0 = don't show flops.</summary>
    public int FlopCount { get; set; } = 8;
    /// <summary>A video counts as a winner at or above this multiple of the channel median.</summary>
    public double WinnerMultiple { get; set; } = 1.6;
    /// <summary>A video counts as a flop at or below this multiple of the channel median.</summary>
    public double FlopMultiple { get; set; } = 0.75;
    /// <summary>
    /// Ignore videos younger than this — a short posted hours ago hasn't had time to break out,
    /// and judging it as a flop would teach exactly the wrong lesson.
    /// </summary>
    public int MinAgeDays { get; set; } = 3;
    /// <summary>
    /// Don't steer generation until the channel has at least this many judgeable videos.
    /// Below it the view spread is mostly noise.
    /// </summary>
    public int MinVideosForSignal { get; set; } = 15;

    public string YtDlpPath { get; set; } = "yt-dlp";
    public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// Facebook Page Reels publishing via the Meta Graph API.
///
/// Unlike YouTube there's no runtime OAuth dance: you mint a long-lived PAGE access token
/// once (see secrets/README.txt) and paste it here. Facebook CAN hold a post until a future
/// time, so scheduled reels use its native scheduled_publish_time.
/// </summary>
public sealed class FacebookOptions
{
    /// <summary>Master switch. Off until a Page id + token are set.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Numeric id of the Facebook Page the reel is posted to.</summary>
    public string PageId { get; set; } = "";
    /// <summary>Long-lived PAGE access token (NOT a user token). Keep out of git — use .env.</summary>
    public string PageAccessToken { get; set; } = "";
    /// <summary>Graph API version to call, e.g. "v21.0".</summary>
    public string ApiVersion { get; set; } = "v21.0";
    /// <summary>Caption template. {title}, {description} and {tags} are substituted.</summary>
    public string CaptionTemplate { get; set; } = "{title}\n\n{tags}";
    // Array default is EMPTY on purpose — the config binder APPENDS appsettings arrays to
    // non-empty defaults (duplicating entries). DefaultHashtags is the fallback.
    public string[] Hashtags { get; set; } = Array.Empty<string>();
    public static readonly string[] DefaultHashtags = { "#Reels", "#meme", "#funny" };
    /// <summary>
    /// Post at the job's reserved publish slot (native scheduling) instead of immediately.
    /// Facebook requires a scheduled time 10 minutes to 75 days out; anything outside that
    /// window falls back to publishing now.
    /// </summary>
    public bool UseSchedule { get; set; } = true;
    /// <summary>Upload timeout. Reels are small but home upstream links are slow.</summary>
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// Instagram Reels publishing via the Meta Graph API (Content Publishing).
///
/// Requires an Instagram **Business or Creator** account linked to a Facebook Page; the token
/// is the same kind of long-lived Page token Facebook uses.
///
/// IMPORTANT: Instagram's API cannot schedule — media_publish goes live the moment it's called.
/// So a reel is uploaded and published AT its reserved slot by SocialPublishWorker, which means
/// the app has to be running then. See <see cref="SkipIfMissedByMinutes"/>.
/// </summary>
public sealed class InstagramOptions
{
    /// <summary>Master switch. Off until an IG user id + token are set.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Instagram *Business* account id (the IG user id, not the @handle).</summary>
    public string IgUserId { get; set; } = "";
    /// <summary>Long-lived Page access token for the linked Page. Keep out of git — use .env.</summary>
    public string AccessToken { get; set; } = "";
    public string ApiVersion { get; set; } = "v21.0";
    /// <summary>Caption template. {title}, {description} and {tags} are substituted.</summary>
    public string CaptionTemplate { get; set; } = "{title}\n\n{tags}";
    public string[] Hashtags { get; set; } = Array.Empty<string>();
    public static readonly string[] DefaultHashtags = { "#reels", "#meme", "#funny" };
    /// <summary>Also show the reel on the main profile feed grid.</summary>
    public bool ShareToFeed { get; set; } = true;
    /// <summary>
    /// Hold the reel until its reserved slot (published by the background worker) instead of
    /// going live as soon as it's approved. Turning this off posts every approved short
    /// immediately — a batch of 10 would all land at once.
    /// </summary>
    public bool UseSchedule { get; set; } = true;
    /// <summary>
    /// If the app was off when a slot passed, publish late so long as it's within this many
    /// minutes; older ones are marked failed rather than dumped out at the wrong time.
    /// 0 = always publish late, no matter how stale.
    /// </summary>
    public int SkipIfMissedByMinutes { get; set; } = 180;
    /// <summary>How long to wait for Instagram to finish transcoding the container.</summary>
    public int ProcessingTimeoutSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class RemotionOptions
{
    /// <summary>Absolute or relative path to the remotion project root (contains package.json).</summary>
    public string ProjectDir { get; set; } = "remotion";
    /// <summary>Entry passed to `remotion render`.</summary>
    public string Entry { get; set; } = "src/index.ts";
    public string CompositionId { get; set; } = "MemeShort";
    /// <summary>"npx" by default; resolved on PATH.</summary>
    public string NpxCommand { get; set; } = "npx";
    public int TimeoutSeconds { get; set; } = 600;
    /// <summary>Lower = gentler on the asset server (fewer parallel frame/audio fetches).</summary>
    public string Concurrency { get; set; } = "4";
    /// <summary>delayRender() timeout (ms). Headroom for the asset server stalling under load.</summary>
    public int TimeoutMs { get; set; } = 120000;
    /// <summary>Retry the whole render this many times on failure (transient stalls).</summary>
    public int MaxAttempts { get; set; } = 2;
    /// <summary>
    /// Intermediate frame format Remotion feeds to the encoder: "png" (lossless — crispest black
    /// line-art) or "jpeg" (small temp footprint). PNG writes large temp files under render-tmp
    /// (cleaned after each render), so keep an eye on free disk during big batches.
    /// </summary>
    public string ImageFormat { get; set; } = "png";
    /// <summary>JPEG frame quality 1-100 (only used when ImageFormat=jpeg).</summary>
    public string JpegQuality { get; set; } = "92";
    /// <summary>
    /// x264 constant rate factor for the final encode (lower = higher quality/bigger file).
    /// 16-18 is visually near-lossless. Empty = Remotion default (18).
    /// </summary>
    public string Crf { get; set; } = "16";

    /// <summary>
    /// Cap (MB) on Remotion's OffthreadVideo frame cache.
    ///
    /// THIS IS THE FIX for the compositor crashing with
    /// "memory allocation of N bytes failed" partway through a render. The cache of decoded
    /// background-video frames grew without bound, so short renders passed and long ones died
    /// (observed failing at frame 71, then 364, while a 201-frame render succeeded). Capping it
    /// let the full 1095-frame render finish at 60fps with no quality loss.
    /// 0 = don't pass the flag (Remotion's own default).
    /// </summary>
    public int OffthreadVideoCacheMb { get; set; } = 512;
}
