## New in 1.6.0

**The app updates itself now.** The new-version banner has an **Update now**
button: it downloads the release, verifies it against the published SHA-256,
swaps the exe in place and restarts. Settings, tabs and taskbar pins all
survive — the file's path never changes — and because the app fetches the
download itself, there's no SmartScreen warning. If the download doesn't
verify or the swap can't work (say the exe sits somewhere unwritable), it
falls back to the manual route below. The update *check* now runs at each
launch instead of daily; it still sends nothing but a User-Agent, and
`"checkForUpdates": "false"` in `settings.json` still turns it off.

One catch, just this once: the version you're *running* needs the button, so
updating **to** 1.6.0 is still the manual replace described below. From the
next release on, it's one click.

## Download

**[ArtifactViewer.exe](#assets)** — download it and run it. That's the whole install.

Nothing to unzip, no .NET to install, nothing written outside your own user
folder. Settings live in `%LOCALAPPDATA%\ArtifactViewer\`; delete that folder and
the exe and it's gone.

### Windows will warn you the first time

The download is not code-signed, so Windows shows **"Windows protected your PC"**.
Click **More info → Run anyway**.

That warning means "we haven't seen this file often", not "this file is
dangerous" — signing certificates require a verified legal identity, which this
project doesn't have. If you'd rather not take our word for it, the exe is built
from this tag by [the release workflow](../../actions/workflows/release.yml) on a
GitHub runner, or you can clone and `dotnet run` yourself.

### Already running an older version?

**On 1.6.0 or later?** Click **Update now** in the banner and you're done —
everything below is handled for you.

On 1.5.0 or earlier, this download is a **separate copy** of the program, so
there's one step the browser won't do for you: put it where the old one lives.

1. In Artifact Viewer, the update notice has a **Show current file** button — it
   opens the folder containing the version you're running, with the file
   selected. (No notice on screen? The program is wherever you saved it, often
   `Downloads`.)
2. Close Artifact Viewer.
3. Drag this download into that folder, replacing the old `ArtifactViewer.exe`
   when Windows asks.

Two things worth knowing. If you download into the folder that already has the
old copy, your browser will name it `ArtifactViewer (1).exe` rather than
overwrite — the numbered one is the *new* one, so replace the old file with it
and delete the leftover. And if you've pinned Artifact Viewer to the taskbar or
made a desktop shortcut, that shortcut points at the old file's location, which
is why replacing in place matters: do it any other way and you'll keep launching
the old version and keep seeing the update notice.

Your settings, watched folder and tab state live in `%LOCALAPPDATA%` and carry
over untouched.

### Requirements

- Windows 10 or 11
- Microsoft Edge **WebView2 runtime** — preinstalled on Windows 11 and most
  Windows 10 machines. If it's missing, the app says so on launch and offers the
  download page.

### First run

The app watches `Documents\claude_artifacts` (created for you). Write a markdown
file into that folder and it appears instantly. To make Claude Code use it
automatically, paste the block from
[CLAUDE-PROMPT.md](../../blob/main/CLAUDE-PROMPT.md) into your `~\.claude\CLAUDE.md`.
