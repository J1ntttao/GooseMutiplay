# Untitled Goose Game Steam P2P 联机 Mod 设计

日期：2026-10-26

## 目标

为 Steam 版 Untitled Goose Game（Unity Mono）增加两人 Steam-only P2P 合作模式。每名玩家控制一只鹅，房主权威维护关卡、任务、NPC、物品和交互对象状态。原版单机和本地双人行为保持可用。

## 运行时架构

插件通过 BepInEx 5 加载，以 Harmony 接入 Assembly-CSharp。Steam 层复用游戏目录中的 Facepunch.Steamworks.Win64.dll。模块包括：

- GooseOnlinePlugin：BepInEx 入口、配置和生命周期。
- SteamSession：好友邀请、建房、加入、P2P 发送/接收和断线。
- HostAuthority：房主状态、事件序号、任务判定和快照。
- InputReplicator：输入帧、运动预测和插值。
- WorldReplicator：交互事件、NPC/物品状态和完整快照。
- GamePatches：双鹅输入、场景切换、交互、任务、NPC 和存档写入点。
- RemoteGooseView：把网络第二只鹅映射到游戏已有鹅对象。

Steam 回调线程只写入线程安全队列，Unity 对象修改统一在主线程执行。

## 网络协议

采用自定义二进制包，避免额外网络依赖。包头包含 magic、协议版本、消息类型、会话 ID、序号、tick 和 payload 长度。消息类型包括 Hello、Welcome、InputFrame、TransformSnapshot、ActionRequest、WorldEvent、FullSnapshot、Ping 和 Disconnect。

输入约 30 tick/s，运动快照约 15–20 tick/s，世界事件立即发送，房主约每 2 秒发送完整快照。动态对象由房主分配 NetworkEntityId；静态对象由场景路径和组件索引生成稳定 ID。快照带 stateRevision 和状态哈希，用于发现丢包和漂移。

握手校验插件协议版本、游戏版本和插件版本。所有包长、对象数量、事件频率和字符串长度受限。

## 权威和同步

客户端发送输入和 ActionRequest，房主验证距离、对象状态和任务条件后广播唯一事件。鹅的运动使用输入加快照纠偏；物理对象和 NPC 由房主模拟，客户端插值显示。客户端只预测本地显示，不直接提交世界状态。

场景切换由房主发起，客户端加载相同场景并等待快照确认。房主断线结束会话并返回菜单，不实现主机迁移。联机期间只有房主写入联机存档，客户端保持自己的单机存档隔离。

## 游戏接入范围

优先同步拾取/放置、拖拽物、门/按钮/机关、可破坏物、关键 NPC 状态、任务清单和区域解锁。纯视觉粒子、镜头抖动和本地音效不进入协议。使用运行时类型/方法探测和 Harmony 补丁，必要时通过配置覆盖类名或方法名，以适配当前 Assembly-CSharp 版本。

联机面板使用 F8 打开，支持创建房间、查看 Steam 好友、接受邀请和离开房间。启动默认保持单机行为。

## 实施顺序

1. BepInEx 插件、配置、日志和 Steam 会话骨架。
2. 双实例握手、输入、双鹅移动和插值。
3. 交互对象与物品同步。
4. NPC、任务和场景切换同步。
5. 快照纠偏、错误处理、安装包和测试脚本。

## 验证

进行协议单元测试、无图形逻辑测试、单机双实例联调和两台电脑 Steam 邀请实测。重点覆盖加入/退出、场景切换、拾取放置、NPC 追逐、任务完成、存档隔离和异常断线。

## 已知边界

第一版固定最多两名玩家；Steam-only；不支持主机迁移；不修改原始游戏资源；不同游戏版本或插件协议版本拒绝互联。
