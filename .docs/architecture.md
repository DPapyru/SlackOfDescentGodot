# Runtime 模块与动作编辑入口

程序集仍为 `SlackOfDescent`，公开类型仍在 `SlackOfDescent` 命名空间。

| 目录 | 职责 |
| --- | --- |
| `Runtime/Gml` | 原版求值、世界积分、碰撞、Motion 数组与资源兼容 |
| `Runtime/Sdk` | Mod 契约、输入、角色、事件、音频与服务入口 |
| `Runtime/Mods` | 包验证、编译加载、贡献注册与资源归属 |
| `Runtime/Content` | 地图、角色、动作图及持久化数据 |
| `Runtime/Gameplay` | 地图流程、角色预览会话、动作状态机与运动意图 |
| `Runtime/Host/Godot` | 场景、窗口、输入、音频、本地化与 FPS |
| `Runtime/Host/Adapters` | 原版角色与骨架、地图安装、控制绑定、实体及伤害适配 |
| `Runtime/Host/Rendering` | 绘图命令、画布、角色呈现与图形资源 |
| `Runtime/Editors` | 地图、角色、动作与 Mod 编辑操作和界面 |
| `Runtime/Diagnostics` | 可选审计、采样场景与探针 |
| `Tests` / `Tools` | 按领域划分的检查及运行入口 |

## 构建与检查

`dotnet build` 默认不编译 `Runtime/Diagnostics`。诊断构建必须显式指定
`dotnet build -p:IncludeDiagnostics=true`；该开关同时启用 `INCLUDE_DIAGNOSTICS`。
正式导出排除诊断场景和 Tools。FPS 与按需性能采样仍可用于正式构建。

`Tools/diagnostics.mjs` 为领域工具准备当前诊断构建并检查程序集元数据。
`Tools/motion-audit.mjs` 检查动作运行与编辑闭环，默认使用真实图形环境；
`--headless` 只运行功能检查。`Tests/Architecture` 检查正式和诊断程序集中的入口隔离。
`Tools/regression.mjs` 对正式发布包执行启动冒烟检查；完整领域审计运行于诊断构建。

每个原版逻辑步在 End Step 后、动画推进与换房前执行 Draw。
屏幕刷新消费最近生成的命令，合并呈现不省略逻辑 Draw；没有逻辑步时不会因刷新重跑 Draw。

## 动作资产与绑定

`.motion.json` 使用 `MotionGraph` 版本 1，动作帧率固定为 60Hz。
图包含 `Id`、`Skeleton`、`Entry`、`Parameters`、`Clips` 和 `States`。
片段包含 `Duration`、`Loop`、部件 `Tracks`、姿态根轨道 `Root`、阶段区间 `Phases` 和 `Events`。
轨道关键帧保存帧、局部 X/Y、角度、缩放及贴图帧；帧必须严格递增。
原版资源的播放长度短于末尾关键帧时，导入片段会扩展到末尾帧。

位移、缩放线性插值，角度走最短方向，贴图帧阶梯切换。
局部位移相对于父骨骼，角度逐级累积；角色朝向在适配层处理。
`Root` 只改变骨架姿态。实体移动由状态的 `Movement` 单独输出，在世界物理积分前提交。

角色定义中的 `MotionGraph` 是可选的已注册图 ID。未设置时保留旧动作。
`ActionConfigs` 按动作槽保存旧参数动作配置；正式绑定会验证目标、槽位、模式并消费参数。
独立 `.action.json` 格式继续用于参数动作编辑。

用户内容的动作图保存在 `Content/Actions/*.motion.json`，启动时与角色定义一起发现。
Mod v2 在 `content.motions` 中声明图 ID 到包内文件的映射，例如：

```json
{
  "content": {
    "motions": { "example.player": "motions/player.motion.json" },
    "characters": { "example.character": "characters/player.character.json" }
  }
}
```

角色的 `BasePreset` 与图的 `Skeleton` 必须一致，支持 Nana、Ryoko、Yuki 和相应骨架上的自定义外观。
原版部件顺序及别名由 `NativeCharacterBones` 与适配层共享。
原版导入读取现有 Motion AST，处理省略字段、部件顺序、常量算术及确定性三角函数，
默认将 30Hz 原版时间换算为 60Hz。随机调用、非玩家部件索引及不递增的原始时间序列明确报错。
原文件不会被修改；旧文本解析器也会拒绝无法完整解析的表达式。

## 执行与交互

`MotionPlayer` 通过 `IMotionHost` 接收输入、接地、速度、生命及受击事实，输出运动意图和事件，
不访问 Godot 控件或原版实例。转换条件支持输入按住/按下/释放、接地、速度、生命、受击、死亡、
片段结束、计时与图参数。条件全部成立才转换，优先级高者先执行，同优先级按列表顺序；每 Tick 最多转换一次。

运动操作仅包括固定速度、左右输入速度、重力、跳跃及显式位移，不执行任意脚本。
适配层复用世界碰撞和原版伤害过程。输入边沿在原版逻辑步之间累积，避免 60Hz 输入被较低频率的世界步吞掉。
最终骨架姿态在原版 End Step 后提交，贴图帧单独写入 `PartPicture`。

每个角色只允许一个完整控制器。运行时图覆盖角色配置图；播放期间停止旧 Jump/Dash 并拒绝新增冲突绑定。
停止运行时覆盖后可恢复配置图；退出图控制后重新创建旧移动动作。
图只替代普通移动/攻击控制分支，原版受击、死亡、束缚及特殊环境状态优先，恢复后从图入口重进。
武器服务继续处理开火、装填和弹药。房间切换、角色解绑、Mod 卸载及资源归属结束均清理旧播放和图创建的资源。

事件可在指定帧触发，也可在区间内检查接触。区域可绑定骨骼，包含朝向、旋转与缩放变换。
同一状态进入/循环中的同一事件默认对每个目标仅触发一次，可关闭该限制。
支持伤害、实体互动、原生实体生成、销毁、SDK 事件及稳定 ID 地图事件。
显式地图事件入口仍遵守地图的 `Once` 规则。

SDK 通过 `actor.Services.Motions` 提供 `Skeleton`、`Play`、`Stop`、`State`、`Seek`、`Parameter`；
`ActorContext` 也提供 `Skeleton()`、`PlayMotion()`、`StopMotion()` 和 `MotionState`。
地图入口为 `actor.Services.Maps.Trigger(eventId, actor)`。

## 编辑闭环

从动作编辑器打开“骨架与动作图”。窗口包含独立骨架预览、60Hz 时间轴、关键帧列表、
可连线状态节点图、事件配置和图参数页。原参数动作页继续支持 `.action.json`，并可将参数绑定到角色文件。

原版 Motion 从列表导入；编辑轨道、根偏移、阶段、状态、条件与事件后可撤销/重做，
保存为 `.motion.json` 并重新加载。预览使用正式状态机、桥接层、物理和伤害服务，
可放置原版测试实体、单帧运行或连续播放。修改后“重载预览”应用整张图；“预览当前帧”用于姿态采样。
“应用到游戏角色”建立运行时覆盖；“将动作图保存到角色”显式写入角色图引用。
Mod 项目页可以导入动作图并写入 `content.motions`，供包安装与卸载管理。

自建骨架拓扑、怪物 AI、动作混合树与抓取双方同步不属于当前动作图功能。
