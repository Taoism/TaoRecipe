# TaoRecipe

Turns cooking videos into written, translated recipes with step screenshots, and imports them into Notion.

Input: any URL yt-dlp supports (YouTube, Instagram, TikTok, Facebook, Vimeo, Bilibili, …) or local video files.
Output: one Notion page per recipe in a **Recipes** database. Each page has a cover shot, ingredient checklist, numbered method with a
screenshot under each step and a timestamp link back into the video, plus tips. A local copy (`recipe.json`, `recipe.md`, JPEGs) goes to
`Documents\TaoRecipe\<recipe>`.

![TaoRecipe](screenshots/main.png)

## Download

Get `TaoRecipe-<version>-win-x64.zip` from [Releases](../../releases), unzip it anywhere, and run `TaoRecipe.exe` (GUI) or
`taorecipe-cli.exe`. The build is self-contained, so no .NET install is needed. `yt-dlp` and `ffmpeg` must be on PATH, or you can set their paths in Settings.

## How it works

1. **Video**: yt-dlp downloads the video (≤1080p) and its metadata; descriptions often contain the exact quantities. Local files are used as they are.
2. **Transcript**: uploader subtitles when available, otherwise local **Whisper** (whisper.cpp via Whisper.net, large-v3-turbo by default,
   GPU via Vulkan with a normal graphics driver, or CUDA when the CUDA 13 runtime is installed; CPU fallback). The model downloads once to `%LOCALAPPDATA%\TaoRecipe\models`.
3. **Frames**: ffmpeg samples frames on scene changes (at least every 20 s, at most every 3 s; up to 90, 640 px).
4. **Claude** (Opus 5.5, adaptive thinking, structured JSON output) reads the metadata, transcript and frames together and writes the
   recipe(s) in the target language, choosing the best timestamp for each step. Several recipes in one video become separate pages.
   Server-side refusal fallback is enabled (beta `server-side-fallback-2026-07-01`).
5. **Screenshots**: for each step, ffmpeg tests 5 frames around the chosen moment and keeps the sharpest (Laplacian variance) at up to 1600 px.
6. **Notion**: API version 2026-03-11. The tool creates the database on first use under your parent page, uploads the images through the
   File Upload API, and creates the page.

A failed run keeps its transcript and Claude result in `Documents\TaoRecipe\_work`, so a retry (e.g. after fixing Notion sharing) does
not transcribe or bill Claude again. Use `--fresh` to force a redo.

## Setup

Requirements: .NET 10, `yt-dlp` and `ffmpeg`/`ffprobe` on PATH (or set their paths in Settings).

1. **Anthropic API key** from console.anthropic.com.
2. **Notion**: create an *internal integration* at <https://www.notion.so/profile/integrations> with Read, Update and Insert content
   capabilities. Copy its secret. In Notion, open the page that should hold the Recipes database, then ••• → **Connections** → add the integration.
3. Enter both, plus the page URL, in the GUI's **Settings** (click *Test connections*), or with the CLI:

```
taorecipe-cli --set-anthropic-key sk-ant-...
taorecipe-cli --set-notion-token ntn_...
taorecipe-cli --set-notion-parent https://www.notion.so/My-Kitchen-0123456789abcdef0123456789abcdef
```

Secrets are stored DPAPI-encrypted in `%APPDATA%\TaoRecipe\settings.json`. `ANTHROPIC_API_KEY` / `NOTION_TOKEN` environment variables override them.

## Use

GUI (`TaoRecipe.exe`): paste URLs or drop files/folders, choose the language, then click **Create recipes**.

CLI:

```
taorecipe-cli "https://www.youtube.com/watch?v=..." D:\Videos\pasta.mp4
taorecipe-cli -l Dutch @urls.txt
taorecipe-cli --no-notion clip.mp4        # local copy only
```

Instagram, Facebook and similar sites may need cookies: set *yt-dlp extra arguments* to e.g. `--cookies-from-browser firefox`.

## Cost

A 30-minute video sends about 90 small frames plus the transcript, roughly 40–60k input tokens, so it costs a few tens of cents on Opus 5.5.
For cheaper runs, pick Sonnet 5.5 or lower the frame count in Settings.

## Build

```
dotnet build TaoRecipe.sln
```

Projects: `TaoRecipe.Core` (pipeline), `TaoRecipe.Cli` (`taorecipe-cli`), `TaoRecipe` (Avalonia GUI, dark theme).
