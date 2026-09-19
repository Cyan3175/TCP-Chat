# TCP Chat 10.0

用 **WinUI 3（Windows App SDK）** 重写的桌面聊天客户端。

和 9.4 的 raylib 版最大的不同：**没有服务端**。程序把学校里已有的 **WebDAV 服务器**当作一块公共留言板 —— 每发一条消息就在远端目录里写一个 JSON 文件，每 2 秒列一次目录把新文件拉下来。不需要开端口、不需要装服务、不需要内网穿透。

---

## 快速开始

1. 双击 `TCP-Chat-10.0.exe`（自包含，无需安装 .NET 或 Windows App Runtime）
2. 首次启动会问昵称，填完即可开始聊
3. 默认连到 `https://dev.zhaohans.cn`，聊天目录 `nw集训/学生资料临存/聊天`

> 换了电脑、换了目录，点右上角 **设置** 改 **WebDAV 地址** 和 **聊天目录** 即可；改这两项后需要重启程序生效。

## 它是怎么工作的

```
  张三的电脑                     学校 WebDAV                   李四的电脑
 ┌──────────┐                 ┌────────────────┐              ┌──────────┐
 │ 发消息   │ ── PUT ───────▶ │ 聊天/           │ ◀── PROPFIND │ 轮询     │
 │          │                 │  msg_<时间>_<随机>.json        │ (每 2 秒)│
 │ 收消息   │ ◀─ GET ──────── │  att_<时间>_<随机>_文件名      │          │
 └──────────┘                 └────────────────┘              └──────────┘
```

- **一条消息一个文件**：WebDAV 没有原子追加，所以不做"往一个日志文件里 append"，而是每条消息单独写 `msg_<unix毫秒>_<8位随机>.json`，天然无并发写冲突
- **文件名即排序键**：时间戳前缀让目录列表天然按时间有序
- **附件**：先 PUT 附件本身（`att_...`），再 PUT 一条引用它的消息 JSON
- **同步**：定时对聊天目录发 `PROPFIND Depth:1`，只对没见过的文件名发 `GET`，收到后反序列化并插到正确的时间位置
- **撤回**：`DELETE` 自己的消息文件和附件

## 功能

### 消息
- 文本消息，`Enter` 发送、`Shift+Enter` 换行，输入框最高 170 DIP 自动换行
- **引用回复**：右键消息 →「引用」，发送后显示为带左侧强调条的引用块
- **复制文本**：右键 →「复制文本」
- **撤回**：右键自己的消息 →「撤回（从服务器删除）」，从服务器删掉并同步移除
- 自己发的消息靠右蓝色气泡，别人发的靠左浅色气泡，各带发送者与时间
- 新消息自动滚到底部（可在设置里关闭）

### 附件
- 点 **📎 文件** 选文件上传，支持任意类型
- **图片**直接在气泡里预览，点击用系统看图程序打开
- **非图片**显示成文件卡片（图标 + 文件名 + 大小），右键可「打开附件」或「另存为…」
- 附件按文件名缓存在 `%LOCALAPPDATA%\TCPChat10\cache`，大小一致就不重复下载

### 界面
- 原生 WinUI 3 控件，跟随系统深浅色，也可在设置里强制浅色/深色
- 高 DPI 感知（PerMonitorV2），窗口按 DIP 居中显示
- 顶栏显示连接状态灯，底栏显示消息条数 / 同步周期 / 上次同步时间 / 当前目录

### 设置项

| 项 | 说明 |
|---|---|
| WebDAV 地址 | 例：`https://dev.zhaohans.cn` |
| 聊天目录 | 服务器上的相对路径，例：`nw集训/学生资料临存/聊天`；不存在会自动逐级创建 |
| 昵称 | 显示在消息上，仅作为身份标识 |
| 用户名 / 密码 | 可选，服务器需要认证时填；留空为匿名访问 |
| 同步周期 | 1–120 秒，默认 2 秒 |
| 历史天数 | 1–365 天，默认只加载最近 7 天的消息 |
| 自动滚动 | 新消息到达时是否自动滚到底部 |
| 主题 | 跟随系统 / 浅色 / 深色 |

## 目录结构

```
TCPChat10/
  App.xaml(.cs)            应用入口、主题资源、崩溃日志
  Models/ChatMessage.cs    消息与附件的数据模型(JSON 结构)
  Services/
    WebDavClient.cs        PROPFIND/GET/PUT/DELETE/MKCOL 极简客户端
    ChatService.cs         发送、轮询同步、撤回、附件下载
    AppSettings.cs         设置读写(%LOCALAPPDATA%\TCPChat10\settings.json)
  ViewModels/MessageVm.cs  消息的显示模型(气泡/附件/图片异步加载)
  Views/
    MainWindow.xaml(.cs)   主窗口
    SettingsDialog.xaml   设置对话框
TCPChat10.Tests/           控制台回归测试(直接引用上面的源码)
```

## 编译

需要 **.NET 10 SDK** + **Windows 11 SDK 10.0.26100**（或 VS 2022 Build Tools 带 UWP/WinUI 工作负载）。

```powershell
cd TCPChat10
dotnet build -c Release          # 调试构建产物
dotnet publish -c Release -o ..dist   # 自包含单目录产物
```

关键工程设置：`WindowsPackageType=None`（免打包运行，双击 exe 即用）、`WindowsAppSDKSelfContained=true`（把 Windows App Runtime 打进去，目标机不需要预装任何运行时）。

> WindowsAppSDK 版本必须 ≥ 2.5.1：1.7.x 上 `XamlCompiler.exe` 会静默退出码 1，构建直接失败。

## 测试

### 控制台回归（16 项，打真实服务器）

```powershell
cd TCPChat10.Tests
dotnet run -c Release
```

覆盖：连通性、发文本、发带引用消息、**另一个全新实例能否读到**、二次同步不重复、附件逐字节一致性(20000 字节)、删除消息。测试全部写在服务器上的 `_tcpchat_selftest` 目录里，跑完可以清掉：

```powershell
dotnet run -c Release -- clean "nw集训/学生资料临存/_tcpchat_selftest" --rmdir
```

### 界面自动化

主窗口内置自测钩子，通过环境变量驱动，可以无人值守地把界面截图出来核对：

```powershell
$env:TCPCHAT10_TEST_SETTINGS = "test_settings.json"   # 指向测试目录，避免污染真实聊天
$env:TCPCHAT10_TEST_LOG      = "ui_test.log"
$env:TCPCHAT10_AUTOTEST      = "waitmsg:9;scroll;menu;shot:C:\shot.png;settings;dshot:C:\dlg.png;closedlg;quit"
.\TCP-Chat-10.0.exe
```

支持的动作：`wait:N` / `waitmsg:N` / `send:文本` / `sendq:引用|正文` / `attach:路径` / `scroll` /
`menu` / `hidemenu` / `settings` / `dshot:路径` / `closedlg` / `shot:路径` / `log:文本` / `quit`。

## 已知限制与注意事项

- **聊天目录是公开的**：只要知道 WebDAV 地址和目录名，任何人不登录也能读到消息。**不要在正式目录里发隐私内容。** 需要保密的话要么给服务器配认证并关掉匿名写，要么给消息加端到端加密（本版未做）
- **消息不是实时推送**：靠轮询，默认 2 秒，所以对方最多慢 2 秒看到（服务器本身偶发会把某个请求挂住几十秒，客户端对每个请求都设了超时，卡住会自动跳过并在下一轮重试）
- **历史只按文件名时间戳排**：客户端时钟不准会导致消息顺序错乱
- **不能发超大附件**：整份文件会先读进内存再上传，超过一两百 MB 请直接用资源管理器拷
- 自包含发布体积约 156 MB（解压后），因为把 .NET 与 Windows App Runtime 都打进去了

## 9.4 及以前（raylib 版）

仓库根目录的 `TCP Chat9.4.cpp` 是基于 [raylib](https://www.raylib.com/) 6.0 + Winsock2 的 C++17 单文件实现，**自带服务端**、走原始 TCP 直连（默认端口 45678），功能包括多房间、私聊、断点续传、表情、搜索、托盘等。

```powershell
g++ "TCP Chat9.4.cpp" -o TCP-Chat-9.4.exe ^
  -I"raylib/include" -L"raylib/lib" ^
  -lraylib -lws2_32 -lwinmm -lgdi32 -lopengl32 -limm32 -lcomdlg32 -lshell32 ^
  -static -static-libgcc -static-libstdc++ -mwindows ^
  -O2 -std=c++17
```

两版各有取舍：9.4 需要一台机器当服务器、但消息不落地；10.0 不需要服务器、但消息明文存在共享目录里。历史版本见 [Releases](https://github.com/Cyan3175/TCP-Chat/releases)。
