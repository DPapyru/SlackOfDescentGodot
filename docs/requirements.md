# 需求记录

固定维护位置：`docs/requirements.md`。以下“原始需求”保留用户提供的内容；实施状态另行记录。

## 实施状态

本轮仅实施 bug 与性能修复，以及支撑性能修复的 debug 独立日志。保留当前 `ai` 工作区已有改动，不自动提交或合并。不加载 Skill，不读取不必要的日志和图片。

| 编号 | 项目 | 状态 |
| --- | --- | --- |
| BUG-01 | mud 沾染逐步去除 | 尚未复现立即移除；身体残留与绘制检查通过，服饰及水洗扩展检查待运行 |
| BUG-02 | SlimePlant 初始化及全地图、怪物加载验证 | SlimePlant 修复已验证；110 张地图逐项复验通过，52 类怪物中 51 类通过，1 类缺原始资源 |
| BUG-03 | 右上角 FPS 实测更新 | 已实现，实际降速测试通过 |
| BUG-04 | chara 导出与加载完整性 | Rust 导出修复、全量提取与本地资源恢复完成；最终导出包验证待完成 |
| PERF-01 | Playground 后段及持续运行退化 | 已优化并完成受控及有渲染路线测试；最终修改后的复核待续 |
| PERF-02 | vore 怪物热点分析与优化 | 共用激活、液滴及资源缓存已优化；各怪物绑定/吞噬动作的专项性能验收待续 |
| PERF-03 | debug 掉帧独立日志 | 已实现，模拟采样与实际降速写入检查通过 |
| LATER-01 | 汉化 | 待办 |
| LATER-02 | 原生 60 帧及 delta 适配 | 待办 |
| LATER-03 | 原版全部按键功能对照测试 | 待办 |
| LATER-04 | 新分支实现 Godot/C# 动作适配 | 待办 |
| LATER-05 | 新分支实现角色编辑器 | 待办 |
| LATER-06 | 新分支实现地图编辑器 | 待办 |

性能日志采用已确认的“相对稳定帧率”基准：进入地图预热 3 秒，再采集 5 个一秒窗口，以中位数建立基准；下降至少 5 FPS 时记录，持续掉帧最多每 5 秒补充一次，切图重建基准。逻辑与渲染帧率分开统计。保持原版逻辑步长，本轮不实施原生 60 帧改造。

2026-10-22 检查点：用户要求先记录、后续继续，本轮在完成记录后停止。当前任务尚未全部完成，不自动提交或合并。下方“继续执行入口”列出剩余工作。

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
- 新增 `Tools/content-audit.mjs` 和 `Runtime/GameHostContentAudit.cs`，覆盖地图加载、怪物生命周期、泥浆残留及往返性能检查。需求之外的汉化、原生 60 帧和编辑器等均未实施。

### 已执行的检查

| 检查 | 最近已完成结果 | 范围与限制 |
| --- | --- | --- |
| `dotnet build --no-restore` | 成功，0 warning、0 error | 最后一次成功在泥浆服饰/水洗审计扩展之前 |
| `dotnet run --project Tests/RuntimeTests.csproj --no-restore` | PASS，2,183 条断言 | 包含 FPS、渲染独立下降、日志字段、激活、液滴到期、精确加载目标、自毁后引用和实例缓存回归 |
| `node Tools/verify.mjs` | 通过 | 98,098 AST 单元、110 地图、371 对象、358 脚本，媒体帧/背景/字体/声音文件检查；最后一次成功在移除 Cargo.lock 自动头注释之前 |
| Rust release 构建及完整提取 | 通过，0 parse errors | 离线构建；提取到 `/tmp/ccgame-extracted-fixes`，仅回填 Chara 原始文件与 sound137，没有覆盖全部生成元数据 |
| 图形混合与 surface 刷新审计 | `GRAPHICS_OK` | 有渲染，blend 0..6、triangle 4/5/6 及 capture 后 surface 刷新；在后续缓存/引用修改前执行，最终回归待续 |
| FPS 实际降速审计 | `FPS_AUDIT_OK` | 原逻辑速度下实际限速，标签降为 14；独立日志写出 drop 记录，逻辑/渲染均有数据 |

地图检查通过结果为“完整枚举后，对失败项逐项修复复验”的汇总，不是最终工作区一次性重跑的 110/110。覆盖加载与几十帧短时运行，不能等同于全剧情通关或全部按键验收。最终实例索引修复之后尚未完整重跑所有地图。

结果文件依次汇总为 `/tmp/ccgame-rooms.json`、`/tmp/ccgame-rooms-recheck.json`、`/tmp/ccgame-rooms-latest.json`、`/tmp/ccgame-rooms-target.json`、`/tmp/ccgame-rooms-demo-final.json`、`/tmp/ccgame-credit-fixed.json`，同名地图取后者，得到 110/110 通过。SurvivalWorld 的最新默认值修复已在 `ccgame-rooms-latest.json` 中通过。

怪物结果汇总 `/tmp/ccgame-enemies.json`、`/tmp/ccgame-enemies-recheck.json`、`/tmp/ccgame-boss-context.json` 为 51/52 通过。SlimePlant 已生成并运行 180 帧；BossHito、Haruki 已复验。BossMachine、BossParasite 依赖关卡对象，改为在各自原始 Boss 关卡运行约 210 帧，不能当作独立 Playground 生成通过。

原始资源限制：`RubberWorm` 的 `Chara/RubberWorm/Picture/Body.gif` 等脚本引用图片在原版目录中就不存在，尚未补造资源；部分 Stage6 地图引用的 `Sound/BGM/Stage6_4.mp3` 原版也不存在，目前以缺失诊断继续运行。不能宣称原版全部资源均可完整显示/播放。

### 泥浆检查

已执行的身体检查结果：总 `qKOF` 沾染后为 12.6039，离开一帧后为 12.6153，再过 120 帧为 11.4133。对应覆盖层绘制的非透明源像素为 3,777 和 4,031，证明当时既有残留状态，也有带透明度的覆盖层绘制。像素数会随姿态/帧图变化，不是画面整体像素一致性测试。

目前没有复现用户报告的“立即移除”，也没有确定该现象的根因，因此 BUG-01 不标记修复完成。最后一次代码编辑已扩展 `--mud-residue-audit`：合并身体与服饰残留采样，并加入实际水体碰撞清洗；此扩展尚未编译或运行，恢复后优先验证。不要把此前仅身体检查通过当作扩展检查已通过。

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

### 继续执行入口

1. 先编译并运行最新泥浆服饰/水洗审计；若失败，修正审计或实际问题并区分两者。继续定位用户所述立即移除，保留“未复现”状态直到有证据。
2. 运行 runtime tests、资源核验、`git diff --check`。最近通过断言为 2,183；最后的泥浆审计扩展及 Cargo.lock/verify 清理尚未验证。
3. 复核共用服饰加载目标与过场初始化的行为，确认未影响 V 键换角色；对最终实例缓存修复重跑代表性地图、SlimePlant、液滴路线及图形审计。不要为了通过审计忽略全部未初始化变量。
4. 针对 vore 怪物实际绑定/吞噬动作进行热点采样；目前仅有共用路径优化及生命周期检查，专项验收未完成。
5. 检查生成纹理的共享引用/删除和角色重载资源寿命；当前已避免立即 Dispose 破坏延迟绘制，最终稳定性仍需检查。
6. 验证最终 Linux 导出包能脱离原版目录使用自带 Chara 资源运行；Windows 打包/运行验证尚未在本轮完成。`Tools/export.mjs` 会依次生成两个平台，执行前检查对应编译器与 Godot 模板。
7. 写回最终状态与限制，保留原始需求和用户既有修改；没有提交、合并或后续大需求开发授权。

运行环境：项目 `/home/dpapyru/ccgame`，分支 `ai`；Godot 为 `/home/dpapyru/公共/Godot/Godot_v4.7.2-stable_mono_linux_x86_64/Godot_v4.7.2-stable_mono_linux.x86_64`。原版目录 `/home/dpapyru/桌面/.game/BaiduNetdiskDownload/SlackOfDescent_Dev`；按需读取的原脚本文本索引 `/home/dpapyru/.cache/ccgame-migration/source.json`。不读取无关日志或图片，不加载 Skill。

建议恢复命令（`GODOT_BIN` 设为上述可执行文件；每次使用独立临时用户目录）：

```sh
dotnet build --no-restore
dotnet run --project Tests/RuntimeTests.csproj --no-restore
node Tools/verify.mjs
env SLACK_PERF=0 SLACK_USER_ROOT=/tmp/ccgame-mud-resume "$GODOT_BIN" --headless --path /home/dpapyru/ccgame --log-file /tmp/ccgame-mud-resume.log -- --audit --room 10 --frames 30 --dialog-choice 1 --mud-residue-audit
env GODOT_BIN="$GODOT_BIN" SLACK_AUDIT_OUTPUT=/tmp/ccgame-content-resume.json node Tools/content-audit.mjs all
```

FPS 已通过测试的记录位于 `/tmp/ccgame-fps-real-v2.log` 和 `/tmp/ccgame-fps-real-v2/logs/`。`--fps-audit` 会在测试模式主动降速；普通游戏没有该限速注入。审计工具支持 `--route-audit`、`--animation-audit route`、`--content-frames`，其余用法见对应代码。

工作区注意：开始时 `GameHost.cs`、`GameHostControls.cs`、`GmAudio.cs`、`GmBuiltins.cs`、`GmInput.cs`、`GmWorld.cs`、`Tests/Program.cs`、`Tools/export.mjs` 已有未提交修改，另有 bindings/regression/paths/gameplay fixes 等未跟踪文件。`.gitignore` 的改动来自其他来源，未回退。不要覆盖这些内容。新增 Chara 文件及当前所有改动留在工作区，未提交。

停止前仅核对了需求文档与相关进程；没有遗留的 Godot、内容审计、runtime test 或导出进程。停止记录不代表剩余验收已完成。
