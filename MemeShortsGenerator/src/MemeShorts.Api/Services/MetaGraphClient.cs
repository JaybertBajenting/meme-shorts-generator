using System.Net.Http.Headers;
using System.Text.Json;

namespace MemeShorts.Api.Services;

/// <summary>A Graph API error, unwrapped from Meta's {"error":{...}} envelope.</summary>
public sealed class MetaGraphException : Exception
{
    public int Code { get; }
    public int SubCode { get; }
    public string? TraceId { get; }

    public MetaGraphException(string message, int code = 0, int subCode = 0, string? traceId = null)
        : base(message)
    {
        Code = code; SubCode = subCode; TraceId = traceId;
    }
}

/// <summary>
/// Thin wrapper over the Meta Graph API shared by the Facebook and Instagram uploaders:
/// form POSTs, field GETs, and the resumable binary upload to rupload.facebook.com.
///
/// Deliberately minimal — no SDK. The three calls each platform needs are simple form posts,
/// and the official SDK would drag in a large dependency for that.
/// </summary>
public sealed class MetaGraphClient
{
    public const string HttpClientName = "meta";
    private const string GraphBase = "https://graph.facebook.com";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<MetaGraphClient> _log;

    public MetaGraphClient(IHttpClientFactory factory, ILogger<MetaGraphClient> log)
    {
        _factory = factory; _log = log;
    }

    /// <summary>POST an application/x-www-form-urlencoded request to /{version}/{path}.</summary>
    public async Task<JsonElement> PostAsync(string apiVersion, string path,
        IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        var url = $"{GraphBase}/{apiVersion.Trim('/')}/{path.TrimStart('/')}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        return await SendAsync(req, Redact(url, form), ct);
    }

    /// <summary>GET /{version}/{path} with query params (e.g. fields=status_code).</summary>
    public async Task<JsonElement> GetAsync(string apiVersion, string path,
        IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{GraphBase}/{apiVersion.Trim('/')}/{path.TrimStart('/')}" + (qs.Length > 0 ? "?" + qs : "");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(req, Redact(url, query), ct);
    }

    /// <summary>
    /// Push the whole file to a resumable upload endpoint (rupload.facebook.com). Both platforms
    /// hand back such a URL from their "start" call. Sent as one chunk from offset 0 — a short is
    /// a few MB, so restart-on-failure beats implementing real resume bookkeeping.
    /// </summary>
    public async Task UploadFileAsync(string uploadUrl, string filePath, string accessToken, CancellationToken ct)
    {
        var size = new FileInfo(filePath).Length;
        await using var fs = File.OpenRead(filePath);

        using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        req.Headers.TryAddWithoutValidation("offset", "0");
        req.Headers.TryAddWithoutValidation("file_size", size.ToString());
        req.Content = new StreamContent(fs);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = size;

        _log.LogInformation("Uploading {Bytes:N0} bytes to Meta resumable endpoint.", size);
        await SendAsync(req, StripQuery(uploadUrl), ct);
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage req, string safeUrl, CancellationToken ct)
    {
        var http = _factory.CreateClient(HttpClientName);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw ToException(body, res.StatusCode, safeUrl);

        if (string.IsNullOrWhiteSpace(body)) return default;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            // Some rupload responses are bare/non-JSON on success; nothing to read back.
            return default;
        }

        using (doc)
        {
            // Graph can return 200 with an error envelope on a few endpoints.
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out _))
                throw ToException(body, res.StatusCode, safeUrl);

            return doc.RootElement.Clone();
        }
    }

    private static MetaGraphException ToException(string body, System.Net.HttpStatusCode status, string safeUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = Str(err, "message") ?? "Unknown Graph API error";
                var type = Str(err, "type");
                var userMsg = Str(err, "error_user_msg");
                var code = Int(err, "code");
                var sub = Int(err, "error_subcode");
                var trace = Str(err, "fbtrace_id");

                var text = userMsg is { Length: > 0 } ? $"{msg} — {userMsg}" : msg;
                return new MetaGraphException(
                    $"Meta Graph API error ({type ?? "error"} {code}{(sub != 0 ? $"/{sub}" : "")}): {text}",
                    code, sub, trace);
            }
        }
        catch (JsonException) { /* fall through to the raw-body form below */ }

        var snippet = body.Length > 400 ? body[..400] + "…" : body;
        return new MetaGraphException($"Meta Graph API {(int)status} calling {safeUrl}: {snippet}");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>Access tokens must never reach a log line or an error surfaced in the UI.</summary>
    private static string Redact(string url, IReadOnlyDictionary<string, string> parameters)
    {
        var stripped = StripQuery(url);
        return parameters.ContainsKey("access_token") ? stripped + " (token redacted)" : stripped;
    }

    private static string StripQuery(string url)
    {
        var i = url.IndexOf('?');
        return i < 0 ? url : url[..i];
    }
}
