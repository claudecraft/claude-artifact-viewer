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
