# Facebook + Instagram auto-upload — getting the API keys

Both platforms are the **same Meta Graph API** and share **one app** and (usually) **one token**.
You do this once; the token you end up with does not expire.

**What you need at the end — five values:**

| Value | Goes in `.env` as | What it is |
|---|---|---|
| Page id | `FACEBOOK_PAGE_ID` | numeric id of your Facebook Page |
| Page access token | `FACEBOOK_PAGE_ACCESS_TOKEN` | long-lived **Page** token |
| Instagram Business id | `INSTAGRAM_USER_ID` | the IG account id (**not** the Page id, **not** the @handle) |
| Instagram token | `INSTAGRAM_ACCESS_TOKEN` | the *same* Page token as above |
| — | `FACEBOOK_ENABLED` / `INSTAGRAM_ENABLED` | `true` to turn each on |

---

## Before you start

You need all three of these, or the API will reject you no matter what token you hold:

1. **A Facebook Page.** A personal profile cannot be posted to via the API.
2. **An Instagram Business or Creator account** — in the IG app:
   *Settings → Account type and tools → Switch to professional account*. The Content
   Publishing API does not work on personal accounts.
3. **That IG account linked to that Page** — Page → *Settings → Linked accounts → Instagram*.
   This link is what lets one Page token control both.

> **You do NOT need App Review.** While your app sits in *Development* mode, these permissions
> work fine for Pages and IG accounts **you** administer, as long as your Facebook user has a
> role on the app. App Review + Business Verification are only needed to publish on behalf of
> *other people's* accounts.

---

## Step 1 — Create a Meta app

1. Go to <https://developers.facebook.com/apps/> → **Create app**.
2. Fill in a name (e.g. `Meme Shorts Uploader`) and your contact email.
3. Use case: **Other** → app type: **Business** → create.
4. Note the **App ID** and **App Secret** from *App settings → Basic* — you need them in Step 3
   only, not in `.env`.

## Step 2 — Generate a user token with the right permissions

1. Go to **Tools → [Graph API Explorer](https://developers.facebook.com/tools/explorer/)**.
2. Top right: pick your app, and set the token type to **User token**.
3. Click **Add permissions** and tick all of these:

   **For Facebook Reels**
   - `pages_show_list`
   - `pages_read_engagement`
   - `pages_manage_posts`
   - `publish_video`

   **For Instagram Reels**
   - `instagram_basic`
   - `instagram_content_publish`

   (`business_management` too, if your Page belongs to a Business portfolio.)

4. Click **Generate access token**, log in, and grant everything. Make sure your Page and IG
   account are both ticked in the consent dialog.
5. Copy the token. This one is **short-lived (~1 hour)** — that's expected, Step 3 fixes it.

## Step 3 — Exchange it for a long-lived token

Paste this into a browser, substituting your App ID, App Secret, and the Step 2 token:

```
https://graph.facebook.com/v21.0/oauth/access_token?grant_type=fb_exchange_token&client_id=APP_ID&client_secret=APP_SECRET&fb_exchange_token=SHORT_LIVED_TOKEN
```

The `access_token` in the response is a **long-lived user token** (~60 days).

## Step 4 — Get the Page id and the Page token

```
https://graph.facebook.com/v21.0/me/accounts?access_token=LONG_LIVED_USER_TOKEN
```

Find your Page in the response and take two fields:

- `id` → this is your **`FACEBOOK_PAGE_ID`**
- `access_token` → this is your **`FACEBOOK_PAGE_ACCESS_TOKEN`**

> A Page token derived from a *long-lived* user token **does not expire**. This is why Step 3
> matters — skip it and your uploads break in an hour. Verify with
> [Access Token Debugger](https://developers.facebook.com/tools/debug/accesstoken/): it should
> say **Expires: Never**.

## Step 5 — Get the Instagram Business account id

```
https://graph.facebook.com/v21.0/PAGE_ID?fields=instagram_business_account&access_token=PAGE_TOKEN
```

The `instagram_business_account.id` in the response is your **`INSTAGRAM_USER_ID`**.

Empty response here means the IG account isn't linked to the Page, or isn't a
Business/Creator account — go back to *Before you start*.

Use the **same Page token** for `INSTAGRAM_ACCESS_TOKEN`.

## Step 6 — Fill in `.env`

Copy `.env.example` to `.env` in the `MemeShortsGenerator/` folder and fill it in:

```dotenv
FACEBOOK_ENABLED=true
FACEBOOK_PAGE_ID=1234567890
FACEBOOK_PAGE_ACCESS_TOKEN=EAAG...
INSTAGRAM_ENABLED=true
INSTAGRAM_USER_ID=1789...
INSTAGRAM_ACCESS_TOKEN=EAAG...
```

`.env` is gitignored. Never put these in `appsettings.json` — that file is committed.

## Step 7 — Verify without posting anything

Restart the API, then:

```powershell
Invoke-RestMethod -Method Post http://localhost:5180/api/meta/verify | ConvertTo-Json -Depth 5
```

Each configured platform should come back `ok: true` with the account it resolved to:

```
Facebook Page: Meme Shorts (1234567890)
Instagram: @memeshorts (1789...)
```

Anything else is a setup problem, and the message says which one. `/api/health` also reports
`facebookConfigured` / `instagramConfigured`.

---

## How scheduling differs per platform

The app reserves **one slot per short** (`Upload:PublishTimesLocal`) and every destination uses
that same time — but they get there differently:

| Platform | How it waits for the slot |
|---|---|
| YouTube | uploaded private + `publishAt`; **YouTube** publishes it |
| Facebook | uploaded with `scheduled_publish_time`; **Facebook** publishes it |
| Instagram | **the app** holds it and posts at the slot — IG's API has no scheduling |

**So for Instagram the app must be running when the slot comes around.** If it isn't, the reel
publishes at the next startup provided it's within `Instagram:SkipIfMissedByMinutes` (default
180); older than that it's marked failed and waits for you to hit Retry. Set
`Instagram:UseSchedule = false` to post immediately on approval instead — but then a batch of
ten approvals all land at once.

Facebook also refuses a scheduled time under 10 minutes or over 75 days out; the app detects
both and posts immediately rather than failing.

## Video requirements

Renders are 1080×1920, which suits both. The one real limit:

- **Facebook Reels: 3–90 seconds.** A short longer than 90s is rejected by Facebook but still
  fine for YouTube and Instagram.
- Instagram Reels: 3 seconds to 15 minutes.
- Instagram allows **50 published posts per rolling 24 hours**.

## Config reference

Beyond the `.env` values, in `appsettings.json`:

| Key | Meaning |
|---|---|
| `Facebook:CaptionTemplate` | `{title}`, `{description}`, `{tags}` placeholders |
| `Facebook:Hashtags` | appended via `{tags}` |
| `Facebook:UseSchedule` | false = post immediately on approval |
| `Instagram:ShareToFeed` | also show the reel on the profile grid |
| `Instagram:UseSchedule` | false = post immediately on approval |
| `Instagram:SkipIfMissedByMinutes` | how late a missed slot may still post (0 = always) |
| `Instagram:ProcessingTimeoutSeconds` | how long to wait for IG to transcode |

## Troubleshooting

| Message | Cause |
|---|---|
| `OAuthException 190` | token expired or revoked — redo Steps 2–4 |
| `code 200` / `code 10` | a permission wasn't granted — redo Step 2, tick them all |
| "did not return a resumable upload URI" | IG account isn't Business/Creator, or isn't linked to the Page |
| "returned no username" | `INSTAGRAM_USER_ID` is the Page id, not the IG Business id (Step 5) |
| "media container expired" | upload took over 24h — effectively a stuck job; retry |
| Facebook rejects the video | likely over the 90-second Reels limit |
