# TCP Chat

一个基于 [raylib](https://www.raylib.com/) 和原生 socket（Windows 下为 Winsock2）编写的图形化局域网聊天程序，使用 C++17 编写，单文件实现服务器与客户端两种模式，**完整支持中文**（中文界面、中文消息、中文输入法），支持 **IPv6**、**私聊**、**文件传输**，并带 **消息滚动条**、**输入光标**、**FPS 跟随显示器**、**传输进度条**、**SHA-256 文件校验** 与 **自定义昵称**。

## 特性

- **一个程序，两种模式**：启动后可在图形界面中选择「启动服务器」或「启动客户端」
- **服务器模式**：
  - 监听端口 `5555`，IPv4/IPv6 双栈，支持最多 `10` 个客户端同时连接
  - 广播消息、私聊转发、文件中转
- **客户端模式**：
  - 服务器地址支持 IPv4 / IPv6 / 主机名（默认 `127.0.0.1`）
  - **自定义昵称**：启动前可填写昵称（留空自动分配「用户N」），连接后实时同步给所有用户
- **文件传输**：
  - 「发送文件…」按钮弹系统文件对话框，发送给当前选中对象
  - Base64 分块（24KB/块）+ **SHA-256 完整性校验**，接收方校验通过才提示成功
  - 接收方自动保存到 `received\` 目录，重名自动加后缀
  - **实时进度条**：发送/接收进度条显示在输入区上方
- **消息区**：
  - **滚动条**：鼠标滚轮 / 拖拽滑块 / PageUp / PageDown 翻看历史，上翻时新消息不打断阅读
  - 最近 `100` 条消息
- **输入光标**：闪烁插入光标，左右/Home/End 键移动，鼠标点击定位，支持字符级删除（Backspace/Delete），悬停输入框显示 I 型鼠标指针
- **性能**：目标帧率自动跟随当前显示器的刷新率（`GetMonitorRefreshRate`），右上角显示实时 FPS
- **完整中文支持**：全中文界面、中文输入法（IME）、`Ctrl+V` 粘贴、系统中文字体自动加载
- **图形界面**：基于 raylib，窗口可自由缩放
- **跨平台**：源码同时支持 Windows / Linux，Windows 下静态链接，无需 DLL

## 技术要点

- 非阻塞 socket + 每连接发送队列（正确处理 `WSAEWOULDBLOCK`）+ 行重组接收（单行最大 1MB）
- **通信协议**：换行分隔的 UTF-8 文本行，字段以 `|` 分隔

```
MSG|<text>                客户端->服务器: 广播
PMSG|<targetId>|<text>    客户端->服务器: 私聊(0=服务器)
NAME|<nick>               客户端->服务器: 设置昵称(连接后首行)
MSG|<id>|<name>|<text>    服务器->客户端: 广播
PMSG|<id>|<name>|<text>   服务器->客户端: 私聊
WELCOME|<id>|<name> / ROSTER|<id:name,...> / JOIN / LEAVE / RENAME|<id>|<old>|<new>
FILE_OFFER|<...> / FILE_DATA|<fileId>|<base64> / FILE_DONE|<fileId>|<bytes>|<sha256hex> / FILE_ERROR
```

- **中文输入**（Windows）：raylib 5.5 RGFW 后端创建 ANSI 窗口，Unicode 字符消息会被系统破坏性转换；通过子类化窗口过程拦截 `WM_IME_COMPOSITION` + `ImmGetCompositionStringW` 读取 UTF-16 提交结果
- **SHA-256**：内置无依赖实现（增量更新，边读边算），已与系统 `Get-FileHash` 交叉验证一致

## 编译

### 依赖

- MinGW-w64（g++，C++17）
- [raylib 5.5](https://github.com/raysan5/raylib/releases/tag/5.5)（下载 `raylib-5.5_win64_mingw-w64.zip`）

### 命令（Windows / MinGW-w64）

```powershell
g++ "TCP Chat9.0-snapshot5.cpp" -o TCP-Chat.exe ^
  -I"raylib/include" -L"raylib/lib" ^
  -lraylib -lws2_32 -lwinmm -lgdi32 -lopengl32 -limm32 -lcomdlg32 ^
  -static -static-libgcc -static-libstdc++ -mwindows ^
  -O2 -std=c++17
```

## 下载

最新版本：**[9.0-snapshot5](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot5)**

直接下载：[TCP-Chat-9.0-snapshot5.exe](https://github.com/Cyan3175/TCP-Chat/releases/download/9.0-snapshot5/TCP-Chat-9.0-snapshot5.exe)

历史版本：[9.0-snapshot4](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot4)（IPv6/私聊/文件传输）、[9.0-snapshot3](https://github.com/Cyan3175/TCP-Chat/releases/tag/9.0-snapshot3)（中文支持）

## 使用

1. 双击运行 `TCP-Chat.exe`
2. 可选：在「昵称」输入框填写你的名字（留空自动分配）
3. 选择「启动服务器」或输入服务器地址后「启动客户端」
4. 右侧「在线用户」面板点击「【广播】」或某位用户选择发送对象
5. 底部输入框输入消息，**Enter** 发送；**←/→/Home/End** 移动光标，**Backspace/Delete** 删除，**鼠标点击**定位光标
6. 点击「发送文件…」发送文件；收发进度显示在输入区上方；收到的文件在 `received\` 目录
7. 消息区可用**滚轮/拖拽滑块/PageUp/PageDown** 查看历史

> 跨设备使用请确保双方在同一局域网内，且服务器防火墙放行 TCP 端口 `5555`。

## 版本

版本号从源文件名提取（格式 `TCP Chat<版本>.cpp`）。当前版本：**9.0-snapshot5**。

## 许可证

- 本项目源码暂无明确许可证，如需要请自行补充
- [raylib](https://github.com/raysan5/raylib) 采用 zlib/libpng 许可证
