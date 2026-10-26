# Goose Online

Steam-only two-player online co-op mod for the Unity Mono Steam build of Untitled Goose Game.

## 给朋友的安装方式

1. 把整个 `GooseOnline-0.4.1.zip` 传给朋友。
2. 朋友解压 ZIP，不要直接在压缩包里运行。
3. 双击 `Install-GooseOnline.bat`。
4. 如果 Windows 弹出管理员权限窗口，选择“是”。
5. 安装结束后从 Steam 启动游戏，进游戏按 `F8`。

安装器会自动寻找 Steam 库；找不到时会提示粘贴游戏目录。ZIP 已内置 BepInEx，不需要朋友另外下载 mod loader。

## 卸载方式

在同一个解压目录双击 `Uninstall-GooseOnline.bat`。它只移除 Goose Online 插件和安装标记，会保留 BepInEx，避免影响朋友安装的其他 mod。

## 联机使用

1. 两名玩家都完成安装，并使用相同游戏版本和 mod 版本。
2. 房主进游戏按 `F8`，点击“创建 Steam 房间”。
3. 点击“打开 Steam 邀请面板”，邀请朋友。
4. 朋友接受 Steam 邀请后等待握手完成。

当前版本固定两名玩家、Steam-only、不支持主机迁移。每台电脑使用本机第一控制槽操作自己的鹅，因此键盘、手柄或兼容的键鼠组合都可以使用。插件加载日志位于游戏目录的 `BepInEx\LogOutput.log`。

0.4.1 会按 P1/P2 身份映射远端鹅，并在联机后自动激活 P2；移动快照使用低延迟通道减少卡顿，门/开关、区域、任务和存档进度使用低频可靠同步，同时同步位置、伸脖子、叼取/拖拽物品和主机权威的物品/NPC状态。游戏启动时会从 GitHub 检查并下载最新 `GooseOnline.dll`，不会替换 BepInEx；下载期间请勿关闭游戏，下载完成后退出游戏，更新器会替换 DLL，随后手动重启。按 `F8` 可以显示或隐藏插件面板。两台电脑都要安装 0.4.1，更新后请完全退出并重新启动游戏。

## 开发者构建

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```





