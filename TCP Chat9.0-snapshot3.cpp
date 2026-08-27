// TCP Chat 9.0-snapshot3
// 图形化局域网聊天程序(完整中文支持)
// 依赖: raylib 5.5 + Winsock2(Windows) / BSD socket(Linux)
// 中文输入: Windows 下通过子类化窗口过程拦截 WM_CHAR / WM_IME_CHAR / 剪贴板粘贴

#include <iostream>
#include <string>
#include <vector>
#include <mutex>
#include <cstring>
#include <chrono>
#include <fstream>
#include <cstdlib>

#ifdef _WIN32
// raylib 与 Windows 头文件的兼容宏
// NOGDI/NOUSER: 排除 wingdi/winuser 中与 raylib 冲突的声明
// (Rectangle/CloseWindow/ShowCursor/DrawText 等函数声明无法用 #undef 消除, 必须提前排除)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef NOGDI
#define NOGDI
#endif
#ifndef NOUSER
#define NOUSER
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

// raylib 必须在 Windows 头之后包含
#include <raylib.h>

// ---- 手动声明输入法所需的少量 Win32 API(user32/kernel32) ----
// NOUSER 排除了 winuser.h, 这里仅导入实际用到的符号
#ifndef WM_KEYDOWN
#define WM_KEYDOWN 0x0100
#endif
#ifndef WM_CHAR
#define WM_CHAR 0x0102
#endif
#ifndef WM_IME_CHAR
#define WM_IME_CHAR 0x0286
#endif
#ifndef WM_IME_COMPOSITION
#define WM_IME_COMPOSITION 0x010F
#endif
#ifndef GCS_RESULTSTR
#define GCS_RESULTSTR 0x0800
#endif
#ifndef VK_SHIFT
#define VK_SHIFT 0x10
#endif
#ifndef VK_CONTROL
#define VK_CONTROL 0x11
#endif
#ifndef VK_INSERT
#define VK_INSERT 0x2D
#endif
#ifndef CF_UNICODETEXT
#define CF_UNICODETEXT 13
#endif
#ifndef GWLP_WNDPROC
#define GWLP_WNDPROC (-4)
#endif

typedef LRESULT (CALLBACK* WNDPROC)(HWND, UINT, WPARAM, LPARAM);

extern "C" {
__declspec(dllimport) HIMC     WINAPI ImmGetContext(HWND hWnd);
__declspec(dllimport) BOOL     WINAPI ImmReleaseContext(HWND hWnd, HIMC hIMC);
__declspec(dllimport) LONG     WINAPI ImmGetCompositionStringW(HIMC hIMC, DWORD dwIndex, LPVOID lpBuf, DWORD dwBufLen);
__declspec(dllimport) LONG_PTR WINAPI GetWindowLongPtrW(HWND hWnd, int nIndex);
__declspec(dllimport) LONG_PTR WINAPI SetWindowLongPtrW(HWND hWnd, int nIndex, LONG_PTR dwNewLong);
__declspec(dllimport) LRESULT   WINAPI CallWindowProcW(WNDPROC lpPrevWndFunc, HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam);
__declspec(dllimport) BOOL      WINAPI SetWindowTextW(HWND hWnd, LPCWSTR lpString);
__declspec(dllimport) SHORT     WINAPI GetKeyState(int nVirtKey);
__declspec(dllimport) BOOL      WINAPI OpenClipboard(HWND hWndNewOwner);
__declspec(dllimport) HANDLE    WINAPI GetClipboardData(UINT uFormat);
__declspec(dllimport) LPVOID    WINAPI GlobalLock(HGLOBAL hMem);
__declspec(dllimport) BOOL      WINAPI GlobalUnlock(HGLOBAL hMem);
__declspec(dllimport) BOOL      WINAPI CloseClipboard(void);
}

typedef SOCKET SocketType;
#define CLOSE_SOCKET closesocket
#define SOCKET_ERROR_CODE SOCKET_ERROR
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/select.h>
#include <raylib.h>
typedef int SocketType;
#define CLOSE_SOCKET close
#define SOCKET_ERROR_CODE -1
#define INVALID_SOCKET -1
#endif

#ifdef _MSC_VER
#pragma comment(lib, "ws2_32.lib")
#endif

// ==================== 常量 ====================
constexpr int PORT = 5555;
constexpr int MAX_CLIENTS = 10;
constexpr int BUFFER_SIZE = 1024;
constexpr int DEFAULT_SCREEN_WIDTH = 800;
constexpr int DEFAULT_SCREEN_HEIGHT = 600;
constexpr int UI_FONT_SIZE = 20;

// ==================== 全局状态 ====================
std::mutex messagesMutex;
std::vector<std::string> chatMessages;

Font g_font;                       // 中文字体(启动时从系统字体加载)
bool g_fontLoaded = false;         // 是否成功加载中文字体

// ---- 输入法字符队列(WM_CHAR / WM_IME_CHAR 推入) ----
std::mutex inputMutex;
std::vector<uint32_t> inputQueue;
std::vector<uint32_t> g_debugLog;  // 调试: 记录所有收到的码点(环境变量 TCPCHAT_DEBUG_CODEPOINTS 非空时输出)
uint32_t g_pendingHighSurrogate = 0;

#ifdef _WIN32
WNDPROC g_originalWndProc = nullptr;

// 推入一个码点(处理 UTF-16 代理对)
static void PushInputChar(uint32_t cp) {
    std::lock_guard<std::mutex> lock(inputMutex);
    if (g_pendingHighSurrogate) {
        if (cp >= 0xDC00 && cp <= 0xDFFF) {
            cp = 0x10000 + ((g_pendingHighSurrogate - 0xD800) << 10) + (cp - 0xDC00);
            g_pendingHighSurrogate = 0;
        } else {
            g_pendingHighSurrogate = 0; // 无效序列, 丢弃高位代理
        }
    } else if (cp >= 0xD800 && cp <= 0xDBFF) {
        g_pendingHighSurrogate = cp;
        return;
    }
    inputQueue.push_back(cp);
    g_debugLog.push_back(cp);
}

// 读取剪贴板文本(支持 Ctrl+V / Shift+Insert 粘贴中文)
static void PushClipboardText() {
    if (!OpenClipboard(nullptr)) return;
    HANDLE h = GetClipboardData(CF_UNICODETEXT);
    if (h) {
        const wchar_t* s = (const wchar_t*)GlobalLock(h);
        if (s) {
            while (*s) PushInputChar((uint32_t)*s++);
            GlobalUnlock(h);
        }
    }
    CloseClipboard();
}

// 子类化窗口过程: 拦截字符输入消息
// 注意: RGFW 创建的是 ANSI 窗口(CreateWindowA), 系统会把投递给 ANSI 窗口的
// Unicode 字符消息破坏性转换(WM_CHAR 截断为 8 位 / WM_IME_CHAR 字节交换),
// 因此中文输入统一通过 WM_IME_COMPOSITION + ImmGetCompositionStringW 读取
// (该 API 直接返回 UTF-16, 不受 ANSI 窗口限制); WM_CHAR 仅可靠传递 ASCII。
LRESULT CALLBACK ChatWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_CHAR: {
            // ANSI 窗口下仅 ASCII 字符可靠, 被转换破坏的非 ASCII 字符直接丢弃
            uint32_t cp = (uint32_t)wParam;
            if (cp >= 32 && cp < 0x80) {
                PushInputChar(cp);
            }
            return 0;
        }
        case WM_IME_CHAR: {
            // 忽略: 对 ANSI 窗口 wParam 已被破坏, 完整结果由 WM_IME_COMPOSITION 提供
            return 0;
        }
        case WM_IME_COMPOSITION: {
            if (lParam & GCS_RESULTSTR) {
                HIMC hIMC = ImmGetContext(hwnd);
                if (hIMC) {
                    LONG len = ImmGetCompositionStringW(hIMC, GCS_RESULTSTR, NULL, 0);
                    if (len > 0) {
                        std::vector<wchar_t> buf((size_t)(len / 2) + 1, 0);
                        ImmGetCompositionStringW(hIMC, GCS_RESULTSTR, buf.data(), (DWORD)len);
                        for (wchar_t wc : buf) {
                            if (wc == 0) break;
                            PushInputChar((uint32_t)wc);
                        }
                    }
                    ImmReleaseContext(hwnd, hIMC);
                }
            }
            // 消费该消息: 防止 DefWindowProc 再生成被 ANSI 窗口破坏的 WM_CHAR 混入输入
            return 0;
        }
        case WM_KEYDOWN: {
            // Ctrl+V / Shift+Insert 粘贴
            bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            if ((ctrl && wParam == 'V') || (shift && wParam == VK_INSERT)) {
                PushClipboardText();
                return 0;
            }
            break;
        }
        default: break;
    }
    return CallWindowProcW(g_originalWndProc, hwnd, msg, wParam, lParam);
}

// 安装/卸载输入钩子
void InstallInputHook() {
    HWND hwnd = (HWND)GetWindowHandle();
    if (!hwnd) return;
    if (!g_originalWndProc) {
        g_originalWndProc = (WNDPROC)GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (LONG_PTR)ChatWndProc);
    }
}

void RemoveInputHook() {
    HWND hwnd = (HWND)GetWindowHandle();
    if (hwnd && g_originalWndProc) {
        SetWindowLongPtrW(hwnd, GWLP_WNDPROC, (LONG_PTR)g_originalWndProc);
    }
}
#endif // _WIN32

// 取走当前帧输入的所有码点
std::vector<uint32_t> TakeInputCodepoints() {
    std::lock_guard<std::mutex> lock(inputMutex);
    std::vector<uint32_t> out;
    out.swap(inputQueue);
    return out;
}

// ==================== 网络工具 ====================
bool InitNetwork() {
#ifdef _WIN32
    WSADATA wsaData;
    return WSAStartup(MAKEWORD(2, 2), &wsaData) == 0;
#else
    return true;
#endif
}

void CleanupNetwork() {
#ifdef _WIN32
    WSACleanup();
#endif
}

bool SetNonBlocking(SocketType sock) {
#ifdef _WIN32
    u_long mode = 1;
    return ioctlsocket(sock, FIONBIO, &mode) == 0;
#else
    int flags = fcntl(sock, F_GETFL, 0);
    if (flags == -1) return false;
    return fcntl(sock, F_SETFL, flags | O_NONBLOCK) != -1;
#endif
}

bool SendAll(SocketType sock, const std::string& data) {
    int total = 0;
    int len = (int)data.size();
    const char* buf = data.c_str();
    while (total < len) {
        int sent = send(sock, buf + total, len - total, 0);
        if (sent == SOCKET_ERROR_CODE) return false;
        total += sent;
    }
    return true;
}

void AddMessage(const std::string& msg) {
    std::lock_guard<std::mutex> lock(messagesMutex);
    chatMessages.push_back(msg);
    if (chatMessages.size() > 100) {
        chatMessages.erase(chatMessages.begin());
    }
}

void ClearMessages() {
    std::lock_guard<std::mutex> lock(messagesMutex);
    chatMessages.clear();
}

// ==================== UTF-8 工具 ====================
void AppendUtf8(std::string& s, uint32_t cp) {
    if (cp < 0x80) {
        s += (char)cp;
    } else if (cp < 0x800) {
        s += (char)(0xC0 | (cp >> 6));
        s += (char)(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        s += (char)(0xE0 | (cp >> 12));
        s += (char)(0x80 | ((cp >> 6) & 0x3F));
        s += (char)(0x80 | (cp & 0x3F));
    } else {
        s += (char)(0xF0 | (cp >> 18));
        s += (char)(0x80 | ((cp >> 12) & 0x3F));
        s += (char)(0x80 | ((cp >> 6) & 0x3F));
        s += (char)(0x80 | (cp & 0x3F));
    }
}

// 删除最后一个完整 UTF-8 字符
void Utf8PopChar(std::string& s) {
    if (s.empty()) return;
    size_t n = s.size() - 1;
    while (n > 0 && ((unsigned char)s[n] & 0xC0) == 0x80) --n;
    s.erase(n);
}

// 去掉末尾换行符(网络消息)
void TrimTrailingNewline(std::string& s) {
    if (!s.empty() && s.back() == '\n') s.pop_back();
    if (!s.empty() && s.back() == '\r') s.pop_back();
}

// ==================== 中文字体 ====================
Font LoadCjkFont() {
    // 需要渲染的字符集: ASCII + Latin-1 + 常用标点 + CJK 标点 + 中日韩统一表意文字 + 全角字符
    std::vector<int> codepoints;
    for (int cp = 0x20; cp <= 0x7E; ++cp) codepoints.push_back(cp);
    for (int cp = 0xA0; cp <= 0xFF; ++cp) codepoints.push_back(cp);
    for (int cp = 0x2000; cp <= 0x206F; ++cp) codepoints.push_back(cp);
    for (int cp = 0x3000; cp <= 0x303F; ++cp) codepoints.push_back(cp);
    for (int cp = 0x4E00; cp <= 0x9FFF; ++cp) codepoints.push_back(cp);
    for (int cp = 0xFF00; cp <= 0xFFEF; ++cp) codepoints.push_back(cp);

    // 字体候选(按优先级): Windows 常用中文字体 + Linux Noto/文泉驿
    std::string windir = "C:\\Windows";
    const char* envWindir = std::getenv("WINDIR");
    if (envWindir && *envWindir) windir = envWindir;

    std::vector<std::string> candidates = {
        windir + "\\Fonts\\Deng.ttf",                    // 等线 (Windows 10+)
        windir + "\\Fonts\\simhei.ttf",                  // 黑体
        windir + "\\Fonts\\Noto Sans SC (TrueType).otf", // 思源黑体
        windir + "\\Fonts\\simkai.ttf",                  // 楷体
        windir + "\\Fonts\\simfang.ttf",                 // 仿宋
        windir + "\\Fonts\\STXIHEI.TTF",                 // 华文细黑
        windir + "\\Fonts\\STKAITI.TTF",                 // 华文楷体
        windir + "\\Fonts\\STSONG.TTF",                  // 华文宋体
        windir + "\\Fonts\\msyi.ttf",                    // 微软雅黑斜体(部分系统)
        "/usr/share/fonts/opentype/noto/NotoSansSC-Regular.otf",
        "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
        "/usr/share/fonts/truetype/arphic/uming.ttc",
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
    };

    for (const auto& path : candidates) {
        if (!FileExists(path.c_str())) continue;
        Font f = LoadFontEx(path.c_str(), UI_FONT_SIZE, codepoints.data(), (int)codepoints.size());
        if (f.texture.id != 0) {
            g_fontLoaded = true;
            return f;
        }
        UnloadFont(f);
    }
    return GetFontDefault();
}

// ==================== 绘制工具 ====================
void DrawTextC(const char* text, float x, float y, int size, Color color) {
    DrawTextEx(g_font, text, Vector2{ x, y }, (float)size, 1.0f, color);
}

int MeasureTextC(const char* text, int size) {
    return (int)MeasureTextEx(g_font, text, (float)size, 1.0f).x;
}

void DrawTextCenteredInRect(const char* text, Rectangle rect, int fontSize, Color color) {
    int textWidth = MeasureTextC(text, fontSize);
    float textX = rect.x + (rect.width - textWidth) / 2;
    float textY = rect.y + (rect.height - fontSize) / 2;
    DrawTextC(text, textX, textY, fontSize, color);
}

// 设置窗口标题(中文, Windows 下用 Wide API 修正 RGFW 的 ANSI 限制)
void SetWindowTitleC(const wchar_t* zh, const char* en) {
#ifdef _WIN32
    if (g_fontLoaded) {
        HWND hwnd = (HWND)GetWindowHandle();
        if (hwnd) SetWindowTextW(hwnd, zh);
        return;
    }
    SetWindowTitle(en);
#else
    (void)zh;
    (void)en;
    // Linux: raylib 标题使用 UTF-8
    SetWindowTitle("TCP Chat");
#endif
}

// ==================== 应用状态机 ====================
enum class AppMode { Select, Server, Client };
AppMode g_mode = AppMode::Select;

// 选择模式
std::string ipInput = "127.0.0.1";

// 服务器模式
SocketType listenSock = INVALID_SOCKET;
std::vector<SocketType> clientSocks;
std::string serverInput;

// 客户端模式
SocketType clientSock = INVALID_SOCKET;
std::string clientInput;

bool IsIpChar(uint32_t cp) {
    return (cp >= '0' && cp <= '9') || cp == '.' || cp == ':' ||
           (cp >= 'a' && cp <= 'f') || (cp >= 'A' && cp <= 'F');
}

// 启动服务器
bool StartServer() {
    listenSock = socket(AF_INET, SOCK_STREAM, 0);
    if (listenSock == INVALID_SOCKET) return false;
    int opt = 1;
    setsockopt(listenSock, SOL_SOCKET, SO_REUSEADDR, (const char*)&opt, sizeof(opt));
    sockaddr_in serverAddr;
    memset(&serverAddr, 0, sizeof(serverAddr));
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_addr.s_addr = INADDR_ANY;
    serverAddr.sin_port = htons(PORT);
    if (bind(listenSock, (sockaddr*)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR_CODE) {
        CLOSE_SOCKET(listenSock);
        listenSock = INVALID_SOCKET;
        return false;
    }
    if (listen(listenSock, MAX_CLIENTS) == SOCKET_ERROR_CODE) {
        CLOSE_SOCKET(listenSock);
        listenSock = INVALID_SOCKET;
        return false;
    }
    SetNonBlocking(listenSock);
    return true;
}

// 连接服务器
bool ConnectClient(const std::string& serverIP) {
    clientSock = socket(AF_INET, SOCK_STREAM, 0);
    if (clientSock == INVALID_SOCKET) return false;
    sockaddr_in serverAddr;
    memset(&serverAddr, 0, sizeof(serverAddr));
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(PORT);
    if (inet_pton(AF_INET, serverIP.c_str(), &serverAddr.sin_addr) != 1) {
        CLOSE_SOCKET(clientSock);
        clientSock = INVALID_SOCKET;
        return false;
    }
    if (connect(clientSock, (sockaddr*)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR_CODE) {
        CLOSE_SOCKET(clientSock);
        clientSock = INVALID_SOCKET;
        return false;
    }
    SetNonBlocking(clientSock);
    return true;
}

void ShutdownServer() {
    for (SocketType sock : clientSocks) CLOSE_SOCKET(sock);
    clientSocks.clear();
    if (listenSock != INVALID_SOCKET) CLOSE_SOCKET(listenSock);
    listenSock = INVALID_SOCKET;
}

void ShutdownClient() {
    if (clientSock != INVALID_SOCKET) CLOSE_SOCKET(clientSock);
    clientSock = INVALID_SOCKET;
}

// ---- 选择模式 ----
void UpdateSelectFrame() {
    int screenWidth = GetScreenWidth();
    float margin = 20;
    float titleY = margin;
    float labelY = titleY + 40;
    float inputBoxY = labelY + 25;
    float buttonY = inputBoxY + 50;

    // 输入: 仅 IP 字符
    for (uint32_t cp : TakeInputCodepoints()) {
        if (IsIpChar(cp)) AppendUtf8(ipInput, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE) && !ipInput.empty()) Utf8PopChar(ipInput);

    Rectangle inputBox = Rectangle{ margin, inputBoxY, (float)screenWidth - 2 * margin, 30 };
    float buttonWidth = 160, buttonHeight = 40, gap = 20;
    float totalButtonsWidth = 2 * buttonWidth + gap;
    float startX = (screenWidth - totalButtonsWidth) / 2;
    Rectangle serverBtn = Rectangle{ startX, buttonY, buttonWidth, buttonHeight };
    Rectangle clientBtn = Rectangle{ startX + buttonWidth + gap, buttonY, buttonWidth, buttonHeight };

    if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
        Vector2 mousePos = GetMousePosition();
        if (CheckCollisionPointRec(mousePos, serverBtn)) {
            ClearMessages();
            serverInput.clear();
            if (StartServer()) {
                g_mode = AppMode::Server;
                SetWindowTitleC(L"聊天服务器", "Chat Server");
                AddMessage("服务器已启动，监听端口 " + std::to_string(PORT));
            } else {
                AddMessage("启动服务器失败：端口 " + std::to_string(PORT) + " 可能已被占用");
            }
            return;
        }
        if (CheckCollisionPointRec(mousePos, clientBtn)) {
            ClearMessages();
            clientInput.clear();
            if (ConnectClient(ipInput)) {
                g_mode = AppMode::Client;
                SetWindowTitleC(L"聊天客户端", "Chat Client");
                AddMessage("已连接到服务器 " + ipInput);
            } else {
                AddMessage("无法连接到服务器 " + ipInput + "，请检查 IP 和端口");
            }
            return;
        }
    }

    BeginDrawing();
    ClearBackground(RAYWHITE);
    const char* title = "选择模式";
    int titleWidth = MeasureTextC(title, UI_FONT_SIZE);
    DrawTextC(title, (screenWidth - titleWidth) / 2.0f, titleY, UI_FONT_SIZE, DARKGRAY);

    DrawTextC("服务器 IP（客户端模式）：", margin, labelY, UI_FONT_SIZE, DARKGRAY);
    DrawRectangleRec(inputBox, LIGHTGRAY);
    DrawRectangleLines((int)inputBox.x, (int)inputBox.y, (int)inputBox.width, (int)inputBox.height, GRAY);
    DrawTextC(ipInput.c_str(), inputBox.x + 5, inputBox.y + 5, UI_FONT_SIZE, BLACK);

    DrawRectangleRec(serverBtn, SKYBLUE);
    DrawTextCenteredInRect("启动服务器", serverBtn, UI_FONT_SIZE, BLACK);
    DrawRectangleRec(clientBtn, GREEN);
    DrawTextCenteredInRect("启动客户端", clientBtn, UI_FONT_SIZE, BLACK);
    EndDrawing();
}

// ---- 服务器模式 ----
void UpdateServerFrame() {
    int screenWidth = GetScreenWidth();
    int screenHeight = GetScreenHeight();

    // 接受新连接
    sockaddr_in clientAddr;
    socklen_t clientLen = sizeof(clientAddr);
    SocketType newClient = accept(listenSock, (sockaddr*)&clientAddr, &clientLen);
    if (newClient != INVALID_SOCKET) {
        SetNonBlocking(newClient);
        clientSocks.push_back(newClient);
        char ip[INET_ADDRSTRLEN];
        inet_ntop(AF_INET, &clientAddr.sin_addr, ip, INET_ADDRSTRLEN);
        std::string welcomeMsg = "客户端已连接：" + std::string(ip);
        AddMessage(welcomeMsg);
        for (SocketType sock : clientSocks) {
            if (sock != newClient) SendAll(sock, welcomeMsg + "\n");
        }
    }

    // 接收客户端数据
    for (size_t i = 0; i < clientSocks.size(); ) {
        SocketType sock = clientSocks[i];
        char buffer[BUFFER_SIZE];
        int bytesReceived = recv(sock, buffer, BUFFER_SIZE - 1, 0);
        if (bytesReceived > 0) {
            buffer[bytesReceived] = '\0';
            std::string msg(buffer);
            TrimTrailingNewline(msg);
            if (!msg.empty()) {
                AddMessage(msg);
                for (SocketType client : clientSocks) {
                    SendAll(client, msg + "\n");
                }
            }
            ++i;
        } else if (bytesReceived == 0) {
            CLOSE_SOCKET(sock);
            clientSocks.erase(clientSocks.begin() + i);
            AddMessage("客户端已断开连接");
        } else {
            ++i; // 非阻塞无数据或临时错误
        }
    }

    // 服务器输入
    for (uint32_t cp : TakeInputCodepoints()) {
        if (cp >= 32 && cp != 127) AppendUtf8(serverInput, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE) && !serverInput.empty()) Utf8PopChar(serverInput);
    if ((IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) && !serverInput.empty()) {
        std::string msg = "服务器：" + serverInput;
        AddMessage(msg);
        for (SocketType sock : clientSocks) {
            SendAll(sock, msg + "\n");
        }
        serverInput.clear();
    }

    // 绘制
    BeginDrawing();
    ClearBackground(RAYWHITE);
    DrawTextC("聊天服务器 - 消息记录：", 10, 10, UI_FONT_SIZE, DARKGRAY);
    int y = 40;
    int maxMessagesY = screenHeight - 60;
    {
        std::lock_guard<std::mutex> lock(messagesMutex);
        for (const auto& msg : chatMessages) {
            if (y > maxMessagesY) break;
            DrawTextC(msg.c_str(), 10, (float)y, UI_FONT_SIZE, BLACK);
            y += 25;
        }
    }
    DrawTextC("输入消息后按回车发送：", 10, (float)(screenHeight - 40), UI_FONT_SIZE, DARKGRAY);
    DrawRectangle(10, screenHeight - 20, screenWidth - 20, 1, LIGHTGRAY);
    DrawTextC(serverInput.c_str(), 10, (float)(screenHeight - 15), UI_FONT_SIZE, BLUE);
    EndDrawing();
}

// ---- 客户端模式 ----
void UpdateClientFrame() {
    int screenWidth = GetScreenWidth();
    int screenHeight = GetScreenHeight();

    char buffer[BUFFER_SIZE];
    int bytesReceived = recv(clientSock, buffer, BUFFER_SIZE - 1, 0);
    if (bytesReceived > 0) {
        buffer[bytesReceived] = '\0';
        std::string msg(buffer);
        TrimTrailingNewline(msg);
        if (!msg.empty()) AddMessage(msg);
    } else if (bytesReceived == 0) {
        AddMessage("与服务器的连接已断开");
        ShutdownClient();
        g_mode = AppMode::Select;
        SetWindowTitleC(L"选择模式", "Select Mode");
        return;
    }

    for (uint32_t cp : TakeInputCodepoints()) {
        if (cp >= 32 && cp != 127) AppendUtf8(clientInput, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE) && !clientInput.empty()) Utf8PopChar(clientInput);
    if ((IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) && !clientInput.empty()) {
        SendAll(clientSock, clientInput + "\n");
        AddMessage("你：" + clientInput);
        clientInput.clear();
    }

    BeginDrawing();
    ClearBackground(RAYWHITE);
    DrawTextC("聊天客户端 - 消息记录：", 10, 10, UI_FONT_SIZE, DARKGRAY);
    int y = 40;
    int maxMessagesY = screenHeight - 60;
    {
        std::lock_guard<std::mutex> lock(messagesMutex);
        for (const auto& msg : chatMessages) {
            if (y > maxMessagesY) break;
            DrawTextC(msg.c_str(), 10, (float)y, UI_FONT_SIZE, BLACK);
            y += 25;
        }
    }
    DrawTextC("输入消息后按回车发送：", 10, (float)(screenHeight - 40), UI_FONT_SIZE, DARKGRAY);
    DrawRectangle(10, screenHeight - 20, screenWidth - 20, 1, LIGHTGRAY);
    DrawTextC(clientInput.c_str(), 10, (float)(screenHeight - 15), UI_FONT_SIZE, BLUE);
    EndDrawing();
}

// ==================== 调试输出 ====================
void WriteDebugDump() {
    const char* dumpPath = std::getenv("TCPCHAT_DEBUG_CODEPOINTS");
    if (!dumpPath || !*dumpPath) return;
    std::ofstream f(dumpPath, std::ios::out | std::ios::trunc);
    if (!f.is_open()) return;
    for (uint32_t cp : g_debugLog) {
        f << "cp:0x" << std::hex << cp << std::dec << "\n";
    }
    f.close();
}

// ==================== 主函数 ====================
int main() {
    if (!InitNetwork()) {
        return 1;
    }

    SetConfigFlags(FLAG_WINDOW_RESIZABLE);
    InitWindow(DEFAULT_SCREEN_WIDTH, DEFAULT_SCREEN_HEIGHT, "TCP Chat");
    SetTargetFPS(60);

    // 加载中文字体(需在 OpenGL 上下文创建之后)
    g_font = LoadCjkFont();
#ifdef _WIN32
    InstallInputHook();
#endif
    SetWindowTitleC(L"选择模式", "Select Mode");

    while (!WindowShouldClose()) {
        switch (g_mode) {
            case AppMode::Select: UpdateSelectFrame(); break;
            case AppMode::Server:  UpdateServerFrame();  break;
            case AppMode::Client:  UpdateClientFrame();  break;
        }
    }

    // 清理
    if (g_mode == AppMode::Server) ShutdownServer();
    if (g_mode == AppMode::Client) ShutdownClient();
    WriteDebugDump();
#ifdef _WIN32
    RemoveInputHook();
#endif
    if (g_fontLoaded) UnloadFont(g_font);
    CloseWindow();
    CleanupNetwork();
    return 0;
}
