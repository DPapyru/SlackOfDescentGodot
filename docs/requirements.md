# 需求记录

固定维护位置：`docs/requirements.md`。以下“原始需求”保留用户提供的内容；实施状态另行记录。

## 实施状态

本轮仅实施 bug 与性能修复，以及支撑性能修复的 debug 独立日志。遵照用户要求先提交检查点，再在 `fix/extraction-fidelity` 分支继续；不自动合并。不加载 Skill，不读取不必要的日志和图片。用户追加 Windows 压缩包资源与运行验证，允许使用 Steam Proton。

| 编号 | 项目 | 状态 |
| --- | --- | --- |
| BUG-01 | mud 沾染逐步去除 | 尚未复现立即移除；身体残留、服饰受控衰减和水洗检查通过，不能标记根因已修复 |
| BUG-02 | SlimePlant 初始化及全地图、怪物加载验证 | SlimePlant 修复已验证；110/110 地图、51/52 怪物短期检查通过，RubberWorm 脚本与所检查资源树不一致 |
| BUG-03 | 右上角 FPS 实测更新 | 已实现，实际降速测试通过 |
| BUG-04 | chara 导出与加载完整性 | 已修复原始角色文件漏打包，独立反编译对照和 ZIP 哈希检查通过；最终双平台完整运行回归未完成 |
| PERF-01 | Playground 后段及持续运行退化 | 已优化并完成受控及有渲染路线测试；最终修改后的复核待续 |
| PERF-02 | vore 怪物热点分析与优化 | 共用激活、液滴及资源缓存已优化；绑定专项汇总 31/32 通过，受控性能对照待完成 |
| PERF-03 | debug 掉帧独立日志 | 已实现，模拟采样与实际降速写入检查通过 |
| LATER-01 | 汉化 | 待办 |
| LATER-02 | 原生 60 帧及 delta 适配 | 待办 |
| LATER-03 | 原版全部按键功能对照测试 | 待办 |
| LATER-04 | 新分支实现 Godot/C# 动作适配 | 待办 |
| LATER-05 | 新分支实现角色编辑器 | 待办 |
| LATER-06 | 新分支实现地图编辑器 | 待办 |

性能日志采用已确认的“相对稳定帧率”基准：进入地图预热 3 秒，再采集 5 个一秒窗口，以中位数建立基准；下降至少 5 FPS 时记录，持续掉帧最多每 5 秒补充一次，切图重建基准。逻辑与渲染帧率分开统计。保持原版逻辑步长，本轮不实施原生 60 帧改造。

2026-10-22 继续实施：已提交 `cf93f98`（保留既有修复、诊断与角色文件）、`bf950b2`（提取动作元数据、独立反编译对照与注释动作回归）。当前任务尚未全部完成；最后一次记录不代表未完成项已验收。

2026-10-22 停止交接：用户要求停止后续实现与测试，只导出资源并提交 Git，剩余由用户 Debug。已停止正在运行的回归，不再自动推进下方剩余事项。运行时与审计修改已提交 `994bc07`，打包工具与本交接文档另行提交。资源和发布包保留在下述导出目录，未解决的资源引用没有填充替代素材。

## 原始需求

以下为整合排序版（重复条目已合并，原文未改）：

---

## 一、bug（优先）

- **bug：** mud 沾染角色部分对比原版，原版有一段时间的去除效果，目前迁移效果是立即移除
- **bug：** SlimePlant的实现逻辑存在未初始化变量 需要额外验证原版游戏所有怪物/地图资源是否可以正常进入，正常加载
- **bug：** 右上角 fps 不会随着游戏帧率改变
- **bug：** 观测到，chara 目录为空，推测 rust 导出脚本有问题

---

## 二、性能调整

- **性能优化：** 目前测试中，play ground 中，越往后面的场景活动，游戏越卡顿，需要修复
- **性能提升：** 基于上面的帧率检测 log，分析场景然后性能优化，目前发现一些有 vore 属性的怪物有轻微掉帧
- **新增需求：** debug 状态下，需要监控性能变化为单独的一个 log，在帧数掉了 5 帧之后记录当前角色，当前活动的 npc，当前地图，粒子信息

---

## 三、小需求

- **新增需求：** 汉化需求
- **新增需求：** 原生 60 帧支持 要求：注意游戏的 delta，避免游戏提升帧率引起加速
- **测试：** 获取原版所有按键功能，并且对迁移版测试

---

## 四、大需求

### 新增大量需求：基于 ai 分支，使用新分支开发

当前游戏只有 gm 的兼容层  
现在需要同时加入对 godot 的适配  
用途：避免编写 gm 代码，去修改/新增内容

**测试：**

- **新增动作：** 通过 c# 脚本编写动作，需要可以使用 godot 的依赖库，验收：角色新增“三段跳“为测试动作，动画目前统一为 yuki 的旋转跳跃风格，能够正常触发即成功，三段跳可以正确让角色触发第三段的跳跃效果
- **修改动作：** 修改 nana 的一段跳跃，将动画改为旋转 360°后落地验收标准参考新增动作  
  修改 nana 的冲刺（双击 a/d 方向）效果，使其距离变长，间隔减短

### 新增大量需求：需要基于当前 ai 分支，开启一个新的分支

制作一个游戏内自带的角色编辑器，放在主界面  
独立于 v 键选择角色

**需求和验收标准：**

- **角色绘制：** 能够绘制角色帧图图像，可以预览角色的动画状态机，能够绘制比如尾巴/帽子一类额外的饰品，进行模拟的角色绘制验收，编写 Python 脚本比对角色绘制前后状态，Python 脚本只返回像素一致性和一些修改信息，支持 Ctrl-z 撤回操作，支持选择像素区域，支持 Ctrl+c 复杂选区，Ctrl+v 粘贴选区
- **保存：** 正常保存绘制的角色帧图
- **加载：** 正常加载绘制的角色帧图
- **被检测：** 游戏过程内，v 键选择角色可以识别

### 新增大量需求：需要基于当前 ai 分支，开启一个新的分支

制作一个游戏内自带的地图编辑器，地图编辑器的功能要求：

- **编辑：** 正常编辑游戏区块，正常放置实体，需要编写测试并且启动无渲染游戏界面读取实体信息是否正确，进行角色操作验证区块是否正常加载
- **编辑器性能要求：** 进行有渲染游戏界面测试，通过一个测试，搭建一个与 test room 一致的地图，然后放置实体，检测游戏帧率是否下降，需要检查 ui 交互功能
- **保存：** 提供一个保存位置选项，调用系统的文件管理器以选择保存位置，确保保存内容正常（在加载部分验证）
- **加载：** 加载游戏地图，验证是否正常，对 test room 的导入时间规定在 3s 内加载完成

--

强制指令需求:

- 不Load Skill
- 不读不必要的日志和图片信息
- 将我的需求保存为一个固定的文档,现在修复bug和性能修复,其他需求暂不完成

## 验证记录

### 已落地的修改

- `Runtime/GmGameplayFixes.cs` 在原有标题菜单、次要射击修补基础上，修复 SlimePlant 对未定义 `WARNING` 的引用，以及 Haruki 空 switch 对未定义 `STAND` 的引用。补齐 `objJungle` 原脚本注释中的初始化默认值。
- 过场角色先初始化共用动作常量为空动作，再加载各自的过场动作。服饰加载使用正在创建的角色作为精确目标；过场初始化期间保存、切换并恢复 `PartCreator`、`LoadOnPlayerDoll`、`ForceLoadOnDoll`。修复贴身服饰在进入 `with` 之前误用 `other.chara` 的上下文，并跳过尚未创建的目标部件。
- `GmWorld`、`GmEvaluator`、`GmExecutable` 保留当前事件中已销毁对象的 `self/other` 字段访问，同时普通实例查询仍排除已销毁对象。该问题由 Credit 的自毁粒子复现。
- `GmWorld.RemoveDestroyed` 在删除实例后使实例选择索引失效，防止删除后创建同数量新实例时遗漏新对象。最后一轮回归测试覆盖了该情况。
- `GmFrameMonitor` 基于单调时间统计实际完成的逻辑帧和渲染回调；切图、目标速度变更和模态等待重置采样。`GameHost` 更新右上角标签、引擎 `fps` 和全局 `current_fps`。
- `GmPerformance` 在 debug 下默认启用独立 JSONL 日志；`SLACK_PERF=1` 强制启用，`SLACK_PERF=0` 关闭。路径为用户数据目录下 `logs/performance-日期-时间-PID.jsonl`，记录角色、活动 NPC 名称/位置/动作、地图、对象计数、粒子/发射器、图形资源及阶段耗时；`SLACK_PERF_SCRIPTS=1` 可额外采集脚本热点。
- 激活对象复用对象选择缓存；内部区域激活/休眠复用空间索引。离屏 `objFluid` 和 `objFluidInWater` 按剩余 alarm 寿命清理，避免休眠冻结 alarm 导致持续累积。普通休眠游戏对象不自动到期。碰撞事件枚举顺序保持原实现。
- 删除动态 sprite 时清理碰撞遮罩缓存与无其他 sprite 引用的生成纹理缓存；surface 释放时清理像素/纹理缓存。纹理不立即强制 Dispose，避免尚未执行的绘制命令引用失效。文件纹理加载后释放临时 Image。
- GML 数值表达式比较使用 `1e-13` 容差；switch 匹配保持精确比较。无效 sprite 的 x/y offset 查询返回 0。无渲染模式截屏生成空白纹理，使依赖屏幕捕获的对象可以进行逻辑检查。
- Rust 提取器把原始 `Chara` 文件一并复制到 `GameData/assets/Chara`，本地已恢复约 46 MB 文件。压缩 WAV 经 `ffmpeg` 转为 PCM16，修复原始 sound137 的 ADPCM 加载问题。原始缺失 BGM 输出 `AUDIO_MISSING`，不再导致关卡中断。
- 提取器保留注释动作及其原始元数据、资源槽位数量与设置；非空但尚不支持的资源类别和解析错误直接失败。运行时同步/异步动作缓存跳过 `kind=0, execution=0` 的注释动作，避免注释占用条件分支体。
- `Tools/decompile-audit.mjs` 独立读取官方反编译器输出的 GM81，与提取结果比较对象、脚本、房间、动作元数据、内嵌图片像素和声音等。当前 EXE 实际为 419 个对象槽位，其中 48 个空槽、371 个对象；paths、timelines、triggers、included files、extensions 均为零。不能将未使用的资源类别推断为本次缺资源的根因。
- `Tools/export.mjs` 为 Linux/Windows 生成 ZIP，附带 `package-files.json`；`Tools/package-audit.mjs` 核对清单、SHA256、资源引用和整个源 GameData。`--package-resources-audit` 实际验证动态编译、角色扫描及 Godot 图像/声音解码。
- 图形审计新增共享 sprite 删除、最后引用缓存释放、反复 alpha 替换与延迟 GPU 绘制检查；alpha 替换后清除旧像素缓存并释放临时 Image。菜单测试新增对实际控件生命周期的检查。
- 角色 PopupMenu 使用原生子窗口，绕过 Godot 4.7.2 发布模板内嵌弹窗的父信号清理错误；避免重复 Hide。角色 UI 审计不依赖静止窗口的绘制信号，图形审计则显式提交 GPU 绘制。最新 Linux 发布包角色审计通过，最新原生弹窗尚未在 Windows 复验。
- 新增 `Tools/content-audit.mjs` 和 `Runtime/GameHostContentAudit.cs`，覆盖地图加载、怪物生命周期、泥浆残留及往返性能检查。需求之外的汉化、原生 60 帧和编辑器等均未实施。

### 已执行的检查

| 检查 | 最近已完成结果 | 范围与限制 |
| --- | --- | --- |
| `dotnet build --no-restore` | 成功，0 warning、0 error | 已覆盖泥浆、绑定和资源审计扩展 |
| `dotnet run --project Tests/RuntimeTests.csproj --no-restore` | PASS，2,191 条断言 | 新增同步/异步条件与注释动作检查，其余包含 FPS、日志、激活、液滴、加载目标、自毁引用和实例缓存回归 |
| `node Tools/verify.mjs` | 通过 | 98,098 AST 单元、110 地图、371 对象、358 脚本及媒体引用 |
| Rust Linux/Windows release 构建 | 通过，0 parse errors | 离线构建，两平台解析器均进入发布包；没有重写全部生成元数据 |
| 独立反编译对照 | `DECOMPILE_AUDIT_OK` | 官方当前版和 v2.2.0 两份参考分别核对，约 180 万检查；不证明全部运行语义、外部 GIF 解码或字体排版一致 |
| 图形混合与资源生命周期审计 | `GRAPHICS_OK` | 已覆盖缓存/引用修改后的 blend 0..6、triangle 4/5/6、surface 刷新与删除后实际 GPU 绘制；Linux 和 Windows/Proton 均已运行 |
| FPS 实际降速审计 | `FPS_AUDIT_OK` | 最新 `/tmp/ccgame-fps-accepted.log` 标签降为 16；独立日志写出 drop 记录，逻辑/渲染均有数据 |

地图与怪物完整枚举的结果位于 `/tmp/ccgame-content-checkpoint.json`：地图 110/110、怪物 51/52 通过。该轮检查跨越了若干运行时构建，覆盖加载与几十帧短时运行，不能等同于最终单一构建的全剧情通关或全部按键验收。SlimePlant 已生成并运行 180 帧。BossMachine、BossParasite 在各自原始 Boss 关卡检查约 210 帧，不能当作独立 Playground 生成通过。

资源来源仍需核实：所检查的 `SlackOfDescent_Dev/Chara/RubberWorm/Picture` 包含 `MaskBody.gif`、`MaskHead.gif` 等，而 `set_data.txt` 引用 `Body.gif`、`Head.gif`、`Mouth_Inside.gif` 等；Stage6 的 `Sound/BGM/Stage6_4.mp3` 引用也未对应上。该事实仅说明所检查资源树与脚本不一致，不能推断所有原版发行版都缺失，也不代表用户批准豁免。没有伪造替代素材，不能宣称全部资源均可完整显示/播放。

绑定专项 `/tmp/ccgame-bind-complete.json` 加上两项复验 `/tmp/ccgame-bind-boss-final.json`，汇总为 31/32 通过，剩余 RubberWorm。每类执行三轮绑定 180 帧、释放 60 帧；除 VorePlantOriginal 使用落下碰撞触发外，其余由脚本设置绑定阶段，不代表所有自然捕获入口已验收。BossHitoBrain、BossWorm 的失败来自审计生成位置，调整场景布置后复验通过，未改怪物脚本。

### 泥浆检查

最新 `/tmp/ccgame-mud-latest.log` 检查通过：身体 `qKOF` 为 12.6039 → 离开一帧 12.6300 → 再过 120 帧 11.0255；服饰为 25.4200 → 25.5590 → 15.8822。覆盖层绘制对应的非透明源像素为 7,111 / 7,226。实际水体碰撞使总量 26.9077 → 一帧 22.2146 → 120 帧后 12.1960，并检查到负清洗状态。像素计数来自被调用的覆盖层源纹理，不是全画面像素一致性测试。

原脚本 `qsMultiDrip` 有 `(x)&(y)&(visible)` 位运算门槛，服饰默认位置 `(32,32)` 会挡住该分支。审计记录被挡住的 8 件服饰，再仅在审计中把其锚点设为 `(1,1)`，验证衰减分支；普通游戏未修改此门槛。因此身体、受控服饰和水洗检查通过不代表普通玩法全部服饰已验证。目前没有复现用户报告的“立即移除”，没有确定根因，BUG-01 不标记修复完成。

### Windows 发布包

旧 `/home/dpapyru/ccgame-builds/SlackOfDescent-Windows-x64.zip` 仅含 251 个 Chara 文件，`Chara/Doll` 下为零。新的 ZIP 每平台包含 14,375 个清单文件，其中 3,565 个 Chara 文件；解压后与源 GameData 的文件列表及 SHA256 全量一致。新增清单也覆盖 EXE、PCK、解析器与自包含 .NET 运行时。

资源运行审计已在 Linux 和 Windows/Proton 通过：动态 UTF-8 编译、角色扫描、10,059 个图片和 803 个声音解码。测试取消 `SLACK_SOURCE`、使用新用户目录、从 `/tmp` 启动，解压路径包含中文与空格。Steam Proton 版本为 `proton-10.0-4b`；系统 Wine 9.0 连 `--version` 都崩溃，不能用该 Wine 失败判定 Windows 包损坏。未测试真实 Windows 机器。

最终导出目录 `/media/dpapyru/e68fb9c4-1943-4ed4-9fe1-e9295d2f5c55/ccgame-final-builds/`，包含独立资源包 `GameData.zip`（约 168 MB，ZIP 完整性检查通过），以及 `Linux.zip`、`Windows.zip` 和两个平台的展开目录。

最新 ZIP 解压目录 `/media/dpapyru/e68fb9c4-1943-4ed4-9fe1-e9295d2f5c55/ccgame-accepted-unzip/发布 解压/` 的 Linux/Windows 清单哈希核对均通过。最新运行回归 `/tmp/ccgame-package-accepted.json` 中 Linux 资源、GPU 图形、角色 UI 三项通过；用户叫停时 Linux 完整玩法正在运行，已停止，Windows 最终整套回归未运行。较早构建的 Linux 完整玩法，以及 Windows/Proton 资源与 GPU 图形已通过，不等于最终构建八项全部通过。未宣称原生 Windows 全面正常。

### 性能测量

受控路线为 Playground 三轮 `[96,2000,4000,6000,8000,6000,4000,2000,96]` 往返，每段 90 帧；相同随机种子、关闭性能日志、依次运行，使用独立用户目录。基线项目位于 `/tmp/ccgame-perf-baseline`，在当时工作区副本中仅回退对象/区域激活查询和离屏液滴到期清理，其他游戏修复保持一致。

| 指标 | 基线 | 优化版 |
| --- | --- | --- |
| 27 段平均单帧 CPU 耗时 | 26.28 ms | 21.05 ms |
| 第三轮后段 x=8000 平均帧耗时 | 30.69 ms | 19.98 ms |
| 最后返回起点平均帧耗时 | 20.19 ms | 14.21 ms |
| 三轮结束实例数 | 3,755 | 2,992 |
| 稳定后生成纹理数 / surface 数 | 1,002 / 17 | 1,002 / 17 |

日志为 `/tmp/ccgame-perf-controlled-before.log`、`/tmp/ccgame-perf-controlled-after.log`。平均帧耗时下降约 20%，只代表本机这条路线的 CPU 测量，不能换算为全游戏统一 FPS 提升。

优化版另有有渲染三轮测试 `/tmp/ccgame-route-rendered.log`：起点各轮约 56–58 FPS，x=8000 约 41–46 FPS，最重中间路段约 34–39 FPS；生成纹理 1,002、surface 17 稳定，结束实例数 2,992。此审计每个渲染回调主动推进一步，用于比较绘制负载，不是原生 60 帧游戏模式。CPU 往返与该有渲染测量都发生在最终实例索引失效修复之前，应在恢复后针对该修改复核。

### 用户接手事项

1. 完成最终双平台 ZIP 的运行回归，包含资源解码、GPU 图形、角色菜单与完整菜单/关卡/武器往返。Windows 使用已获准的 Steam Proton，真实 Windows 未覆盖。
2. 完成最终 Playground 和绑定热点的顺序受控性能测量。实际 FPS 下降及独立日志已复测通过；功能枚举期间的并行耗时不能作为性能证据。
3. BUG-01 仍未复现；RubberWorm 和 Stage6_4 的资源来源仍未解决。保持这些项目的未完成状态，不以审计通过或未经批准的豁免代替修复。
4. 以上均由用户继续 Debug；本轮仅导出和提交现有成果，不自动合并，也不实施 LATER 项目。

运行环境：项目 `/home/dpapyru/ccgame`，分支 `fix/extraction-fidelity`；Godot 为 `/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64`。原版目录 `/home/dpapyru/桌面/.game/BaiduNetdiskDownload/SlackOfDescent_Dev`；按需读取的原脚本文本索引 `/home/dpapyru/.cache/ccgame-migration/source.json`。不读取无关日志或图片，不加载 Skill。

建议恢复命令（`GODOT_BIN` 设为上述可执行文件；每次使用独立临时用户目录）：

```sh
dotnet build --no-restore
dotnet run --project Tests/RuntimeTests.csproj --no-restore
node Tools/verify.mjs
env SLACK_PERF=0 SLACK_USER_ROOT=/tmp/ccgame-mud-resume "$GODOT_BIN" --headless --path /home/dpapyru/ccgame --log-file /tmp/ccgame-mud-resume.log -- --audit --room 10 --frames 30 --dialog-choice 1 --mud-residue-audit
env GODOT_BIN="$GODOT_BIN" SLACK_AUDIT_OUTPUT=/tmp/ccgame-content-resume.json node Tools/content-audit.mjs all
```

`--fps-audit` 会在测试模式主动降速；普通游戏没有该限速注入。审计工具支持 `--route-audit`、`--animation-audit route`、`--content-frames`；`Tools/bind-audit.mjs` 支持 `--rendered` 与 `SLACK_AUDIT_PROJECT`，用于独立副本的受控比较。

工作区既有修改和新增 Chara 文件已由 `cf93f98` 检查点保留，没有回退用户改动。后续提交仍在开发分支，不自动合并。
