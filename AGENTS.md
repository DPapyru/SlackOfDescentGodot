## Global Rules

- **Comments & Code:** Limit code comments to an absolute minimum; almost never add comments. Make the smallest necessary changes; reuse existing code whenever possible; avoid unnecessary scope expansion.
- **Images:** With explicit user authorization, you may read images autonomously.
- **Skills:** With explicit user authorization, you may use and invoke Skills.
- **Documentation vs. Git:** Avoid redundant documentation. Documents must strictly reflect the current architecture, never act as a changelog (Git handles history). Do not create standalone `TODO.md`; manage TODOs through Git commit messages, PRs, or issues.
- **Plan Tracking:** Never save plans in git commits. Plans, TODOs and progress live in GitHub issues (comments and sub-issues); commit messages describe only the change.
- **Agent Permissions:** Implementation agents MUST NOT modify documentation files directly. They may only propose documentation changes. Only designated documentation subagents (or the main agent acting as the doc writer) can write to docs.
- **Main Agent Role:** When sub-agents are authorized and deployed by the user, the main agent must only report/supervise and refrain from doing the work itself, unless explicitly instructed otherwise by the user.
- **Execution Pace:** Execute only one step per turn. Proceed step-by-step rather than completing everything at once, unless explicitly authorized by the user.

## Branch Model

- `main` is the stable/release branch; `ai` is the integration branch. Feature branches are cut from `ai`, and feature PRs target `ai`. Release PRs go `ai` -> `main` once per milestone.
- Never commit directly to `ai` or `main`. Agents may commit and push branches and open PRs, but never merge, approve, or change repository settings.

## Issues & Pull Requests

- **Issue First:** Any non-trivial change starts as an issue. Work discovered mid-flight gets a follow-up issue in the same PR, not a silent scope creep.
- **One Branch Per Issue:** Name the branch `<type>/<issue-number>-<slug>` (e.g. `feat/12-mud-tentacle`).
- **PR Target:** Feature PRs target `ai`; the only PRs to `main` are milestone release PRs from `ai`. For an issue-closing PR, a human-authored body starts with `Closes #N` on the first line. An AI-authored body starts with `## AI回复` on the first line and `Closes #N` on the second, followed by what changed, how it was verified, and anything deliberately left out. Release PRs are exempt from `Closes #N`.
- **PR Scope:** A PR contains only the issue's work. Unrelated fixes branch off separately.
- **Planning Lives In Issues:** Plans, progress notes, and acceptance criteria go into issue comments or the PR body — never into commits or temporary markdown.
- **Agents Do Not Merge:** Agents may commit, push feature branches, open PRs, and respond to review. They never merge, never approve, never force-push a shared branch, never change repository settings, and never close someone else's PR. Self-review by the author is a description, not an approval.

## Comments & Decisions

- **Comments Are Immutable:** Never edit or delete issue or PR comments; corrections are posted as new comments. The issue body is the current state, and only the maintainer edits it. A title change requires a `TITLE EDIT` comment explaining the change.
- **Decision Protocol:** AI may post `DECISION:` and `SPEC:`. Scope, architecture, and acceptance decisions require a human `ACK:`. `VETO:` is maintainer-only and overrides everything. Precedence is `VETO` > human > AI, and later beats earlier. A `VETO` can only be lifted by the maintainer who issued it; AI must reply `VETO ACK`.
- **AI Marker:** Every AI-written issue body, issue comment, PR description, and PR review comment begins with `## AI回复` on the first line. Human-written content carries no marker.

## Documentation & Archive

- **Wiki Only:** Documentation lives in the GitHub Wiki, not this repository. The repository allows only `AGENTS.md` and `README.md`; `node Tools/verify.mjs` enforces the whitelist.
- **Issue Archive:** Issues are snapshotted nightly to the `issue-archive` branch. Issues are never deleted; close them with a closing comment.
- **Quality Gate:** Every milestone ends with the full gate. Motion work also runs the motion audit; the commands are listed in `README.md`.
