# 原生适配与编辑器实施

固定需求记录：`docs/requirements.md`；本文件记录本轮架构、操作与验证。

实施基线：`ai` 的 `d996bab2df5431a7447e37bce7b46fad54bfa37b`。
交付分支：`feature/native-editors-integration`，不合并回 `ai`。

公共边界：`NativeContracts.cs` 提供 `FrameContext`、输入快照、共享 GM 状态的 `ActorContext`、扩展/动作/实体接口及 `ContentRegistry`。
`NativeContent.cs` 提供版本化角色定义、未知字段保留、用户内容目录及同目录临时文件替换保存。
玩法固定 delta 为 1/60 秒；原版兼容语义的时间迁移单独验证，不以渲染次数替代逻辑更新。

模块所有权：加载器负责 `NativeMods*` / `GmNativeBridge*`；角色编辑器负责 `NativeCharacter*`；地图编辑器负责 `NativeMap*`；公共宿主、输入、时序、汉化与集成由协调者维护。
每个 worktree 独立构建、用户和审计输出。性能验收必须串行进行，不能使用并行测试耗时。

待验收：完整汉化（含图片）、60 帧时间一致性、原版全按键矩阵、真实 Godot 扩展 20 次重载、两个编辑器真实 UI、地图 1227 实例/1647 图块、冷/热各 5 次导入均小于 3 秒、有渲染 60 FPS 与 P95 退化不超过 5%、Linux 与 Windows。
