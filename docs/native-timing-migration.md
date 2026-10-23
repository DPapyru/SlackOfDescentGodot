# 原生 60 Hz 时序迁移清单

2026-10-24。本文记录只读审查及可执行迁移边界，不代表原生 60 Hz 已完成。

## 已实现范围

`Runtime/GmNativeTiming.cs` 提供独立换算函数；`Tests/NativeTimingTests.cs`
包含 330 条断言。独立 net8 runner 在 `/tmp/ccgame-timing-fresh` 运行通过，
输出 `NATIVE_TIMING_HELPERS_PASS 330 assertions`。测试不依赖 Godot，不运行渲染性能测试。

这些函数尚未接入 `GmWorld` 或 GM AST 执行器。当前宿主虽有 60 Hz 固定时钟，
仍由 `LegacyDue(room_speed)` 决定是否执行 GM 世界步骤；30 Hz 房间中的 GM
移动、碰撞、脚本和动画仍然每秒执行 30 次。不得将辅助函数通过或渲染 60 FPS
记为原生 GM 60 Hz 验收通过。

## 实际代码清点

清点输入为当前 `GameData/code.json`、`manifest.json`，按 AST 节点逐个统计，
不是用字段名称推断全部运行语义。静态计数包含初始化和循环，不能直接作为要修改的数量。

| 项目 | 数量 | 含义 |
| --- | ---: | --- |
| 30 Hz 房间 | 83 | 正常玩法房间 |
| 3000 Hz 房间 | 27 | 加载房间，应作为加载任务执行 |
| AST 代码单元 | 98,098 | rooms 94,293、external 1,997、objects 1,443、scripts 358、library 7 |
| 普通 Assign 节点 | 581,262 | 包含 `x=x+v` 等非复合累加，不能只处理 `+=` |
| 复合赋值节点 | 2,891 | Add 1,642、Subtract 1,152、Multiply 94、Divide 3 |
| 复合赋值目标名称 | 379 | 名称相同不保证语义相同 |
| 含复合赋值的代码单元 | 528 | external 628、objects 1,899、rooms 70、scripts 294 个节点 |
| for 控制部分的复合赋值 | 605 | 例如 `i` 482 处，不能按 delta 缩小循环增量 |
| 含 random 家族调用的代码单元 | 369 | random 2,970、irandom 229、choose 2、random_range 25、irandom_range 27 |
| 条件中含 random 家族调用 | if 883、switch 111 | 包含概率判断与选项选择，不能一律换算随机返回值 |

复合赋值的主要目标包括 `x` 386、`BindCounter` 124、`PlayerST` 123、`y` 74、
`PartsNumber` 71、`qsSuctionP` 69、`JumpNum` 59、`hspeed` 52、`vspeed` 50、
`image_alpha` 28、`Angle` 22。`JumpNum` 等离散计数不应乘以 0.5；
`PlayerST` 同时存在持续消耗和一次性消耗，必须逐调用点分类。

## 统一单位与接线顺序

1. 玩法时钟固定 `dt = 1/60` 秒；原 GM 值仍以原逻辑步为单位，
   30 Hz 内容的 `legacyUnitsPerTick = 30/60 = 0.5`。
   原生 `FrameContext` 与 `GmWorld.GameTime` 使用同一已完成模拟时钟。
   3000 Hz 加载任务不推进玩法时间，模态暂停不补执行积压输入。
2. 让每个 60 Hz tick 都运行 GM Begin Step、普通 Step、运动、碰撞、End Step。
   `Frame` 统计实际完成的 tick，独立 FPS 监测目标同步改为 60。
   仅删除宿主 `LegacyDue` 会使下列内容全部加速，不能单独提交为完成。
3. 先迁移世界内置时序，再迁移有明确语义的共享动作脚本，再按具体代码位置迁移
   玩家、怪物、环境、Draw 的持续更新和离散副作用。保持未知脚本可定位、可回归，
   避免根据变量名称全局改写所有 AST。

## 世界内置时序

| 位置 | 必须修改 | 可执行验收 |
| --- | --- | --- |
| `GmWorld.StepAsync` GameTime | 每玩法 tick 加 1/60，不再以 room_speed 决定时间 | 60/120 tick 分别为 1/2 秒；加载与模态不增加玩法时间 |
| 12 个 alarm | 读取 double，每 tick 减 legacyUnitsPerTick；跨零触发一次；保留 0 和禁用 -1 | `alarm=30` 恰在第 60 tick 触发一次；支持触发后重设；不通过 Int 丢失半步 |
| gravity、friction、速度位移 | 速度继续存原 GM 单位；每 tick 以半步积分；速度赋值和冲量不重复减半 | 恒速每 tick 都移动；原跳跃起点/顶点/落地时间、停止距离及碰撞速度对照 |
| image_index/image_speed | fractional image_index，每 tick 增加 image_speed*0.5；按实际越过的周期产生事件 | 多周期高速、逆放、无效 sprite、0 帧不重复产生 animation-end |
| `GmRooms.UpdateBackgrounds` | background_hspeed/vspeed 按半步推进 | 一秒端点与原 30 Hz 一致、每 tick 有增量 |
| `GmParticles.Update` | 分离连续轨迹/衰减与寿命、死亡、步发射、流发射；随机发射保留事件频率 | 一秒粒子寿命/数量/分布与原版比较；轨迹以 60 Hz 推进 |
| 休眠液滴到期 | `InactiveEffectExpiry` 不能再直接用旧 alarm 数值加到 native Frame | alarm=30 的休眠液滴 60 tick 到期；普通休眠对象不自动销毁 |
| 碰撞 | 每 tick 检测并处理位置；持续伤害按明确的语义事件与实例对限频 | 薄墙、高速弹、接触持续一秒、多个接触对象、首次碰撞立即响应 |

`GmNativeTiming.Accelerate` 的端点保持公式：旧速度为 v、每原步加速度为 a、
本 tick 原步单位为 f，先更新速度 `v+a*f`，位移使用
`v*f + a*f*(f+1)/2`。两个 f=0.5 的步骤与原一次半隐式 Euler 的速度及位置一致。
这个证明只适用于恒定加速度；摩擦停止钳制、碰撞冲量、脚本修改速度需要单独检查，
不能据此宣称全部物理已等价。

## 共享动作与非运动时间

`objects/objDoll/3/0/3` 同时包含：

- 持续姿态累加：`PosX/Y += chgPosX/Y`，子部件的 `Angle`、`ScaleX/Y`、
  `PartPosDir/Dis`，绑定姿态 `BindPosX/Y`。
- 原步计时：`CL += 1`、条件成立后 `Step += 1`，`BindStep += 1`。
- 离散循环控制：`for i += 1` 和 `Process[i]` 选择。

`scripts/DoubleMotionStep2` 有同样的子部件变化、`DoubleCL` 和 `DoubleStep`。
`scripts/DoubleMotionStep` 以及 `objDoll` 的其他 Step 代码比较 Step 与 motStep，
设置 KEYFRAME、nextStep、MOTIONEND。必须迁移整条状态机，而不能将
`CL += 1` 和 `Step += 1` 同时减半：二者分别是累积时间和条件推进的动作格索引。

可执行验收：对 Nana/Yuki/Ryoko 每个共享动作记录每 tick 的 MotionNum、
KeyFrmNum、Step、CL、KEYFRAME、MOTIONEND、PosX/Y 及部件角度；
原 30 Hz 与新 60 Hz 每两个 tick 对齐比较，额外检查中间 tick 姿态确实变化。
MotionChange、alarm[0] 过渡、KOF>1、零长格、DoubleMotion、绑定/解绑都要覆盖。

明确的非运动候选还包括：

| 代码位置 | 旧逻辑 | 验收点 |
| --- | --- | --- |
| `objects/objEnemyBullet/3/2/0` | Time += 1，Time>300 销毁 | 保留原版检查/累加的先后顺序和约 10 秒寿命 |
| `objects/objFrag/3/0/0` | Time>150 爆炸，然后 Time += 1 | 爆炸只一次，约 5 秒，保留原阈值的一步偏移 |
| `objects/objSlimeFluidTemp/3/0/0` | Time>150/300、命中和销毁 | 命中即时响应、无重复伤害/声音/液滴 |
| `objects/objFogController/3/0/0` | Phase、Time+=0.05、FogAlpha 渐变 | 一秒相位与淡入淡出实际时长一致 |
| `external/Chara/BossMachine/step.txt` | Counter += 1 | 发射/状态切换次数与原版一致 |
| `external/Chara/{MouthWorm,RubberWorm,Vix}/step.txt` | CountDown 递减与比较 | 等待实际秒数相同、跨阈值不丢事件 |
| `objects/objController/3/0/28` | DeathEffectWait；按键边沿增加 BindCounter | 持续时间减半步，单次按键贡献保持完整 |

## 离散事件、概率及 Draw

- 使用前值到后值的跨阈值检测，避免 fractional 时间跳过 `==`。
  `Crossed` 起点开区间、终点闭区间，停留或离开相同阈值不重复触发。
  KEYFRAME 是事件脉冲，不应在两个 native tick 都为 true。
- 持续接触伤害、持续声音/生成、输入按下、动作换帧各自有事件身份。
  `GmNativeEventCadence<TKey>` 可用于已经明确的重复副作用；碰撞键至少包含
  self、other、事件/调用点，销毁和切图清理键。不能全局限制 `instance_create`
  或 `sound_play`，否则会误伤一次事件中的多颗子弹、多部件与多音效。
- 对独立每原步概率 p，以 `p60=1-(1-p)^0.5` 保持每 1/30 秒至少出现一次的概率。
  该公式不保证一秒事件数量或随机种子轨迹完全一致；要求精确事件数时需要明确
  的离散试验时钟。随机大小、方向、位置和选项索引不应使用这个概率换算。
  随机 `switch` 必须先区分每步尝试与已经触发事件内的选择。
- `x=x+v`、`value=max(0,value-1)`、`value=target+(value-target)*.9`、
  Draw 中累计变量均需显式分类。指数衰减应使用每原步倍率的 0.5 次方。
  将所有 `AssignAdd` 乘 0.5 会同时破坏循环、库存、按键贡献和伤害。
- Draw 脚本既参与姿态/服饰绘制，也可能修改世界状态；必须识别其实际每秒执行次数。
  单纯增加渲染次数不能作为动画/逻辑迁移，不能让绘制频率影响游戏副作用。

## 完成标准与当前缺口

辅助函数的数学测试已经通过；宿主、世界、粒子、共享动作状态机及上述具体
内容的接线与 30/60 对照仍待完成。没有运行全地图/怪物 60 Hz 内容回归，
没有完成所有按键、跳跃/冲刺、绑定、服饰、泥浆、随机 AI 的时间一致性验收。

最终验证应使用同一构建、同一输入时间戳和用户目录，先完成无渲染 30/60
轨迹/事件差分，再检查实际 60 Hz tick、动画与输入。保留每项允许的轨迹误差、
随机分布容差和失败样本；不能用已有 30 Hz 的通过记录替代这轮验证。
