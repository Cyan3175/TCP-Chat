# TCP Chat

一个基于 [raylib](https://www.raylib.com/) 和原生 socket（Windows 下为 Winsock2）编写的图形化局域网聊天程序，使用 C++17 编写，单文件实现服务器与客户端两种模式，**完整支持中文**（中文界面、中文消息、中文输入法），并支持 **IPv6**、**私聊** 与 **文件传输**。

## 特性

- **一个程序，两种模式**：启动后可在图形界面中选择「启动服务器」或「启动客户端」
- **服务器模式**：
  - 监听端口 `5555`，IPv4/IPv6 双栈，支持最多 `10` 个客户端同时连接
  - 收到任意客户端消息后向所有客户端广播
  - 可在右侧在线用户面板选择某位用户进行私聊，或向其发送文件
- **客户端模式**：
  - 启动前可输入服务器地址（支持 IPv4 / IPv6 / 主机名，默认 `127.0.0.1`）
  - 可在右侧在线用户面板选择「广播」或某位用户，发送公开消息或私聊
- **文件传输**：
  - 点击「发送文件…」按钮选择文件（系统文件对话框），发送给当前选中的对象（服务器或某位用户）
  - 协议内置 Base64 分块（24KB/块）与完成校验，接收方自动保存到 `received\` 目录，重名自动加后缀
  - 支持任意大小文件（内存占用恒定），断线自动中断并提示
- **完整中文支持**：
  - 全中文图形界面与状态提示
  - 支持中文输入法（拼音/五笔等）与 `Ctrl+V` / `Shift+Insert` 粘贴
  - 自动加载系统中文字体（等线/黑体/思源黑体/楷体等，按顺序回退）
- **图形界面**：基于 raylib，窗口可自由缩放，消息区随窗口高度动态调整
- **消息历史**：内存中保留最近 `100` 条消息，超出自动淘汰
- **跨平台**：源码同时支持 Windows（Winsock2）与 Linux（BSD socket），Windows 下静态链接 raylib，无需携带 DLL

## 技术要点

- 所有 socket 均设为**非阻塞模式**，与 raylib 的 60 FPS 游戏循环集成
- **非阻塞发送队列**：每条连接维护待发送缓冲，每帧刷出，正确处理 `WSAEWOULDBLOCK`，大文件不会卡界面
- **行重组接收**：每条连接维护接收缓冲，按换行符重组完整协议行（单行最大 1MB），不再假设一次 recv 一条消息
- 消息列表使用 `std::mutex` 保护，线程安全
- **通信协议**：换行分隔的 UTF-8 文本行，字段以 `|` 分隔（末字段可含任意文本）

```
MSG|<text>                 客户端->服务器: 广播
PMSG|<targetId>|<text>     客户端->服务器: 私聊(0=服务器)
MSG|<id>|<name>|<text>     服务器->客户端: 广播
PMSG|<id>|<name>|<text>    服务器->客户端: 私聊
WELCOME|<id>|<name> / ROSTER|<id:name,...> / JOIN / LEAVE
FILE_OFFER|<...> / FILE_DATA|<fileId>|<base64> / FILE_DONE|<fileId>|<bytes> / FILE_ERROR
```

- **中文输入实现**（Windows）：
  - raylib 5.5 的 RGFW 后端创建 ANSI 窗口且不处理 `WM_CHAR`，Unicode 字符消息会被系统破坏性转换
  - 通过子类化窗口过程拦截 `WM_IME_COMPOSITION`，用 `ImmGetCompositionStringW` 读取 UTF-16 提交结果
  - 输入按 Unicode 码点处理：代理对组合、UTF-8 编码追加、按字符退格

## 编译

### 依赖

- MinGW-w64（g++，C++17）
- [raylib 5.5](https://github.com/raysan5/raylib/releases/tag/5.5)（下载 `raylib-5.5_win64_mingw-w64.zip`）

### 命令（Windows / MinGW-w64）

```powershell
g++ "TCP Chat9.0-snapshot4.cpp" -o TCP-Chat.exe ^
  -I"raylib/include" -L"raylib/lib" ^
  -lraylib -lws2_32 -lwinmm -lgdi32 -lopengl32 -limm32 -lcomdlg32 ^
  -static -static-libgcc -static-libstdc++ -mwindows ^
  -O2 -std=c++17
```

说明：

- `-static` + `libraylib.a`：静态链接 raylib，生成的 exe **不依赖任何 DLL**，可直接双击运行
- `-limm32`：中文输入法支持；`-lcomdlg32`：文件选择对话框
- `-mwindows`：纯 GUI 程序，不弹出控制台窗口
- Linux 下请移除 `-lws2_32 -lwinmm -lgdi32 -limm32 -lcomdlg32`，并链接系统 OpenGL/X11 相关库

## 下载

最新版本：**[9.0-snapshot4](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot4)**

直接下载：[TCP-Chat-9.0-snapshot4.exe](https://github.com/Cyan3175/TCP-Chat/releases/download/9.0-snapshot4/TCP-Chat-9.0-snapshot4.exe)

历史版本：[9.0-snapshot3](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot3)（中文支持）、[9.0-snapshot2](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot2)（英文界面）

## 使用

1. 双击运行 `TCP-Chat.exe`，选择「启动服务器」或输入地址后「启动客户端」
2. 右侧「在线用户」面板显示当前在线用户，点击「【广播】」或某位用户选择发送对象
3. 底部输入框输入消息（中文可直接输入或粘贴），按 **Enter** 发送，**Backspace** 删除
4. 点击右下角「发送文件…」选择文件，发送给当前选中的对象；收到的文件自动保存在 `received\` 目录
5. 客户端之间、客户端与服务器之间均可互相收发消息、私聊与传文件

> 跨设备使用请确保双方在同一局域网内，且服务器防火墙放行 TCP 端口 `5555`。

## 版本

版本号从源文件名提取（格式 `TCP Chat<版本>.cpp`）。当前版本：**9.0-snapshot4**。

## 许可证

- 本项目源码暂无明确许可证，如需要请自行补充
- [raylib](https://github.com/raysan5/raylib) 采用 zlib/libpng 许可证
