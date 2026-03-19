FROM mcr.microsoft.com/devcontainers/base:2-trixie

# Placed early so these rarely-changing packages form a stable cached layer,
# avoiding re-download when later layers change.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       shellcheck fzf fd-find jq curl wget tree \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Debian installs fd as "fdfind"; symlink to the standard name
RUN ln -sf /usr/bin/fdfind /usr/local/bin/fd

# Docker named volumes inherit ownership from the mount-point at creation time.
# Pre-creating these directories ensures the vscode user owns them from the start,
# preventing permission errors on first container launch.
RUN mkdir -p /home/vscode/.local/share/mise \
             /home/vscode/.local/share/mise/state \
             /home/vscode/.nuget/packages \
             /home/vscode/.config/gh \
    && chown -R vscode:vscode /home/vscode/.local \
                               /home/vscode/.nuget \
                               /home/vscode/.config

# Antidote (zsh plugin manager) is cloned at build time so container startup
# does not depend on network access to GitHub.
RUN git clone --depth=1 https://github.com/mattmc3/antidote.git /home/vscode/.antidote \
    && chown -R vscode:vscode /home/vscode/.antidote

# Declared here rather than in .zshrc so that mise shims and locally installed
# binaries are available in non-interactive shells (e.g. VS Code tasks, cron).
ENV PATH="/home/vscode/.local/bin:/home/vscode/.local/share/mise/shims:${PATH}"

WORKDIR /workspaces/web-optimised-video-generation
USER vscode
