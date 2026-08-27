# TCP Chat

一个基于 [raylib](https://www.raylib.com/) 和原生 socket（Windows 下为 Winsock2）编写的图形化局域网聊天程序，使用 C++17 编写，单文件实现服务器与客户端两种模式，**完整支持中文**（中文界面、中文消息、中文输入法）。

## 特性

- **一个程序，两种模式**：启动后可在图形界面中选择「启动服务器」或「启动客户端」
- **服务器模式**：
  - 监听端口 `5555`，支持最多 `10` 个客户端同时连接
  - 收到任意客户端消息后向所有客户端广播
  - 支持在服务器窗口直接输入消息（以「服务器：」前缀广播）
- **客户端模式**：
  - 启动前可输入服务器 IP（默认 `127.0.0.1`）
  - 连接成功后即可收发消息，断线时自动提示「与服务器的连接已断开」
- **完整中文支持**：
  - 全中文图形界面与状态提示
  - 支持中文输入法（拼音/五笔等，Windows 下通过 IME 提交）
  - 支持 `Ctrl+V` / `Shift+Insert` 粘贴中文文本
  - 自动加载系统中文字体（等线/黑体/思源黑体/楷体等，按顺序回退）
  - UTF-8 编码收发，按完整字符删除（Backspace）
- **图形界面**：基于 raylib，窗口可自由缩放，消息区随窗口高度动态调整
- **消息历史**：内存中保留最近 `100` 条消息，超出自动淘汰
- **跨平台**：源码同时支持 Windows（Winsock2）与 Linux（BSD socket），Windows 下静态链接 raylib，无需携带 DLL

## 技术要点

- 所有 socket 均设为**非阻塞模式**，与 raylib 的 60 FPS 游戏循环集成，无需额外网络线程
- `SendAll` 处理部分发送（partial send），保证消息完整送达
- 消息列表使用 `std::mutex` 保护，线程安全
- 协议：以换行符 `\n` 分隔的 UTF-8 文本消息，缓冲区 `1024` 字节
- **中文输入实现**（Windows）：
  - raylib 5.5 的 RGFW 后端创建的是 ANSI 窗口且不处理 `WM_CHAR`，无法直接接收中文
  - 通过子类化窗口过程（`SetWindowLongPtrW`）拦截 `WM_IME_COMPOSITION`，
    用 `ImmGetCompositionStringW` 直接读取 UTF-16 提交结果，绕过 ANSI 窗口限制
  - `WM_CHAR` 仅可靠接收 ASCII；剪贴板通过 `CF_UNICODETEXT` 读取
  - 输入按 Unicode 码点处理：代理对组合、UTF-8 编码追加、按字符退格

## 编译

### 依赖

- MinGW-w64（g++，C++17）
- [raylib 5.5](https://github.com/raysan5/raylib/releases/tag/5.5)（下载 `raylib-5.5_win64_mingw-w64.zip`）

### 命令（Windows / MinGW-w64）

```powershell
g++ "TCP Chat9.0-snapshot3.cpp" -o TCP-Chat.exe ^
  -I"raylib/include" -L"raylib/lib" ^
  -lraylib -lws2_32 -lwinmm -lgdi32 -lopengl32 -limm32 ^
  -static -static-libgcc -static-libstdc++ -mwindows ^
  -O2 -std=c++17
```

说明：

- `-static` + `libraylib.a`：静态链接 raylib，生成的 exe **不依赖任何 DLL**，可直接双击运行
- `-limm32`：中文输入法（IMM32）支持
- `-mwindows`：纯 GUI 程序，不弹出控制台窗口
- Linux 下请移除 `-lws2_32 -lwinmm -lgdi32 -limm32`，并链接系统 OpenGL/X11 相关库

## 下载

最新版本：**[9.0-snapshot3](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot3)**

直接下载：[TCP-Chat-9.0-snapshot3.exe](https://github.com/Cyan3175/TCP-Chat/releases/download/9.0-snapshot3/TCP-Chat-9.0-snapshot3.exe)

历史版本：[9.0-snapshot2](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot2)（英文界面）

## 使用

1. 双击运行 `TCP-Chat.exe`（或从上方 Releases 链接下载对应版本）
2. 选择模式：
   - **启动服务器**：在本机启动聊天服务器，窗口标题显示「聊天服务器」
   - **启动客户端**：在上方输入框填入服务器 IP 后点击，窗口标题显示「聊天客户端」
3. 在底部输入框输入消息（中文可直接输入或粘贴），按 **Enter** 发送，**Backspace** 删除
4. 客户端之间、客户端与服务器之间均可互相收发消息

> 跨设备使用请确保双方在同一局域网内，且服务器防火墙放行 TCP 端口 `5555`。

## 版本

版本号从源文件名提取（格式 `TCP Chat<版本>.cpp`）。当前版本：**9.0-snapshot3**。

## 许可证

- 本项目源码暂无明确许可证，如需要请自行补充
- [raylib](https://github.com/raysan5/raylib) 采用 zlib/libpng 许可证
