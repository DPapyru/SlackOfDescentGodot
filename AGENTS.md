## Global Rules

- **Comments & Code:** Limit code comments to an absolute minimum; almost never add comments. Make the smallest necessary changes; reuse existing code whenever possible; avoid unnecessary scope expansion.
- **Images:** With explicit user authorization, you may read images autonomously.
- **Skills:** With explicit user authorization, you may use and invoke Skills.
- **Documentation vs. Git:** Avoid redundant documentation. Documents must strictly reflect the current architecture, never act as a changelog (Git handles history). Do not create standalone `TODO.md`; manage TODOs through Git commit messages, PRs, or issues.
- **Plan Tracking:** Never save plans in git commits. Plans, TODOs and progress live in GitHub issues (comments and sub-issues); commit messages describe only the change.
- **Agent Permissions:** Implementation agents MUST NOT modify documentation files directly. They may only propose documentation changes. Only designated documentation subagents (or the main agent acting as the doc writer) can write to docs.
- **Main Agent Role:** When sub-agents are authorized and deployed by the user, the main agent must only report/supervise and refrain from doing the work itself, unless explicitly instructed otherwise by the user.
- **Execution Pace:** Execute only one step per turn. Proceed step-by-step rather than completing everything at once, unless explicitly authorized by the user.

## Issues & Pull Requests

- **Issue First:** Any non-trivial change starts as an issue. Work discovered mid-flight gets a follow-up issue in the same PR, not a silent scope creep.
- **One Branch Per Issue:** Name the branch `<type>/<issue-number>-<slug>` (e.g. `feat/12-mud-tentacle`). Never commit directly to `ai`.
- **PR Target:** Every pull request targets the `ai` branch. First line of the body is `Closes #N`, followed by what changed, how it was verified, and anything deliberately left out.
- **PR Scope:** A PR contains only the issue's work. Unrelated fixes branch off separately.
- **Planning Lives In Issues:** Plans, progress notes, and acceptance criteria go into issue comments or the PR body — never into commits or temporary markdown.
- **Agents Do Not Merge:** Agents may commit, push feature branches, open PRs, and respond to review. They never merge, never force-push `ai`, and never close someone else's PR. Self-review by the author is a description, not an approval.
