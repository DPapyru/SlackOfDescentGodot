# Slack of Descent

Slack of Descent 是一个使用 Godot 与 C# 开发的游戏项目。

## 分支与 PR

- `main` 是稳定/发布分支，`ai` 是集成分支。
- 功能分支从 `ai` 切出，PR 提向 `ai`；每个里程碑发一次 `ai` -> `main` 的发布 PR。
- 永远不要直接提交到 `ai` 或 `main`。Agent 可以提交、推送分支和开 PR，但不能合并、批准或修改仓库设置。

## 文档与决策

- 文档在 [GitHub Wiki](https://github.com/DPapyru/SlackOfDescentGodot/wiki)；仓库只保留 `AGENTS.md` 和本文件。
- 决策与计划记录在 issue 和 Wiki 的索引页中。

## 门禁

```bash
dotnet build
dotnet run --project Tests/RuntimeTests.csproj
node Tools/verify.mjs
node Tools/gate.mjs
dotnet build -p:IncludeDiagnostics=true
node Tools/motion-audit.mjs --headless
```

`node Tools/gate.mjs` 已包含 build、tests、verify 三步；诊断构建和动作审计按需另跑。

## 硬规则

1. 不新增仓库文档。仓库只允许 `AGENTS.md` 和本文件两个 Markdown；其余 `.md` / `.rst` / `.adoc` / `.txt` 会被 `Tools/verify.mjs` 拦下。
2. 不写代码注释。`.cs` / `.mjs` / `.rs` 不保留任何注释；字符串和正则里的 `//`、`/* */` 是代码，不能删。
3. AI 写的 issue 正文、issue 评论、PR 描述和 PR 审查评论，第一行必须是 `## AI回复`；人类内容不加标记。
4. issue/PR 评论不可修改或删除，更正用新评论；issue 正文只由维护者更新，改标题需要 `TITLE EDIT` 评论。
5. 计划、TODO 与进度写进 GitHub issue；不把计划写进提交，也不新增临时文档。
6. issue 每晚快照到 `issue-archive` 分支；issue 不删除，只用收尾评论关闭。
