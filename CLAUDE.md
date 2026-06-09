# CLAUDE.md

This file exists for Claude-compatible agents.

The canonical, IDE-agnostic instructions live in `AGENTS.md` (the single source of truth). Load it now so its rules are in context, not just linked:

@AGENTS.md

Follow these files in order:
1. [AGENTS.md](AGENTS.md)
2. [developers/guidelines/AGENT_WORKFLOW.md](developers/guidelines/AGENT_WORKFLOW.md)
3. [docs/architecture/MULTI_AGENT_COORDINATION.md](docs/architecture/MULTI_AGENT_COORDINATION.md) when delegating to sub-agents, worktrees, or parallel sessions
4. [developers/lessons-learnt/general.md](developers/lessons-learnt/general.md) for durable repo memory

If any instruction conflicts, `AGENTS.md` wins.

Repo-specific reminders:
- For non-trivial work, start with `Entering plan mode for this task...` when the host supports explicit plan mode.
- Planning is mandatory for meaningful changes, but waiting for approval is only required when the user asks for it or the design is ambiguous or high-risk.
- When work is large, multi-step, or clearly parallelizable, delegation is required when the host supports it; follow `developers/guidelines/AGENT_WORKFLOW.md` and `docs/architecture/MULTI_AGENT_COORDINATION.md` instead of keeping the whole task in one agent context.
- Do not bypass verification, build timeout, TFM, or package-version rules from `AGENTS.md`.

## Claude Code specifics

- **Skills.** This repo's agent skills are authored under `.ai/skills/` (the IDE-agnostic source of truth). Claude Code discovers them through the generated mirror at `.claude/skills/`, which is committed so a fresh clone gets them with zero setup. **Do not hand-edit `.claude/skills/`** — edit the source under `.ai/skills/<slug>/` and regenerate with the `sync-claude-skills` skill (`.ai/skills/sync-claude-skills/scripts/sync-claude-skills.sh`, or `.ps1` on Windows). CI (`.github/workflows/claude-skills-sync.yml`) fails if the mirror drifts from the source.
- **Shell.** The "no `&&`" rule in `AGENTS.md` is for the Windows/PowerShell developer shell. On Linux/macOS, Claude Code runs `bash`, where `&&` and `;` are both fine.
- **Stray `CLAUDE.md` stubs.** Empty `<claude-mem-context>` `CLAUDE.md` files can appear in subdirectories from local memory tooling. They are not repository content — do not commit them; stage intended paths explicitly instead of `git add .`.
