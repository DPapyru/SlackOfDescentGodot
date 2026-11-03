# Slack of Descent · AI 分支协作指南

本分支（`ai`）只用于 AI/LLM 协作开发，不存放项目文档；项目介绍与人类文档保留在 `human` 分支。

## 硬规则

1. 不新增文档。仓库只允许 `AGENTS.md` 和本文件两个 Markdown；其余 `.md` / `.rst` / `.adoc` / `.txt` 会被 `Tools/verify.mjs` 拦下。
2. 不写代码注释。`.cs` / `.mjs` / `.rs` 不保留任何注释；字符串和正则里的 `//`、`/* */` 是代码，不能删。
3. 需要记录的内容写进 commit。标题一行说清动作；正文写作用范围、改动范围、原因和未做项。
4. 只保存内容、没有文件变化时用空提交：`git commit --allow-empty -F -`。
5. 只修改 `ai` 分支。`human` 与 `feature/*` 都不碰，不建 PR。
6. 每个阶段结束必须通过完整门禁；动作相关阶段另跑动作审计。
7. 子代理分工由当次任务临时决定，不把固定角色写进流程。

## 门禁

```bash
dotnet build
dotnet run --project Tests/RuntimeTests.csproj
node Tools/verify.mjs
node Tools/gate.mjs
dotnet build -p:IncludeDiagnostics=true
node Tools/motion-audit.mjs --headless
```

`node Tools/gate.mjs` 已包含 build、tests、verify 三步；另两条用于诊断构建与动作审计。

## 交付顺序

- 先清理：文档与注释、README 指南、`verify` 白名单与导出过滤，单独一个 commit。
- 再功能：按阶段拆 commit，每段由 Reviewer 只读验收（门禁、diff、截图）通过后才进下一段。
- 验收只认仓库状态与门禁输出；没有文档要改，也不要补文档。
