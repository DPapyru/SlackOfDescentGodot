# Slack of Descent · Godot

用 Godot 4（.NET / C#）重建 [*Slack of Descent*](https://www.deviantart.com/mudbait)（*CrackleCradle* 的 MOD）原版 GameMaker 逻辑的开源运行时与编辑器。

> 非官方粉丝项目，与原游戏及其 MOD 作者无关。原游戏的图像、音频、字体与反编译数据版权归原作者所有，**不在本仓库与许可证范围内**。

## 包含内容

| 目录 | 职责 |
| --- | --- |
| `Runtime/Gml` | GML 兼容求值器、世界积分、碰撞、Motion 与资源兼容 |
| `Runtime/Sdk` | Mod 契约、输入、角色、事件、音频与服务入口 |
| `Runtime/Mods` | 包验证、编译加载、贡献注册与资源归属 |
| `Runtime/Content` | 地图、角色、动作图及持久化数据 |
| `Runtime/Gameplay` | 地图流程、角色预览、动作状态机与运动意图 |
| `Runtime/Host/Godot` | 场景、窗口、输入、音频、本地化与 FPS |
| `Runtime/Host/Adapters` | 原版角色/骨架、地图安装、控制绑定、实体与伤害适配 |
| `Runtime/Host/Rendering` | 绘图命令、画布、角色呈现与图形资源 |
| `Runtime/Editors` | 地图、角色、动作与 Mod 编辑器 |
| `Runtime/Diagnostics` | 可选审计、采样场景与探针（默认不编译） |
| `Tests` | 按领域划分的运行时检查 |
| `Tools` | 审计/性能/导出/Mod 编译脚本，以及 `Tools/Extract`（Rust 反编译提取工具） |
| `Examples/Mods` | Mod 与 SDK 示例 |
| `.docs` | 架构与编辑器文档 |

程序集名称保持 `SlackOfDescent`。

## 环境要求

- Godot 4.7.2 .NET（mono）
- .NET SDK 8
- Node.js（`Tools/*.mjs` 脚本）
- Rust 与 ffmpeg（仅提取资源时需要）

## 原游戏资源

仓库**不包含** `GameData/`。该目录全部由原版游戏提取而来，受原游戏版权保护，已在 `.gitignore` 中排除。

自行从合法持有的原版游戏生成：

```bash
cd Tools/Extract
cargo run --release -- /path/to/original-game ../../GameData
```

生成的 `GameData/` 放在仓库根目录。运行时、测试与导出包都从该目录读取资源；发行版不含资源，需要把 `GameData/` 放到可执行文件旁。

## 构建与运行

```bash
dotnet build
godot --path .
```

诊断构建（启用 `Runtime/Diagnostics`）：

```bash
dotnet build -p:IncludeDiagnostics=true
```

测试与门禁（需要 `GameData/`，部分测试还需要 `SLACK_SOURCE` 指向原版游戏目录）：

```bash
dotnet run --project Tests/RuntimeTests.csproj
node Tools/gate.mjs
```

导出使用 `export_presets.cfg` 中的 Linux / Windows 预设。预设已排除 `GameData`、`Tests` 与 `Tools`，发行包只包含运行时。

## 发行版

[GitHub Releases](../../releases) 提供 Linux x86_64 与 Windows x64 构建（不含原游戏资源）。

## 分支

远程只保留两个分支：

- `ai`：AI 辅助开发主线
- `human`：人工开发分支

其余本地开发分支不推送到远程。

## 许可证

代码以 [MIT](LICENSE) 许可发布。原游戏资源、名称与相关素材不属于本项目，也不受 MIT 许可覆盖。
