// TCP Chat 9.2
// 图形化局域网聊天程序: 完整中文支持 + IPv6 + 私聊 + 文件传输 + 时间戳/输入历史/多行输入/
//   搜索/撤回/表情/回复/并行传输/断点续传/取消/拖放/速度显示/用户颜色/心跳/踢出禁言/密码/
//   消息复制/清屏/字号/主题/托盘/通知/断线重连/ACK/简单加密/多房间
// 依赖: raylib 5.5 + Winsock2(Windows) / BSD socket(Linux)
//
// 协议: 换行分隔的 UTF-8 文本行, 字段以 '|' 分隔; 消息正文与引用经 Base64 编码以支持多行与 '|'
// 客户端->服务器:
//   AUTH|<password>  NAME|<nick>  ROOM|join|<name>  PONG|<ts>
//   MSG|<room>|<b64quote>|<b64text>           广播
//   PMSG|<targetId>|<b64quote>|<b64text>      私聊(0=服务器)
//   RECALL|<msgId>
//   FILE_OFFER|<targetId>|<fileName>|<size>|<fileId>
//   FILE_RESUME|<fileId>|<offset>  FILE_DATA|<fileId>|<b64>  FILE_DONE|<fileId>|<bytes>|<sha>
//   FILE_CANCEL|<fileId>|<reason>  FILE_ERROR|<fileId>|<msg>
// 服务器->客户端:
//   AUTH_REQ  AUTH_FAIL|<msg>  AUTH_OK
//   WELCOME|<id>|<name>  ROSTER|<id:name,...>  JOIN|<id>|<name>  LEAVE|<id>|<name>
//   RENAME|<id>|<old>|<new>  ROOM_OK|<name>  PING|<ts>  KICK|<msg>  MUTED|<0|1>
//   MSG|<msgId>|<senderId>|<senderName>|<room>|<b64quote>|<b64text>
//   PMSG|<msgId>|<senderId>|<senderName>|<b64quote>|<b64text>
//   MSG_ACK|<msgId>  RECALL|<msgId>
//   FILE_OFFER|<senderId>|<senderName>|<fileName>|<size>|<fileId>  (服务器->客户端)
//   FILE_RESUME|<fileId>|<offset>  FILE_DONE|<fileId>|<bytes>|<sha>
//   FILE_CANCEL|<fileId>|<reason>

#include <iostream>
#include <string>
#include <vector>
#include <map>
#include <mutex>
#include <cstring>
#include <cstdio>
#include <cwchar>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <algorithm>
#include <ctime>
#include <fstream>
#include <cstdlib>

#ifdef _WIN32
// raylib 与 Windows 头文件的兼容宏(NOGDI/NOUSER 排除冲突声明)
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
#include <raylib.h>

// ---- 手动声明所需 Win32 API(NOUSER 排除了 winuser.h) ----
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
#ifndef WM_LBUTTONUP
#define WM_LBUTTONUP 0x0202
#endif
#ifndef WM_RBUTTONUP
#define WM_RBUTTONUP 0x0205
#endif
#ifndef WM_CLOSE
#define WM_CLOSE 0x0010
#endif
#ifndef WM_APP
#define WM_APP 0x8000
#endif
#ifndef SW_HIDE
#define SW_HIDE 0
#endif
#ifndef SW_SHOW
#define SW_SHOW 5
#endif
#ifndef SW_RESTORE
#define SW_RESTORE 9
#endif
#ifndef IDI_APPLICATION
#define IDI_APPLICATION 32512
#endif
#ifndef NIM_ADD
#define NIM_ADD 0x00000000
#endif
#ifndef NIM_MODIFY
#define NIM_MODIFY 0x00000001
#endif
#ifndef NIM_DELETE
#define NIM_DELETE 0x00000002
#endif
#ifndef NIF_MESSAGE
#define NIF_MESSAGE 0x00000001
#endif
#ifndef NIF_ICON
#define NIF_ICON 0x00000002
#endif
#ifndef NIF_TIP
#define NIF_TIP 0x00000004
#endif
#ifndef FLASHW_ALL
#define FLASHW_ALL 0x00000003
#endif
#ifndef FLASHW_TIMERNOFG
#define FLASHW_TIMERNOFG 0x0000000C
#endif
#ifndef GMEM_MOVEABLE
#define GMEM_MOVEABLE 0x0002
#endif

typedef LRESULT (CALLBACK* WNDPROC)(HWND, UINT, WPARAM, LPARAM);

// 托盘图标结构(与 NOTIFYICONDATAW V2 布局一致, 64 位大小 952)
typedef struct {
    DWORD  cbSize;
    HWND   hWnd;
    UINT   uID;
    UINT   uFlags;
    UINT   uCallbackMessage;
    HICON  hIcon;
    WCHAR  szTip[128];
    DWORD  dwState;
    DWORD  dwStateMask;
    WCHAR  szInfo[256];
    UINT   uTimeout;
    WCHAR  szInfoTitle[64];
    DWORD  dwInfoFlags;
} TRAYICON_MINE;

// 任务栏闪烁结构
typedef struct {
    UINT   cbSize;
    HWND   hwnd;
    DWORD  dwFlags;
    UINT   uCount;
    DWORD  dwTimeout;
} FLASHW_MINE;

extern "C" {
__declspec(dllimport) LONG_PTR WINAPI GetWindowLongPtrW(HWND hWnd, int nIndex);
__declspec(dllimport) LONG_PTR WINAPI SetWindowLongPtrW(HWND hWnd, int nIndex, LONG_PTR dwNewLong);
__declspec(dllimport) LRESULT   WINAPI CallWindowProcW(WNDPROC lpPrevWndFunc, HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam);
__declspec(dllimport) BOOL      WINAPI SetWindowTextW(HWND hWnd, LPCWSTR lpString);
__declspec(dllimport) SHORT     WINAPI GetKeyState(int nVirtKey);
__declspec(dllimport) BOOL      WINAPI OpenClipboard(HWND hWndNewOwner);
__declspec(dllimport) BOOL      WINAPI EmptyClipboard(void);
__declspec(dllimport) HANDLE    WINAPI GetClipboardData(UINT uFormat);
__declspec(dllimport) HANDLE    WINAPI SetClipboardData(UINT uFormat, HANDLE hMem);
__declspec(dllimport) LPVOID    WINAPI GlobalLock(HGLOBAL hMem);
__declspec(dllimport) BOOL      WINAPI GlobalUnlock(HGLOBAL hMem);
__declspec(dllimport) HGLOBAL   WINAPI GlobalAlloc(UINT uFlags, SIZE_T dwBytes);
__declspec(dllimport) HGLOBAL   WINAPI GlobalFree(HGLOBAL hMem);
__declspec(dllimport) BOOL      WINAPI CloseClipboard(void);
__declspec(dllimport) HIMC      WINAPI ImmGetContext(HWND hWnd);
__declspec(dllimport) BOOL      WINAPI ImmReleaseContext(HWND hWnd, HIMC hIMC);
__declspec(dllimport) LONG      WINAPI ImmGetCompositionStringW(HIMC hIMC, DWORD dwIndex, LPVOID lpBuf, DWORD dwBufLen);
__declspec(dllimport) BOOL      WINAPI Shell_NotifyIconW(DWORD dwMessage, TRAYICON_MINE* lpData);
__declspec(dllimport) BOOL      WINAPI FlashWindowEx(FLASHW_MINE* pfwi);
__declspec(dllimport) BOOL      WINAPI MessageBeep(UINT uType);
__declspec(dllimport) HICON     WINAPI LoadIconW(HINSTANCE hInstance, LPCWSTR lpIconName);
__declspec(dllimport) BOOL      WINAPI ShowWindow(HWND hWnd, int nCmdShow);
__declspec(dllimport) BOOL      WINAPI SetForegroundWindow(HWND hWnd);
__declspec(dllimport) BOOL      WINAPI PostMessageW(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam);
// CreateDirectoryW / MultiByteToWideChar / WideCharToMultiByte 由核心头提供
}

// 文件打开对话框(comdlg32), 与 OPENFILENAMEW 布局一致
typedef struct {
    DWORD        lStructSize;
    HWND         hwndOwner;
    HINSTANCE    hInstance;
    const wchar_t* lpstrFilter;
    wchar_t*     lpstrCustomFilter;
    DWORD        nMaxCustFilter;
    DWORD        nFilterIndex;
    wchar_t*     lpstrFile;
    DWORD        nMaxFile;
    wchar_t*     lpstrFileTitle;
    DWORD        nMaxFileTitle;
    const wchar_t* lpstrInitialDir;
    const wchar_t* lpstrTitle;
    DWORD        Flags;
    WORD         nFileOffset;
    WORD         nFileExtension;
    const wchar_t* lpstrDefExt;
    LPARAM       lCustData;
    void*        lpfnHook;
    const wchar_t* lpTemplateName;
    void*        pvReserved;
    DWORD        dwReserved;
    DWORD        FlagsEx;
} OPENFILENAME_MINE;

#define CP_UTF8 65001
#define CP_ACP 0
#define OFN_FILEMUSTEXIST 0x1000
#define OFN_HIDEREADONLY 0x4
#define OFN_PATHMUSTEXIST 0x800
#define OFN_ALLOWMULTISELECT 0x200

extern "C" __declspec(dllimport) BOOL WINAPI GetOpenFileNameW(OPENFILENAME_MINE* lpofn);

typedef SOCKET SocketType;
#define CLOSE_SOCKET closesocket
#define SOCKET_ERROR_CODE SOCKET_ERROR
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/select.h>
#include <sys/stat.h>
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
constexpr int DEFAULT_PORT = 45678;   // 默认端口(避开 5555: ADB/安卓模拟器常用端口)
constexpr int PORT_FALLBACK_RANGE = 100; // 端口被占用时自动向后尝试的范围
constexpr int MAX_CLIENTS = 10;
constexpr int BUFFER_SIZE = 16384;
constexpr size_t MAX_LINE_LENGTH = 1024 * 1024;
constexpr int DEFAULT_SCREEN_WIDTH = 800;
constexpr int DEFAULT_SCREEN_HEIGHT = 600;
constexpr int MAX_MESSAGES = 300;
constexpr size_t FILE_CHUNK_RAW = 24 * 1024;
constexpr int FILE_CHUNKS_PER_FRAME = 4;
constexpr int MAX_PARALLEL_SENDS = 3;
constexpr int MAX_INPUT_LINES = 5;
constexpr double PING_INTERVAL = 5.0;
constexpr double PONG_TIMEOUT = 15.0;
constexpr double CLIENT_TIMEOUT = 25.0;
constexpr double RECONNECT_DELAY = 3.0;

// ==================== 主题与字号 ====================
int g_fontSize = 24;

struct ThemeColors {
    Color bg, panel, text, dim, sep, inputText, hl;
};
ThemeColors g_light = { RAYWHITE, LIGHTGRAY, BLACK, DARKGRAY, GRAY, BLUE, SKYBLUE };
ThemeColors g_dark  = { Color{30,30,34,255}, Color{50,50,58,255}, Color{225,225,230,255}, Color{150,150,160,255}, Color{80,80,90,255}, Color{120,180,255,255}, Color{70,90,130,255} };
ThemeColors g_theme = g_light;
bool g_darkMode = false;

void ApplyTheme() {
    g_theme = g_darkMode ? g_dark : g_light;
}

// 用户颜色(按 ID 分配)
Color UserColor(int id) {
    static const Color palette[8] = {
        Color{ 200, 60, 60, 255 }, Color{ 210, 130, 30, 255 }, Color{ 40, 140, 60, 255 },
        Color{ 50, 100, 200, 255 }, Color{ 140, 70, 180, 255 }, Color{ 120, 90, 50, 255 },
        Color{ 170, 40, 110, 255 }, Color{ 30, 130, 140, 255 }
    };
    return palette[((id % 8) + 8) % 8];
}

// ==================== 全局状态 ====================
std::mutex messagesMutex;
struct ChatMessage {
    long long msgId = 0;
    int senderId = 0;
    std::string senderName;
    std::string time;      // "HH:MM:SS"
    std::string room;
    std::string quote;     // 引用文本(可空)
    std::string text;      // 正文(可多行)
    bool pendingAck = false;
};
std::vector<ChatMessage> chatMessages;
int g_chatScrollPx = 0;      // 消息区像素滚动偏移
bool g_scrollDragging = false;

// 消息行(供点击/右键命中测试)
struct MsgRow { long long msgId; int senderId; float y, h; };
std::vector<MsgRow> g_msgRows;

std::vector<uint32_t> g_debugLog;
std::vector<std::string> g_eventLog;

void LogEvent(const std::string& e) {
    std::string s = e.size() > 200 ? e.substr(0, 200) + "..." : e;
    if (g_eventLog.size() < 200000) g_eventLog.push_back(s);
}

std::string NowTimeStr() {
    time_t t = time(nullptr);
    struct tm tmv;
#ifdef _WIN32
    localtime_s(&tmv, &t);
#else
    localtime_r(&t, &tmv);
#endif
    char buf[16];
    snprintf(buf, sizeof(buf), "%02d:%02d:%02d", tmv.tm_hour, tmv.tm_min, tmv.tm_sec);
    return buf;
}

// ==================== UTF 工具 ====================
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

size_t Utf8Next(const std::string& s, size_t p) {
    if (p >= s.size()) return s.size();
    unsigned char c = (unsigned char)s[p];
    size_t len = c < 0x80 ? 1 : (c < 0xE0 ? 2 : (c < 0xF0 ? 3 : 4));
    return std::min(s.size(), p + len);
}

size_t Utf8Prev(const std::string& s, size_t p) {
    if (p == 0) return 0;
    size_t q = p - 1;
    while (q > 0 && ((unsigned char)s[q] & 0xC0) == 0x80) --q;
    return q;
}

// 从 UTF-8 字符串中取一个码点
uint32_t Utf8Decode(const std::string& s, size_t p, size_t& adv) {
    unsigned char c = (unsigned char)s[p];
    uint32_t cp;
    if (c < 0x80) { cp = c; adv = 1; }
    else if (c < 0xE0) { cp = ((c & 0x1F) << 6) | (s[p + 1] & 0x3F); adv = 2; }
    else if (c < 0xF0) { cp = ((c & 0x0F) << 12) | ((s[p + 1] & 0x3F) << 6) | (s[p + 2] & 0x3F); adv = 3; }
    else { cp = ((c & 0x07) << 18) | ((s[p + 1] & 0x3F) << 12) | ((s[p + 2] & 0x3F) << 6) | (s[p + 3] & 0x3F); adv = 4; }
    return cp;
}

void InsertUtf8At(std::string& s, size_t& caret, uint32_t cp) {
    std::string u;
    AppendUtf8(u, cp);
    s.insert(caret, u);
    caret += u.size();
}

void DelCharBefore(std::string& s, size_t& caret) {
    if (caret == 0) return;
    size_t q = Utf8Prev(s, caret);
    s.erase(q, caret - q);
    caret = q;
}

void DelCharAt(std::string& s, size_t& caret) {
    if (caret >= s.size()) return;
    size_t q = Utf8Next(s, caret);
    s.erase(caret, q - caret);
}

#ifdef _WIN32
std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0);
    std::wstring out(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &out[0], n);
    return out;
}
std::string WideToUtf8(const std::wstring& s) {
    if (s.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), NULL, 0, NULL, NULL);
    std::string out(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &out[0], n, NULL, NULL);
    return out;
}
std::wstring AnsiToWide(const std::string& s) {
    if (s.empty()) return std::wstring();
    int n = MultiByteToWideChar(CP_ACP, 0, s.c_str(), (int)s.size(), NULL, 0);
    std::wstring out(n, 0);
    MultiByteToWideChar(CP_ACP, 0, s.c_str(), (int)s.size(), &out[0], n);
    return out;
}
#else
std::wstring AnsiToWide(const std::string& s) { std::wstring w; for (unsigned char c : s) w += (wchar_t)c; return w; }
std::string WideToUtf8(const std::wstring& s) { std::string u; for (wchar_t c : s) u += (char)(c & 0xFF); return u; }
std::wstring Utf8ToWide(const std::string& s) { return AnsiToWide(s); }
#endif

FILE* OpenFileWrite(const std::wstring& path) {
#ifdef _WIN32
    return _wfopen(path.c_str(), L"wb");
#else
    return fopen(WideToUtf8(path).c_str(), "wb");
#endif
}
FILE* OpenFileAppend(const std::wstring& path) {
#ifdef _WIN32
    return _wfopen(path.c_str(), L"ab");
#else
    return fopen(WideToUtf8(path).c_str(), "ab");
#endif
}
FILE* OpenFileRead(const std::wstring& path) {
#ifdef _WIN32
    return _wfopen(path.c_str(), L"rb");
#else
    return fopen(WideToUtf8(path).c_str(), "rb");
#endif
}
void FileSeekEnd(FILE* f) {
#ifdef _WIN32
    _fseeki64(f, 0, SEEK_END);
#else
    fseeko(f, 0, SEEK_END);
#endif
}
long long FileTell(FILE* f) {
#ifdef _WIN32
    return _ftelli64(f);
#else
    return (long long)ftello(f);
#endif
}
void FileSeek(FILE* f, long long pos) {
#ifdef _WIN32
    _fseeki64(f, pos, SEEK_SET);
#else
    fseeko(f, pos, SEEK_SET);
#endif
}
void EnsureReceivedDir() {
#ifdef _WIN32
    CreateDirectoryW(L"received", NULL);
#else
    mkdir("received", 0755);
#endif
}

void TrimLineEnd(std::string& s) {
    while (!s.empty() && (s.back() == '\n' || s.back() == '\r')) s.pop_back();
}

std::vector<std::string> SplitFields(const std::string& line, size_t maxFields) {
    std::vector<std::string> out;
    size_t pos = 0;
    while (out.size() + 1 < maxFields) {
        size_t p = line.find('|', pos);
        if (p == std::string::npos) break;
        out.push_back(line.substr(pos, p - pos));
        pos = p + 1;
    }
    out.push_back(line.substr(pos));
    return out;
}

long long ParseLL(const std::string& s) { return std::atoll(s.c_str()); }

std::string SizeStr(long long bytes) {
    if (bytes < 1024) return std::to_string(bytes) + " B";
    if (bytes < 1024 * 1024) return std::to_string(bytes / 1024) + " KB";
    if (bytes < 1024LL * 1024 * 1024) return std::to_string(bytes / (1024 * 1024)) + " MB";
    return std::to_string(bytes / (1024LL * 1024 * 1024)) + " GB";
}

// ==================== Base64 ====================
static const char* B64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
std::string Base64Encode(const unsigned char* data, size_t len) {
    std::string out;
    out.reserve((len + 2) / 3 * 4);
    size_t i = 0;
    while (i + 3 <= len) {
        uint32_t v = ((uint32_t)data[i] << 16) | ((uint32_t)data[i + 1] << 8) | (uint32_t)data[i + 2];
        out += B64_ALPHABET[(v >> 18) & 63];
        out += B64_ALPHABET[(v >> 12) & 63];
        out += B64_ALPHABET[(v >> 6) & 63];
        out += B64_ALPHABET[v & 63];
        i += 3;
    }
    if (len - i == 1) {
        uint32_t v = (uint32_t)data[i] << 16;
        out += B64_ALPHABET[(v >> 18) & 63];
        out += B64_ALPHABET[(v >> 12) & 63];
        out += "==";
    } else if (len - i == 2) {
        uint32_t v = ((uint32_t)data[i] << 16) | ((uint32_t)data[i + 1] << 8);
        out += B64_ALPHABET[(v >> 18) & 63];
        out += B64_ALPHABET[(v >> 12) & 63];
        out += B64_ALPHABET[(v >> 6) & 63];
        out += '=';
    }
    return out;
}
bool Base64Decode(const std::string& s, std::string& out) {
    auto val = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    out.clear();
    int buf = 0, bits = 0;
    for (char c : s) {
        int v = val(c);
        if (v < 0) continue;
        buf = (buf << 6) | v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out += (char)((buf >> bits) & 0xFF);
        }
    }
    return true;
}

// ==================== SHA-256 ====================
struct Sha256 {
    uint32_t state[8];
    uint64_t bitLen;
    unsigned char buf[64];
    size_t bufLen;
    static uint32_t ROR(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
    static uint32_t GetBE32(const unsigned char* p) {
        return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
    }
    static void PutBE32(unsigned char* p, uint32_t v) {
        p[0] = (unsigned char)(v >> 24); p[1] = (unsigned char)(v >> 16);
        p[2] = (unsigned char)(v >> 8);  p[3] = (unsigned char)v;
    }
    void Init() {
        state[0] = 0x6a09e667u; state[1] = 0xbb67ae85u; state[2] = 0x3c6ef372u; state[3] = 0xa54ff53au;
        state[4] = 0x510e527fu; state[5] = 0x9b05688cu; state[6] = 0x1f83d9abu; state[7] = 0x5be0cd19u;
        bitLen = 0; bufLen = 0;
    }
    void Transform(const unsigned char block[64]) {
        static const uint32_t K[64] = {
            0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
            0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u, 0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
            0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
            0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u, 0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
            0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u, 0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
            0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
            0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
            0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u, 0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u
        };
        uint32_t w[64];
        for (int i = 0; i < 16; ++i) w[i] = GetBE32(block + 4 * i);
        for (int i = 16; i < 64; ++i) {
            uint32_t s0 = ROR(w[i - 15], 7) ^ ROR(w[i - 15], 18) ^ (w[i - 15] >> 3);
            uint32_t s1 = ROR(w[i - 2], 17) ^ ROR(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = state[0], b = state[1], c = state[2], d = state[3];
        uint32_t e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = 0; i < 64; ++i) {
            uint32_t S1 = ROR(e, 6) ^ ROR(e, 11) ^ ROR(e, 25);
            uint32_t ch = (e & f) ^ (~e & g);
            uint32_t t1 = h + S1 + ch + K[i] + w[i];
            uint32_t S0 = ROR(a, 2) ^ ROR(a, 13) ^ ROR(a, 22);
            uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
            uint32_t t2 = S0 + maj;
            h = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + t2;
        }
        state[0] += a; state[1] += b; state[2] += c; state[3] += d;
        state[4] += e; state[5] += f; state[6] += g; state[7] += h;
    }
    void Update(const void* data, size_t len) {
        const unsigned char* p = (const unsigned char*)data;
        bitLen += (uint64_t)len * 8;
        if (bufLen > 0) {
            size_t need = 64 - bufLen;
            size_t take = len < need ? len : need;
            memcpy(buf + bufLen, p, take);
            bufLen += take; p += take; len -= take;
            if (bufLen == 64) { Transform(buf); bufLen = 0; }
        }
        while (len >= 64) { Transform(p); p += 64; len -= 64; }
        if (len > 0) { memcpy(buf, p, len); bufLen = len; }
    }
    void Final(unsigned char out[32]) {
        uint64_t bits = bitLen;
        buf[bufLen++] = 0x80;
        if (bufLen > 56) {
            while (bufLen < 64) buf[bufLen++] = 0;
            Transform(buf);
            bufLen = 0;
        }
        while (bufLen < 56) buf[bufLen++] = 0;
        for (int i = 0; i < 8; ++i) buf[56 + i] = (unsigned char)(bits >> (56 - 8 * i));
        Transform(buf);
        for (int i = 0; i < 8; ++i) PutBE32(out + 4 * i, state[i]);
    }
    static std::string Hex(const unsigned char d[32]) {
        static const char* hexdigits = "0123456789abcdef";
        std::string s;
        s.reserve(64);
        for (int i = 0; i < 32; ++i) {
            s += hexdigits[d[i] >> 4];
            s += hexdigits[d[i] & 15];
        }
        return s;
    }
};

// ==================== 简单 XOR 流加密 ====================
struct XorStream {
    uint64_t s = 0;
    static XorStream FromPassword(const std::string& pwd) {
        Sha256 h;
        h.Init();
        std::string m = pwd + "tcpchat-9.0-salt";
        h.Update(m.data(), m.size());
        unsigned char d[32];
        h.Final(d);
        XorStream xs;
        xs.s = 0;
        for (int i = 0; i < 8; ++i) xs.s = (xs.s << 8) | d[i];
        if (xs.s == 0) xs.s = 0x9E3779B97F4A7C15ull;
        return xs;
    }
    unsigned char Next() {
        s ^= s << 13;
        s ^= s >> 7;
        s ^= s << 17;
        return (unsigned char)(s & 0xFF);
    }
    void Apply(unsigned char* p, size_t n) {
        for (size_t i = 0; i < n; ++i) p[i] ^= Next();
    }
};

// ==================== 消息存储 ====================
void AddMessage(const ChatMessage& m) {
    std::lock_guard<std::mutex> lock(messagesMutex);
    chatMessages.push_back(m);
    if (chatMessages.size() > MAX_MESSAGES) {
        chatMessages.erase(chatMessages.begin(), chatMessages.begin() + (chatMessages.size() - MAX_MESSAGES));
    }
}

void AddMessageSimple(int senderId, const std::string& name, const std::string& text,
                      const std::string& quote = "", const std::string& room = "",
                      long long msgId = 0, bool pendingAck = false) {
    ChatMessage m;
    m.msgId = msgId;
    m.senderId = senderId;
    m.senderName = name;
    m.time = NowTimeStr();
    m.room = room;
    m.quote = quote;
    m.text = text;
    m.pendingAck = pendingAck;
    AddMessage(m);
}

bool RemoveMessageById(long long msgId) {
    std::lock_guard<std::mutex> lock(messagesMutex);
    for (auto it = chatMessages.begin(); it != chatMessages.end(); ++it) {
        if (it->msgId == msgId) {
            chatMessages.erase(it);
            return true;
        }
    }
    return false;
}

void ClearMessages() {
    std::lock_guard<std::mutex> lock(messagesMutex);
    chatMessages.clear();
    g_chatScrollPx = 0;
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
// 刷出非阻塞发送队列(可选 XOR 加密)
void FlushSendQueue(SocketType sock, std::string& buf, XorStream* xs) {
    while (!buf.empty()) {
        int n = send(sock, buf.data(), (int)buf.size(), 0);
        if (n == SOCKET_ERROR_CODE) {
#ifdef _WIN32
            if (WSAGetLastError() == WSAEWOULDBLOCK) break;
#else
            if (errno == EAGAIN || errno == EWOULDBLOCK) break;
#endif
            break;
        }
        buf.erase(0, (size_t)n);
    }
    (void)xs;
}

std::string SockAddrToString(const sockaddr* sa) {
    char buf[INET6_ADDRSTRLEN] = {0};
    if (sa->sa_family == AF_INET6) {
        const sockaddr_in6* s6 = (const sockaddr_in6*)sa;
        if (IN6_IS_ADDR_V4MAPPED(&s6->sin6_addr)) {
            inet_ntop(AF_INET, &s6->sin6_addr.s6_addr[12], buf, sizeof(buf));
            return buf;
        }
        inet_ntop(AF_INET6, &s6->sin6_addr, buf, sizeof(buf));
        return buf;
    }
    inet_ntop(AF_INET, &((const sockaddr_in*)sa)->sin_addr, buf, sizeof(buf));
    return buf;
}

// ==================== 中文字体 + 表情字体 ====================
Font g_font;
Font g_emojiFont;
bool g_fontLoaded = false;
bool g_emojiLoaded = false;

Font LoadCjkFont() {
    std::vector<int> codepoints;
    for (int cp = 0x20; cp <= 0x7E; ++cp) codepoints.push_back(cp);
    for (int cp = 0xA0; cp <= 0xFF; ++cp) codepoints.push_back(cp);
    for (int cp = 0x2000; cp <= 0x206F; ++cp) codepoints.push_back(cp);
    for (int cp = 0x3000; cp <= 0x303F; ++cp) codepoints.push_back(cp);
    for (int cp = 0x4E00; cp <= 0x9FFF; ++cp) codepoints.push_back(cp);
    for (int cp = 0xFF00; cp <= 0xFFEF; ++cp) codepoints.push_back(cp);
    std::string windir = "C:\\Windows";
    const char* envWindir = std::getenv("WINDIR");
    if (envWindir && *envWindir) windir = envWindir;
    std::vector<std::string> candidates = {
        windir + "\\Fonts\\Deng.ttf",
        windir + "\\Fonts\\simhei.ttf",
        windir + "\\Fonts\\Noto Sans SC (TrueType).otf",
        windir + "\\Fonts\\simkai.ttf",
        windir + "\\Fonts\\simfang.ttf",
        windir + "\\Fonts\\STXIHEI.TTF",
        windir + "\\Fonts\\STKAITI.TTF",
        windir + "\\Fonts\\STSONG.TTF",
        windir + "\\Fonts\\msyi.ttf",
        "/usr/share/fonts/opentype/noto/NotoSansSC-Regular.otf",
        "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
    };
    for (const auto& path : candidates) {
        if (!FileExists(path.c_str())) continue;
        Font f = LoadFontEx(path.c_str(), g_fontSize, codepoints.data(), (int)codepoints.size());
        if (f.texture.id != 0) {
            g_fontLoaded = true;
            return f;
        }
        UnloadFont(f);
    }
    return GetFontDefault();
}

Font LoadEmojiFont() {
    std::vector<int> codepoints;
    for (int cp = 0x2600; cp <= 0x27BF; ++cp) codepoints.push_back(cp);
    for (int cp = 0x1F000; cp <= 0x1FAFF; ++cp) codepoints.push_back(cp);
    std::string windir = "C:\\Windows";
    const char* envWindir = std::getenv("WINDIR");
    if (envWindir && *envWindir) windir = envWindir;
    std::vector<std::string> candidates = {
        windir + "\\Fonts\\seguiemj.ttf",
        windir + "\\Fonts\\NotoColorEmoji.ttf",
        "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
    };
    for (const auto& path : candidates) {
        if (!FileExists(path.c_str())) continue;
        Font f = LoadFontEx(path.c_str(), g_fontSize, codepoints.data(), (int)codepoints.size());
        if (f.texture.id != 0) {
            g_emojiLoaded = true;
            return f;
        }
        UnloadFont(f);
    }
    return GetFontDefault();
}

void ReloadFonts() {
    if (g_fontLoaded) UnloadFont(g_font);
    if (g_emojiLoaded) UnloadFont(g_emojiFont);
    g_fontLoaded = g_emojiLoaded = false;
    g_font = LoadCjkFont();
    g_emojiFont = LoadEmojiFont();
}

// 判断码点是否应使用表情字体
bool IsEmojiCp(uint32_t cp) {
    return cp >= 0x1F000 || (cp >= 0x2600 && cp <= 0x27BF);
}

// 混合字体绘制(中文用 g_font, 表情用 g_emojiFont)
void DrawTextMixed(const char* text, float x, float y, int size, Color color) {
    std::string s = text;
    float cx = x;
    size_t p = 0;
    while (p < s.size()) {
        size_t adv = 0;
        uint32_t cp = Utf8Decode(s, p, adv);
        std::string ch = s.substr(p, adv);
        Font f = (g_emojiLoaded && IsEmojiCp(cp)) ? g_emojiFont : g_font;
        DrawTextEx(f, ch.c_str(), Vector2{ cx, y }, (float)size, 1.0f, color);
        cx += MeasureTextEx(f, ch.c_str(), (float)size, 1.0f).x;
        p += adv;
    }
}

float MeasureTextMixed(const char* text, int size) {
    std::string s = text;
    float w = 0;
    size_t p = 0;
    while (p < s.size()) {
        size_t adv = 0;
        uint32_t cp = Utf8Decode(s, p, adv);
        std::string ch = s.substr(p, adv);
        Font f = (g_emojiLoaded && IsEmojiCp(cp)) ? g_emojiFont : g_font;
        w += MeasureTextEx(f, ch.c_str(), (float)size, 1.0f).x;
        p += adv;
    }
    return w;
}

// ==================== 绘制工具 ====================
void DrawTextC(const char* text, float x, float y, int size, Color color) {
    DrawTextMixed(text, x, y, size, color);
}
int MeasureTextC(const char* text, int size) {
    return (int)MeasureTextMixed(text, size);
}

void DrawTextCenteredInRect(const char* text, Rectangle rect, int fontSize, Color color) {
    int textWidth = MeasureTextC(text, fontSize);
    float textX = rect.x + (rect.width - textWidth) / 2;
    float textY = rect.y + (rect.height - fontSize) / 2;
    DrawTextC(text, textX, textY, fontSize, color);
}

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
    SetWindowTitle("TCP Chat");
#endif
}

// ==================== 文本换行与截断 ====================
// 按最大像素宽度将文本拆分为多行(逐字符贪心, 兼容中英文与表情)
std::vector<std::string> WrapText(const std::string& text, int size, float maxWidth) {
    std::vector<std::string> lines;
    std::string cur;
    auto flush = [&]() {
        lines.push_back(cur);
        cur.clear();
    };
    size_t p = 0;
    while (p <= text.size()) {
        size_t nl = text.find('\n', p);
        size_t end = (nl == std::string::npos) ? text.size() : nl;
        size_t q = p;
        while (q < end) {
            size_t adv = 0;
            Utf8Decode(text, q, adv);
            std::string ch = text.substr(q, adv);
            float w = MeasureTextC((cur + ch).c_str(), size);
            if (w > maxWidth && !cur.empty()) flush();
            cur += ch;
            q += adv;
        }
        flush();
        if (nl == std::string::npos) break;
        p = nl + 1;
    }
    if (text.empty()) lines.push_back("");
    while (lines.size() > 1 && lines.back().empty()) lines.pop_back();
    return lines;
}

// 按像素宽度截断文本(超出部分以 ... 结尾)
std::string TruncateToWidth(const std::string& s, int size, float maxW) {
    if (MeasureTextC(s.c_str(), size) <= maxW) return s;
    std::string out;
    size_t p = 0;
    while (p < s.size()) {
        size_t adv = 0;
        Utf8Decode(s, p, adv);
        std::string ch = s.substr(p, adv);
        if (MeasureTextC((out + ch + "...").c_str(), size) > maxW) break;
        out += ch;
        p += adv;
    }
    return out + "...";
}

// 绘制带光标的输入框文本
void DrawFieldText(const std::string& s, size_t caret, float x, float y, Color textColor, bool focused) {
    DrawTextC(s.c_str(), x, y, g_fontSize, textColor);
    if (focused && fmod(GetTime(), 1.0) < 0.5) {
        float cx = x + MeasureTextC(s.substr(0, caret).c_str(), g_fontSize);
        DrawRectangle((int)cx, (int)y + 2, 2, g_fontSize - 2, g_theme.dim);
    }
}

void HandleCaretKeys(const std::string& s, size_t& caret) {
    if (IsKeyPressed(KEY_LEFT)) caret = Utf8Prev(s, caret);
    if (IsKeyPressed(KEY_RIGHT)) caret = Utf8Next(s, caret);
    if (IsKeyPressed(KEY_HOME)) caret = 0;
    if (IsKeyPressed(KEY_END)) caret = s.size();
}

size_t CaretFromX(const std::string& s, int fontSize, float clickX, float textX) {
    float target = clickX - textX;
    std::vector<size_t> bounds;
    bounds.push_back(0);
    size_t p = 0;
    while (p < s.size()) {
        p = Utf8Next(s, p);
        bounds.push_back(p);
    }
    size_t best = 0;
    float bestDist = 1e9f;
    for (size_t b : bounds) {
        float w = (float)MeasureTextC(s.substr(0, b).c_str(), fontSize);
        float d = fabsf(w - target);
        if (d < bestDist) {
            bestDist = d;
            best = b;
        }
    }
    return best;
}

void UpdateIBeamCursor(const std::vector<Rectangle>& boxes) {
    Vector2 m = GetMousePosition();
    bool over = false;
    for (const auto& r : boxes) {
        if (CheckCollisionPointRec(m, r)) { over = true; break; }
    }
    SetMouseCursor(over ? MOUSE_CURSOR_IBEAM : MOUSE_CURSOR_DEFAULT);
}

std::string SanitizeName(const std::string& raw) {
    std::string out;
    size_t chars = 0;
    size_t p = 0;
    while (p < raw.size() && chars < 16) {
        unsigned char c = (unsigned char)raw[p];
        size_t len = c < 0x80 ? 1 : (c < 0xE0 ? 2 : (c < 0xF0 ? 3 : 4));
        if (p + len > raw.size()) break;
        std::string u = raw.substr(p, len);
        if (u != "|" && u != "\n" && u != "\r") {
            out += u;
            ++chars;
        }
        p += len;
    }
    return out;
}

void SetClipboardText(const std::string& utf8) {
#ifdef _WIN32
    std::wstring w = Utf8ToWide(utf8);
    if (!OpenClipboard(nullptr)) return;
    EmptyClipboard();
    size_t bytes = (w.size() + 1) * sizeof(wchar_t);
    HGLOBAL h = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (h) {
        void* p = GlobalLock(h);
        if (p) {
            memcpy(p, w.c_str(), bytes);
            GlobalUnlock(h);
        }
        SetClipboardData(CF_UNICODETEXT, h);
    }
    CloseClipboard();
#endif
}

// ==================== 应用状态机 ====================
enum class AppMode { Select, Server, Client };
AppMode g_mode = AppMode::Select;
bool g_quitRequested = false;

// 选择模式
std::string ipInput = "127.0.0.1";
std::string nameInput;
std::string passwordInput;
size_t g_ipCaret = ipInput.size();
size_t g_nameCaret = 0;
size_t g_pwdCaret = 0;
enum class SelFocus { Name, Pwd, Ip };
SelFocus g_selFocus = SelFocus::Name;

// 服务器模式
int g_serverPort = DEFAULT_PORT;   // 服务器实际监听端口
int g_clientPort = DEFAULT_PORT;   // 客户端实际连接端口
std::string g_clientHost = "127.0.0.1";
SocketType listenSock = INVALID_SOCKET;
struct ClientInfo {
    SocketType sock = INVALID_SOCKET;
    int id = 0;
    std::string name;
    std::string room = "大厅";
    std::string recvBuf;
    std::string sendBuf;
    XorStream xorSend, xorRecv;
    bool xorEnabled = false;
    bool pendingXorEnable = false;
    bool pendingWelcome = false;
    bool authed = true;
    double authDeadline = 0;
    double lastPong = 0;
    bool muted = false;
};
std::vector<ClientInfo> g_clients;
int g_nextClientId = 1;
std::string g_serverPassword;
long long g_serverMsgId = 1;
long long g_nextFileId = 1000;
std::map<std::pair<int, int>, int> g_fileIdMap;     // (senderId, clientFileId) -> serverFileId
std::map<int, int> g_fileRelayTarget;               // serverFileId -> 目标客户端 id
std::map<int, std::pair<int, int>> g_fileReverse;   // serverFileId -> (senderId, clientFileId)
struct RecallRoute { int senderId; int targetId; std::string room; };
std::map<long long, RecallRoute> g_msgRoutes;
std::string serverInput;
size_t g_serverCaret = 0;
int g_serverTargetId = 0;
double g_lastPingTime = 0;

// 客户端模式
SocketType clientSock = INVALID_SOCKET;
std::string g_clientRecvBuf;
std::string g_clientSendBuf;
XorStream g_clientXorSend, g_clientXorRecv;
bool g_clientXorEnabled = false;
bool g_clientPendingXorEnable = false;
std::string clientInput;
size_t g_clientCaret = 0;
int g_myId = 0;
std::string g_myName;
std::string g_room = "大厅";
std::map<int, std::string> g_roster;
int g_clientTargetId = 0;
double g_lastRecvAt = 0;
double g_reconnectAt = 0;
std::string g_clientPassword;

// 输入历史(聊天输入框)
std::vector<std::string> g_inputHistory;
int g_historyIdx = -1;
std::string g_historySaved;

// 搜索
bool g_searchActive = false;
std::string g_searchText;
size_t g_searchCaret = 0;

// 表情面板
bool g_emojiOpen = false;
const std::vector<uint32_t> g_emojiList = {
    0x1F600, 0x1F602, 0x1F923, 0x1F60A, 0x1F60D, 0x1F914, 0x1F605, 0x1F62D,
    0x1F44D, 0x1F44E, 0x2764, 0x1F525, 0x1F389, 0x1F64F, 0x1F4AA, 0x1F91D,
    0x1F634, 0x1F631, 0x1F644, 0x1F609, 0x1F60E, 0x1F973, 0x1F917, 0x1F607
};

// 回复
struct ReplyState { bool active = false; std::string name; std::string text; };
ReplyState g_reply;

// 右键菜单
struct CtxMenu { bool open = false; long long msgId = 0; int senderId = 0; float x = 0, y = 0; };
CtxMenu g_ctx;

// 房间输入
std::string g_roomInput;
size_t g_roomCaret = 0;
bool g_roomFocus = false;

// ==================== 文件传输 ====================
struct SpeedMeter {
    long long bytes = 0;
    double lastT = 0;
    double speed = 0;
    void Update(long long totalBytes) {
        double t = GetTime();
        if (lastT == 0) { lastT = t; bytes = totalBytes; return; }
        double dt = t - lastT;
        if (dt >= 0.5) {
            speed = (double)(totalBytes - bytes) / dt;
            bytes = totalBytes;
            lastT = t;
        }
    }
    std::string SpeedStr() const {
        if (speed < 1024) return std::to_string((long long)speed) + " B/s";
        if (speed < 1024 * 1024) return std::to_string((long long)(speed / 1024)) + " KB/s";
        return std::to_string((long long)(speed / (1024 * 1024))) + " MB/s";
    }
};

struct FileReceiveState {
    int fileId = 0;
    std::wstring path;      // 最终路径
    std::wstring partPath;  // .part 临时路径
    FILE* f = nullptr;
    long long size = 0, received = 0;
    std::string name, senderLabel;
    Sha256 sha;
    SpeedMeter speed;
};
std::map<int, FileReceiveState> g_fileRecv;

enum class SendPhase { AwaitingResume, HashingPrefix, Streaming };
struct FileSendState {
    bool active = false;
    int fileId = 0;
    std::wstring path;
    std::string name;
    long long size = 0, sent = 0;
    FILE* f = nullptr;
    int targetId = 0;
    Sha256 sha;
    SendPhase phase = SendPhase::AwaitingResume;
    long long prefixRemaining = 0;
    SpeedMeter speed;
};
std::vector<FileSendState> g_fileSends;

// ==================== Windows 输入子系统(IME + 粘贴 + 托盘) ====================
#ifdef _WIN32
WNDPROC g_originalWndProc = nullptr;
bool g_inTray = false;
std::mutex inputMutex;
std::vector<uint32_t> inputQueue;
uint32_t g_pendingHighSurrogate = 0;

void PushInputChar(uint32_t cp) {
    std::lock_guard<std::mutex> lock(inputMutex);
    if (g_pendingHighSurrogate) {
        if (cp >= 0xDC00 && cp <= 0xDFFF) {
            cp = 0x10000 + ((g_pendingHighSurrogate - 0xD800) << 10) + (cp - 0xDC00);
            g_pendingHighSurrogate = 0;
        } else {
            g_pendingHighSurrogate = 0;
        }
    } else if (cp >= 0xD800 && cp <= 0xDBFF) {
        g_pendingHighSurrogate = cp;
        return;
    }
    inputQueue.push_back(cp);
    g_debugLog.push_back(cp);
}

void PushClipboardText() {
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

void TrayAdd() {
    HWND hwnd = (HWND)GetWindowHandle();
    if (!hwnd) return;
    TRAYICON_MINE nid;
    memset(&nid, 0, sizeof(nid));
    nid.cbSize = sizeof(nid);
    nid.hWnd = hwnd;
    nid.uID = 1;
    nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    nid.uCallbackMessage = WM_APP + 1;
    nid.hIcon = LoadIconW(nullptr, (LPCWSTR)(LONG_PTR)IDI_APPLICATION);
    wcscpy(nid.szTip, L"TCP Chat");
    Shell_NotifyIconW(NIM_ADD, &nid);
    g_inTray = true;
}

void TrayRemove() {
    if (!g_inTray) return;
    HWND hwnd = (HWND)GetWindowHandle();
    TRAYICON_MINE nid;
    memset(&nid, 0, sizeof(nid));
    nid.cbSize = sizeof(nid);
    nid.hWnd = hwnd;
    nid.uID = 1;
    Shell_NotifyIconW(NIM_DELETE, &nid);
    g_inTray = false;
}

void RestoreFromTray() {
    HWND hwnd = (HWND)GetWindowHandle();
    if (!hwnd) return;
    ShowWindow(hwnd, SW_RESTORE);
    ShowWindow(hwnd, SW_SHOW);
    SetForegroundWindow(hwnd);
    TrayRemove();
}

// 新消息提醒(窗口未激活时)
void NotifyNewMessage() {
    if (IsWindowFocused()) return;
    HWND hwnd = (HWND)GetWindowHandle();
    if (hwnd) {
        FLASHW_MINE fi;
        memset(&fi, 0, sizeof(fi));
        fi.cbSize = sizeof(fi);
        fi.hwnd = hwnd;
        fi.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
        fi.uCount = 4;
        fi.dwTimeout = 0;
        FlashWindowEx(&fi);
    }
    MessageBeep(0xFFFFFFFF);
}

LRESULT CALLBACK ChatWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_CHAR: {
            uint32_t cp = (uint32_t)wParam;
            if (cp >= 32 && cp < 0x80) PushInputChar(cp);
            return 0;
        }
        case WM_IME_CHAR: {
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
            return 0;
        }
        case WM_KEYDOWN: {
            bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
            if ((ctrl && wParam == 'V') || (shift && wParam == VK_INSERT)) {
                PushClipboardText();
                return 0;
            }
            break;
        }
        case WM_APP + 1: {
            // 托盘图标消息
            if (lParam == WM_LBUTTONUP) {
                RestoreFromTray();
            } else if (lParam == WM_RBUTTONUP) {
                TrayRemove();
                PostMessageW(hwnd, WM_CLOSE, 0, 0);
            }
            return 0;
        }
        default: break;
    }
    return CallWindowProcW(g_originalWndProc, hwnd, msg, wParam, lParam);
}

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
#endif

std::vector<uint32_t> TakeInputCodepoints() {
    std::lock_guard<std::mutex> lock(inputMutex);
    std::vector<uint32_t> out;
    out.swap(inputQueue);
    return out;
}

// ==================== 服务器逻辑 ====================
ClientInfo* FindClientById(int id) {
    for (auto& c : g_clients) if (c.id == id) return &c;
    return nullptr;
}
std::string ClientNameOf(int id) {
    ClientInfo* c = FindClientById(id);
    return c ? c->name : ("用户" + std::to_string(id));
}
std::string TargetLabel(int targetId) {
    if (targetId == 0) return "广播";
    if (g_mode == AppMode::Server) {
        ClientInfo* c = FindClientById(targetId);
        return c ? c->name : ("用户" + std::to_string(targetId));
    }
    auto it = g_roster.find(targetId);
    if (it != g_roster.end()) return it->second;
    return "用户" + std::to_string(targetId);
}
void ServerSendToClient(int id, const std::string& line) {
    ClientInfo* c = FindClientById(id);
    if (c) {
        c->sendBuf += line;
        LogEvent("SEND->" + std::to_string(id) + " " + line);
    }
}
void ServerBroadcastOthers(const std::string& line, SocketType exceptSock) {
    for (auto& c : g_clients) {
        if (c.sock != exceptSock) c.sendBuf += line;
    }
    LogEvent("BCAST " + line);
}
void ServerBroadcastRoom(const std::string& room, const std::string& line, SocketType exceptSock) {
    for (auto& c : g_clients) {
        if (c.room == room && c.sock != exceptSock) c.sendBuf += line;
    }
    LogEvent("BCAST-ROOM " + room + " " + line);
}
void ServerBroadcastAll(const std::string& line) {
    for (auto& c : g_clients) c.sendBuf += line;
    LogEvent("BCAST " + line);
}
std::string RosterLine() {
    std::string out = "ROSTER|";
    bool first = true;
    for (const auto& c : g_clients) {
        if (!first) out += ",";
        out += std::to_string(c.id) + ":" + c.name;
        first = false;
    }
    return out;
}

// 服务器广播一条聊天消息(系统注入)
void ServerSystemMessage(const std::string& room, const std::string& text, SocketType exceptSock) {
    long long id = g_serverMsgId++;
    std::string line = "MSG|" + std::to_string(id) + "|0|服务器|" + room + "||" + Base64Encode((const unsigned char*)text.data(), text.size()) + "\n";
    ServerBroadcastRoom(room, line, exceptSock);
    g_msgRoutes[id] = RecallRoute{ 0, 0, room };
}

// 文件接收(服务器与客户端共用), 返回续传偏移
bool PrepareReceiveFile(int fileId, const std::string& fileNameUtf8, long long size, const std::string& senderLabel, long long& outResumeOffset) {
    std::string safeName = fileNameUtf8;
    for (auto& ch : safeName) {
        if (ch == '\\' || ch == '/' || ch == '|' || ch == ':' || ch == '*'
            || ch == '?' || ch == '"' || ch == '<' || ch == '>') ch = '_';
    }
    EnsureReceivedDir();
    std::wstring wname = Utf8ToWide(safeName);
#ifdef _WIN32
    std::wstring sep = L"\\";
#else
    std::wstring sep = L"/";
#endif
    std::wstring finalPath = L"received" + sep + wname;
    int suffix = 1;
    while (true) {
        FILE* probe = OpenFileRead(finalPath);
        if (!probe) break;
        fclose(probe);
        finalPath = L"received" + sep + wname.substr(0, wname.find_last_of(L'.')) + L"_" + std::to_wstring(suffix)
                  + (wname.find_last_of(L'.') != std::wstring::npos ? wname.substr(wname.find_last_of(L'.')) : L"");
        ++suffix;
    }
    std::wstring partPath = finalPath + L".part";
    // 断点续传: 检查已有 .part
    long long resumeOffset = 0;
    FILE* probe = OpenFileRead(partPath);
    if (probe) {
        FileSeekEnd(probe);
        long long partSize = FileTell(probe);
        fclose(probe);
        if (partSize > 0 && partSize < size) resumeOffset = partSize;
        else if (partSize >= size) {
            // 已有完整 .part, 重新接收
            resumeOffset = 0;
        }
    }
    FILE* f = resumeOffset > 0 ? OpenFileAppend(partPath) : OpenFileWrite(partPath);
    if (!f) return false;
    FileReceiveState st;
    st.fileId = fileId;
    st.path = finalPath;
    st.partPath = partPath;
    st.f = f;
    st.size = size;
    st.received = resumeOffset;
    st.name = safeName;
    st.senderLabel = senderLabel;
    st.sha.Init();
    // 续传时重算已接收部分的哈希
    if (resumeOffset > 0) {
        FILE* pf = OpenFileRead(partPath);
        if (pf) {
            std::vector<unsigned char> buf(256 * 1024);
            size_t n;
            while ((n = fread(buf.data(), 1, buf.size(), pf)) > 0) st.sha.Update(buf.data(), n);
            fclose(pf);
        }
    }
    g_fileRecv[fileId] = st;
    if (resumeOffset > 0) {
        AddMessageSimple(0, "系统", "文件 " + safeName + " 断点续传，从 " + SizeStr(resumeOffset) + " 继续");
    }
    AddMessageSimple(0, "系统", senderLabel + " 发来文件：" + safeName + "（" + SizeStr(size) + "）");
    LogEvent("FILE_START " + std::to_string(fileId) + " " + safeName + " " + std::to_string(size) + " resume:" + std::to_string(resumeOffset));
    outResumeOffset = resumeOffset;
    return true;
}

bool ReceiveFileChunk(int fileId, const std::string& b64) {
    auto it = g_fileRecv.find(fileId);
    if (it == g_fileRecv.end()) return false;
    std::string raw;
    Base64Decode(b64, raw);
    if (!raw.empty()) {
        fwrite(raw.data(), 1, raw.size(), it->second.f);
        it->second.received += (long long)raw.size();
        it->second.sha.Update(raw.data(), raw.size());
        it->second.speed.Update(it->second.received);
    }
    return true;
}

void FinishReceiveFile(int fileId, long long bytes, const std::string& shaHex) {
    auto it = g_fileRecv.find(fileId);
    if (it == g_fileRecv.end()) return;
    fclose(it->second.f);
    std::wstring partPath = it->second.partPath;
    std::wstring finalPath = it->second.path;
    std::string name = it->second.name;
    long long size = it->second.size;
    long long recv = it->second.received;
    unsigned char digest[32];
    it->second.sha.Final(digest);
    std::string actual = Sha256::Hex(digest);
    g_fileRecv.erase(it);
    if (bytes >= 0) recv = bytes;
    if (recv != size) {
        AddMessageSimple(0, "系统", "文件接收不完整：" + name + "（" + std::to_string(recv) + "/" + std::to_string(size) + " 字节），保留 .part 可断点续传");
        LogEvent("FILE_INCOMPLETE " + name + " " + std::to_string(recv) + "/" + std::to_string(size));
    } else if (actual != shaHex) {
        AddMessageSimple(0, "系统", "文件接收完成：" + name + "，但 SHA-256 校验失败（文件可能已损坏）");
        LogEvent("FILE_SAVED " + WideToUtf8(finalPath) + " " + std::to_string(size) + " sha:mismatch");
    } else {
#ifdef _WIN32
        _wrename(partPath.c_str(), finalPath.c_str());
#else
        rename(WideToUtf8(partPath).c_str(), WideToUtf8(finalPath).c_str());
#endif
        AddMessageSimple(0, "系统", "文件接收完成：" + name + "（" + SizeStr(size) + "）已保存到 received 目录，SHA-256 校验通过");
        LogEvent("FILE_SAVED " + WideToUtf8(finalPath) + " " + std::to_string(size) + " sha:ok");
    }
}

void AbortReceiveFile(int fileId, const std::string& msg) {
    auto it = g_fileRecv.find(fileId);
    if (it == g_fileRecv.end()) return;
    fclose(it->second.f);
    std::string name = it->second.name;
    g_fileRecv.erase(it);
    AddMessageSimple(0, "系统", "文件接收已取消：" + name + "（" + msg + "），进度已保留");
}

// 服务器: 处理一条来自客户端的协议行
void ServerHandleLine(const std::string& line, ClientInfo& from) {
    LogEvent("RECV " + from.name + " " + line);
    std::vector<std::string> f = SplitFields(line, 7);
    if (f.empty()) return;
    const std::string& t = f[0];

    if (!from.authed) {
        if (t == "AUTH" && f.size() >= 2) {
            if (f[1] == g_serverPassword) {
                from.authed = true;
                // AUTH_OK 明文发送, 队列清空后启用加密, 再发 WELCOME 等(加密)
                from.sendBuf += "AUTH_OK\n";
                from.pendingXorEnable = true;
                from.pendingWelcome = true;
                LogEvent("AUTH_OK " + from.name);
            } else {
                // 明文发送失败通知(尽力)
                std::string fail = "AUTH_FAIL|密码错误\n";
                send(from.sock, fail.data(), (int)fail.size(), 0);
                AddMessageSimple(0, "系统", from.name + " 密码验证失败，已断开");
                CLOSE_SOCKET(from.sock);
                from.sock = INVALID_SOCKET; // 标记待清理
            }
        }
        return;
    }

    if (t == "NAME" && f.size() >= 2) {
        std::string newName = SanitizeName(f[1]);
        if (!newName.empty() && newName != from.name) {
            std::string oldName = from.name;
            from.name = newName;
            AddMessageSimple(0, "系统", oldName + " 改名为 " + newName);
            ServerSendToClient(from.id, "WELCOME|" + std::to_string(from.id) + "|" + newName + "\n");
            ServerBroadcastOthers("RENAME|" + std::to_string(from.id) + "|" + oldName + "|" + newName + "\n", from.sock);
            ServerBroadcastAll(RosterLine() + "\n");
        }
    } else if (t == "ROOM" && f.size() >= 3 && f[1] == "join") {
        std::string newRoom = SanitizeName(f[2]);
        if (newRoom.empty()) newRoom = "大厅";
        if (newRoom != from.room) {
            std::string oldRoom = from.room;
            from.room = newRoom;
            ServerSendToClient(from.id, "ROOM_OK|" + newRoom + "\n");
            ServerSystemMessage(oldRoom, from.name + " 离开了房间", from.sock);
            ServerSystemMessage(newRoom, from.name + " 进入了房间", from.sock);
            LogEvent("ROOM " + from.name + " " + oldRoom + " -> " + newRoom);
        }
    } else if (t == "PONG") {
        from.lastPong = GetTime();
    } else if (t == "MSG" && f.size() >= 4) {
        if (from.muted) {
            ServerSendToClient(from.id, "MUTED|1\n");
            return;
        }
        std::string room = f[1];
        std::string quote, text;
        Base64Decode(f[2], quote);
        Base64Decode(f[3], text);
        if (text.empty()) return;
        long long msgId = g_serverMsgId++;
        AddMessageSimple(from.id, from.name, text, quote, from.room);
        std::string bq = Base64Encode((const unsigned char*)quote.data(), quote.size());
        std::string bt = Base64Encode((const unsigned char*)text.data(), text.size());
        ServerBroadcastRoom(from.room, "MSG|" + std::to_string(msgId) + "|" + std::to_string(from.id) + "|" + from.name + "|" + from.room + "|" + bq + "|" + bt + "\n", from.sock);
        ServerSendToClient(from.id, "MSG_ACK|" + std::to_string(msgId) + "\n");
        g_msgRoutes[msgId] = RecallRoute{ from.id, 0, from.room };
    } else if (t == "PMSG" && f.size() >= 4) {
        if (from.muted) {
            ServerSendToClient(from.id, "MUTED|1\n");
            return;
        }
        int targetId = (int)ParseLL(f[1]);
        std::string quote, text;
        Base64Decode(f[2], quote);
        Base64Decode(f[3], text);
        if (text.empty()) return;
        long long msgId = g_serverMsgId++;
        std::string bq = Base64Encode((const unsigned char*)quote.data(), quote.size());
        std::string bt = Base64Encode((const unsigned char*)text.data(), text.size());
        if (targetId == 0) {
            AddMessageSimple(from.id, from.name, text, quote);
            AddMessageSimple(from.id, from.name + "（私聊服务器）", "", quote);
        } else if (FindClientById(targetId)) {
            AddMessageSimple(from.id, from.name + " → " + ClientNameOf(targetId), text, quote);
            ServerSendToClient(targetId, "PMSG|" + std::to_string(msgId) + "|" + std::to_string(from.id) + "|" + from.name + "|" + bq + "|" + bt + "\n");
        }
        ServerSendToClient(from.id, "MSG_ACK|" + std::to_string(msgId) + "\n");
        g_msgRoutes[msgId] = RecallRoute{ from.id, targetId, from.room };
    } else if (t == "RECALL" && f.size() >= 2) {
        long long msgId = ParseLL(f[1]);
        auto it = g_msgRoutes.find(msgId);
        if (it == g_msgRoutes.end()) return;
        if (it->second.senderId != from.id) return;
        RemoveMessageById(msgId);
        if (it->second.targetId == 0) {
            ServerBroadcastRoom(it->second.room, "RECALL|" + std::to_string(msgId) + "\n", INVALID_SOCKET);
        } else {
            ServerSendToClient(it->second.targetId, "RECALL|" + std::to_string(msgId) + "\n");
        }
        ServerSendToClient(from.id, "RECALL|" + std::to_string(msgId) + "\n");
        g_msgRoutes.erase(it);
        LogEvent("RECALL " + std::to_string(msgId));
    } else if (t == "FILE_OFFER" && f.size() >= 5) {
        int targetId = (int)ParseLL(f[1]);
        std::string fileName = f[2];
        long long size = ParseLL(f[3]);
        int clientFileId = (int)ParseLL(f[4]);
        int serverFileId = g_nextFileId++;
        g_fileIdMap[std::make_pair(from.id, clientFileId)] = serverFileId;
        g_fileReverse[serverFileId] = std::make_pair(from.id, clientFileId);
        if (targetId == 0) {
            long long resumeOffset = 0;
            if (PrepareReceiveFile(serverFileId, fileName, size, from.name, resumeOffset)) {
                ServerSendToClient(from.id, "FILE_RESUME|" + std::to_string(clientFileId) + "|" + std::to_string(resumeOffset) + "\n");
            }
        } else if (FindClientById(targetId)) {
            g_fileRelayTarget[serverFileId] = targetId;
            ServerSendToClient(targetId, "FILE_OFFER|" + std::to_string(from.id) + "|" + from.name + "|" + fileName + "|" + std::to_string(size) + "|" + std::to_string(serverFileId) + "\n");
        } else {
            g_fileIdMap.erase(std::make_pair(from.id, clientFileId));
            g_fileReverse.erase(serverFileId);
            ServerSendToClient(from.id, "FILE_ERROR|" + std::to_string(clientFileId) + "|对方已离线\n");
        }
    } else if (t == "FILE_RESUME" && f.size() >= 3) {
        int clientFileId = (int)ParseLL(f[1]);
        long long offset = ParseLL(f[2]);
        // 服务器自己发送的文件(接收方回复的续传偏移)
        for (auto& fs : g_fileSends) {
            if (fs.active && fs.fileId == clientFileId) {
                fs.sent = offset;
                fs.phase = offset > 0 ? SendPhase::HashingPrefix : SendPhase::Streaming;
                fs.prefixRemaining = offset;
                FileSeek(fs.f, 0);
                AddMessageSimple(0, "系统", "文件 " + fs.name + " 断点续传，从 " + SizeStr(offset) + " 继续");
                return;
            }
        }
        auto mit = g_fileIdMap.find(std::make_pair(from.id, clientFileId));
        if (mit == g_fileIdMap.end()) return;
        int serverFileId = mit->second;
        if (g_fileRecv.count(serverFileId)) {
            // 文件发给服务器: 直接回复发送方
            auto rit = g_fileReverse.find(serverFileId);
            int senderId = rit != g_fileReverse.end() ? rit->second.first : from.id;
            ServerSendToClient(senderId, "FILE_RESUME|" + std::to_string(clientFileId) + "|" + std::to_string(offset) + "\n");
        } else if (g_fileRelayTarget.count(serverFileId)) {
            // 中转: 回复发送方
            auto rit = g_fileReverse.find(serverFileId);
            int senderId = rit != g_fileReverse.end() ? rit->second.first : from.id;
            ServerSendToClient(senderId, "FILE_RESUME|" + std::to_string(clientFileId) + "|" + std::to_string(offset) + "\n");
        }
    } else if (t == "FILE_DATA" && f.size() >= 3) {
        int clientFileId = (int)ParseLL(f[1]);
        auto mit = g_fileIdMap.find(std::make_pair(from.id, clientFileId));
        if (mit == g_fileIdMap.end()) return;
        int serverFileId = mit->second;
        if (g_fileRecv.count(serverFileId)) {
            ReceiveFileChunk(serverFileId, f[2]);
        } else if (g_fileRelayTarget.count(serverFileId)) {
            ServerSendToClient(g_fileRelayTarget[serverFileId], "FILE_DATA|" + std::to_string(serverFileId) + "|" + f[2] + "\n");
        }
    } else if (t == "FILE_DONE" && f.size() >= 4) {
        int clientFileId = (int)ParseLL(f[1]);
        long long bytes = ParseLL(f[2]);
        std::string shaHex = f[3];
        auto mit = g_fileIdMap.find(std::make_pair(from.id, clientFileId));
        if (mit == g_fileIdMap.end()) return;
        int serverFileId = mit->second;
        if (g_fileRecv.count(serverFileId)) {
            FinishReceiveFile(serverFileId, bytes, shaHex);
        } else if (g_fileRelayTarget.count(serverFileId)) {
            ServerSendToClient(g_fileRelayTarget[serverFileId], "FILE_DONE|" + std::to_string(serverFileId) + "|" + std::to_string(bytes) + "|" + shaHex + "\n");
            g_fileRelayTarget.erase(serverFileId);
        }
        g_fileIdMap.erase(mit);
        g_fileReverse.erase(serverFileId);
    } else if (t == "FILE_CANCEL" && f.size() >= 3) {
        int clientFileId = (int)ParseLL(f[1]);
        std::string reason = f[2];
        auto mit = g_fileIdMap.find(std::make_pair(from.id, clientFileId));
        if (mit != g_fileIdMap.end()) {
            int serverFileId = mit->second;
            if (g_fileRecv.count(serverFileId)) {
                AbortReceiveFile(serverFileId, reason);
            } else if (g_fileRelayTarget.count(serverFileId)) {
                ServerSendToClient(g_fileRelayTarget[serverFileId], "FILE_CANCEL|" + std::to_string(serverFileId) + "|" + reason + "\n");
                g_fileRelayTarget.erase(serverFileId);
            }
            g_fileIdMap.erase(mit);
            g_fileReverse.erase(serverFileId);
            LogEvent("FILE_CANCEL " + std::to_string(clientFileId) + " " + reason);
        } else {
            // 接收方取消: 查找反向映射, 通知发送方
            for (auto rit = g_fileReverse.begin(); rit != g_fileReverse.end(); ++rit) {
                if (rit->second.second == clientFileId && rit->second.first == from.id) {
                    int serverFileId = rit->first;
                    int senderId = -1;
                    for (auto mit2 = g_fileIdMap.begin(); mit2 != g_fileIdMap.end(); ++mit2) {
                        if (mit2->second == serverFileId) { senderId = mit2->first.first; break; }
                    }
                    if (senderId > 0) {
                        ServerSendToClient(senderId, "FILE_CANCEL|" + std::to_string(clientFileId) + "|" + reason + "\n");
                    }
                    break;
                }
            }
        }
    } else if (t == "FILE_ERROR" && f.size() >= 3) {
        int clientFileId = (int)ParseLL(f[1]);
        auto mit = g_fileIdMap.find(std::make_pair(from.id, clientFileId));
        if (mit != g_fileIdMap.end()) {
            int serverFileId = mit->second;
            if (g_fileRecv.count(serverFileId)) {
                AbortReceiveFile(serverFileId, f.size() > 2 ? f[2] : "对方取消");
            } else if (g_fileRelayTarget.count(serverFileId)) {
                ServerSendToClient(g_fileRelayTarget[serverFileId], "FILE_ERROR|" + std::to_string(serverFileId) + "|" + (f.size() > 2 ? f[2] : "") + "\n");
                g_fileRelayTarget.erase(serverFileId);
            }
            g_fileIdMap.erase(mit);
            g_fileReverse.erase(serverFileId);
        }
    }
}

// ==================== 客户端逻辑 ====================
void ShutdownClient(); // 前向声明

void ClientHandleLine(const std::string& line) {
    LogEvent("RECV " + line);
    std::vector<std::string> f = SplitFields(line, 7);
    if (f.empty()) return;
    const std::string& t = f[0];

    if (t == "AUTH_REQ") {
        // 服务器要求密码: 发送 AUTH(明文)
        g_clientSendBuf += "AUTH|" + g_clientPassword + "\n";
    } else if (t == "AUTH_FAIL") {
        AddMessageSimple(0, "系统", "密码验证失败：" + (f.size() > 1 ? f[1] : ""));
        ShutdownClient();
        g_mode = AppMode::Select;
        SetWindowTitleC(L"选择模式", "Select Mode");
    } else if (t == "AUTH_OK") {
        g_clientXorEnabled = true;        // 入站解密即时启用
        g_clientPendingXorEnable = true;  // 出站加密待队列清空后启用
        AddMessageSimple(0, "系统", "验证通过，已启用加密");
    } else if (t == "WELCOME" && f.size() >= 3) {
        g_myId = (int)ParseLL(f[1]);
        g_myName = f[2];
        AddMessageSimple(0, "系统", "已连接到服务器，你是 " + g_myName);
    } else if (t == "ROSTER" && f.size() >= 2) {
        g_roster.clear();
        std::string body = f[1];
        size_t pos = 0;
        while (!body.empty() && pos <= body.size()) {
            size_t comma = body.find(',', pos);
            std::string entry = body.substr(pos, comma == std::string::npos ? std::string::npos : comma - pos);
            size_t colon = entry.find(':');
            if (colon != std::string::npos) {
                g_roster[(int)ParseLL(entry.substr(0, colon))] = entry.substr(colon + 1);
            }
            if (comma == std::string::npos) break;
            pos = comma + 1;
        }
        LogEvent("ROSTER_APPLIED " + f[1]);
    } else if (t == "JOIN" && f.size() >= 3) {
        int id = (int)ParseLL(f[1]);
        g_roster[id] = f[2];
        AddMessageSimple(0, "系统", f[2] + " 已加入");
    } else if (t == "LEAVE" && f.size() >= 3) {
        int id = (int)ParseLL(f[1]);
        auto it = g_roster.find(id);
        AddMessageSimple(0, "系统", (it != g_roster.end() ? it->second : f[2]) + " 已离开");
        g_roster.erase(id);
    } else if (t == "RENAME" && f.size() >= 4) {
        int id = (int)ParseLL(f[1]);
        std::string oldName = f[2], newName = f[3];
        g_roster[id] = newName;
        AddMessageSimple(0, "系统", oldName + " 改名为 " + newName);
    } else if (t == "ROOM_OK" && f.size() >= 2) {
        g_room = f[1];
        AddMessageSimple(0, "系统", "已进入房间：" + g_room);
    } else if (t == "PING") {
        g_clientSendBuf += "PONG|" + (f.size() > 1 ? f[1] : "0") + "\n";
    } else if (t == "KICK") {
        AddMessageSimple(0, "系统", "你已被服务器踢出：" + (f.size() > 1 ? f[1] : ""));
        ShutdownClient();
        g_mode = AppMode::Select;
        SetWindowTitleC(L"选择模式", "Select Mode");
    } else if (t == "MUTED") {
        AddMessageSimple(0, "系统", f.size() > 1 && f[1] == "1" ? "你已被服务器禁言" : "你已被解除禁言");
    } else if (t == "MSG" && f.size() >= 7) {
        long long msgId = ParseLL(f[1]);
        int senderId = (int)ParseLL(f[2]);
        std::string senderName = f[3];
        std::string room = f[4];
        std::string quote, text;
        Base64Decode(f[5], quote);
        Base64Decode(f[6], text);
        AddMessageSimple(senderId, senderName, text, quote, room, msgId);
        NotifyNewMessage();
    } else if (t == "PMSG" && f.size() >= 6) {
        long long msgId = ParseLL(f[1]);
        int senderId = (int)ParseLL(f[2]);
        std::string senderName = f[3];
        std::string quote, text;
        Base64Decode(f[4], quote);
        Base64Decode(f[5], text);
        AddMessageSimple(senderId, senderName, text, quote, "", msgId);
        NotifyNewMessage();
    } else if (t == "MSG_ACK" && f.size() >= 2) {
        long long msgId = ParseLL(f[1]);
        std::lock_guard<std::mutex> lock(messagesMutex);
        for (auto it = chatMessages.rbegin(); it != chatMessages.rend(); ++it) {
            if (it->pendingAck) {
                it->msgId = msgId;
                it->pendingAck = false;
                break;
            }
        }
        LogEvent("ACK " + std::to_string(msgId));
    } else if (t == "RECALL" && f.size() >= 2) {
        RemoveMessageById(ParseLL(f[1]));
    } else if (t == "FILE_OFFER" && f.size() >= 6) {
        int fileId = (int)ParseLL(f[5]);
        long long resumeOffset = 0;
        if (PrepareReceiveFile(fileId, f[3], ParseLL(f[4]), f[2], resumeOffset)) {
            g_clientSendBuf += "FILE_RESUME|" + std::to_string(fileId) + "|" + std::to_string(resumeOffset) + "\n";
        }
    } else if (t == "FILE_RESUME" && f.size() >= 3) {
        int fileId = (int)ParseLL(f[1]);
        long long offset = ParseLL(f[2]);
        for (auto& fs : g_fileSends) {
            if (fs.active && fs.fileId == fileId) {
                fs.sent = offset;
                fs.phase = offset > 0 ? SendPhase::HashingPrefix : SendPhase::Streaming;
                fs.prefixRemaining = offset;
                FileSeek(fs.f, 0);
                AddMessageSimple(0, "系统", "文件 " + fs.name + " 断点续传，从 " + SizeStr(offset) + " 继续");
                break;
            }
        }
    } else if (t == "FILE_DATA" && f.size() >= 3) {
        ReceiveFileChunk((int)ParseLL(f[1]), f[2]);
    } else if (t == "FILE_DONE" && f.size() >= 4) {
        FinishReceiveFile((int)ParseLL(f[1]), ParseLL(f[2]), f[3]);
    } else if (t == "FILE_CANCEL" && f.size() >= 3) {
        int fileId = (int)ParseLL(f[1]);
        std::string reason = f.size() > 2 ? f[2] : "对方取消";
        // 取消我方发送
        for (auto& fs : g_fileSends) {
            if (fs.active && fs.fileId == fileId) {
                fclose(fs.f);
                fs.active = false;
                AddMessageSimple(0, "系统", "文件发送已取消：" + fs.name + "（" + reason + "）");
                break;
            }
        }
        // 取消我方接收
        if (g_fileRecv.count(fileId)) {
            AbortReceiveFile(fileId, reason);
        }
    } else if (t == "FILE_ERROR" && f.size() >= 3) {
        int fileId = (int)ParseLL(f[1]);
        std::string msg = f.size() > 2 ? f[2] : "对方取消";
        for (auto& fs : g_fileSends) {
            if (fs.active && fs.fileId == fileId) {
                fclose(fs.f);
                fs.active = false;
                AddMessageSimple(0, "系统", "文件发送失败：" + fs.name + "（" + msg + "）");
                break;
            }
        }
        if (g_fileRecv.count(fileId)) {
            AbortReceiveFile(fileId, msg);
        }
    }
}

// ==================== 文件发送队列 ====================
int ActiveSendCount() {
    int n = 0;
    for (auto& fs : g_fileSends) if (fs.active) ++n;
    return n;
}

void StartFileSend(const std::wstring& path, int targetId) {
    if (ActiveSendCount() >= MAX_PARALLEL_SENDS) {
        AddMessageSimple(0, "系统", "同时传输的文件数已达上限（" + std::to_string(MAX_PARALLEL_SENDS) + " 个）");
        return;
    }
    FILE* f = OpenFileRead(path);
    if (!f) {
        AddMessageSimple(0, "系统", "无法打开文件：" + WideToUtf8(path));
        return;
    }
    FileSeekEnd(f);
    long long size = FileTell(f);
    FileSeek(f, 0);
    if (size <= 0) {
        fclose(f);
        AddMessageSimple(0, "系统", "文件为空，无法发送");
        return;
    }
    size_t slash = path.find_last_of(L"\\/");
    std::wstring wname = slash == std::wstring::npos ? path : path.substr(slash + 1);
    std::string name = WideToUtf8(wname);
    for (auto& ch : name) {
        if (ch == '|' || ch == '\n' || ch == '\r') ch = '_';
    }
    // 去重: 同一文件同一目标已存在
    for (auto& fs : g_fileSends) {
        if (fs.active && fs.name == name && fs.targetId == targetId && fs.size == size) {
            fclose(f);
            AddMessageSimple(0, "系统", "该文件已在传输队列中：" + name);
            return;
        }
    }
    FileSendState st;
    st.active = true;
    st.fileId = g_nextFileId++;
    st.path = path;
    st.name = name;
    st.size = size;
    st.sent = 0;
    st.f = f;
    st.targetId = targetId;
    st.phase = SendPhase::AwaitingResume;
    st.prefixRemaining = 0;
    st.sha.Init();
    g_fileSends.push_back(st);

    std::string offer;
    if (g_mode == AppMode::Client) {
        offer = "FILE_OFFER|" + std::to_string(targetId) + "|" + name + "|" + std::to_string(size) + "|" + std::to_string(st.fileId) + "\n";
        g_clientSendBuf += offer;
        LogEvent("SEND " + offer);
    } else {
        if (targetId <= 0 || !FindClientById(targetId)) {
            st.active = false;
            fclose(f);
            AddMessageSimple(0, "系统", "请先在右侧选择接收用户");
            return;
        }
        offer = "FILE_OFFER|0|服务器|" + name + "|" + std::to_string(size) + "|" + std::to_string(st.fileId) + "\n";
        ServerSendToClient(targetId, offer);
    }
    AddMessageSimple(0, "系统", "开始发送文件：" + name + "（" + SizeStr(size) + "）");
    LogEvent("FILE_SEND_START " + name + " " + std::to_string(size));
}

void CancelFileSend(int fileId) {
    for (auto& fs : g_fileSends) {
        if (fs.active && fs.fileId == fileId) {
            std::string cancel = "FILE_CANCEL|" + std::to_string(fileId) + "|发送方取消\n";
            if (g_mode == AppMode::Client) {
                g_clientSendBuf += cancel;
                LogEvent("SEND " + cancel);
            } else {
                ServerSendToClient(fs.targetId, "FILE_CANCEL|" + std::to_string(fileId) + "|发送方取消\n");
            }
            fclose(fs.f);
            AddMessageSimple(0, "系统", "已取消发送：" + fs.name);
            fs.active = false;
            return;
        }
    }
}

void CancelFileReceive(int fileId) {
    auto it = g_fileRecv.find(fileId);
    if (it == g_fileRecv.end()) return;
    std::string cancel = "FILE_CANCEL|" + std::to_string(fileId) + "|接收方取消\n";
    if (g_mode == AppMode::Client) {
        g_clientSendBuf += cancel;
    }
    AbortReceiveFile(fileId, "接收方取消");
}

// 每帧泵送所有文件发送
void PumpFileSend() {
    for (auto& fs : g_fileSends) {
        if (!fs.active) continue;
        // 哈希前缀(断点续传)
        if (fs.phase == SendPhase::HashingPrefix) {
            std::vector<unsigned char> buf(256 * 1024);
            size_t want = (size_t)std::min<long long>((long long)buf.size(), fs.prefixRemaining);
            size_t n = fread(buf.data(), 1, want, fs.f);
            if (n > 0) {
                fs.sha.Update(buf.data(), n);
                fs.prefixRemaining -= (long long)n;
            }
            if (fs.prefixRemaining <= 0) {
                FileSeek(fs.f, fs.sent);
                fs.phase = SendPhase::Streaming;
            }
            continue;
        }
        if (fs.phase != SendPhase::Streaming) continue;
        int chunks = 0;
        while (fs.active && fs.phase == SendPhase::Streaming && chunks < FILE_CHUNKS_PER_FRAME) {
            std::vector<unsigned char> raw(FILE_CHUNK_RAW);
            size_t n = fread(raw.data(), 1, raw.size(), fs.f);
            if (n > 0) {
                fs.sha.Update(raw.data(), n);
                std::string b64 = Base64Encode(raw.data(), n);
                std::string line = "FILE_DATA|" + std::to_string(fs.fileId) + "|" + b64 + "\n";
                if (g_mode == AppMode::Client) {
                    g_clientSendBuf += line;
                } else {
                    ServerSendToClient(fs.targetId, line);
                }
                fs.sent += (long long)n;
                fs.speed.Update(fs.sent);
                ++chunks;
            }
            if (n < raw.size()) {
                unsigned char digest[32];
                fs.sha.Final(digest);
                std::string done = "FILE_DONE|" + std::to_string(fs.fileId) + "|" + std::to_string(fs.sent) + "|" + Sha256::Hex(digest) + "\n";
                if (g_mode == AppMode::Client) {
                    g_clientSendBuf += done;
                    LogEvent("SEND " + done);
                } else {
                    ServerSendToClient(fs.targetId, done);
                }
                fclose(fs.f);
                AddMessageSimple(0, "系统", "文件发送完成：" + fs.name + "（" + SizeStr(fs.sent) + "）");
                LogEvent("FILE_SEND_DONE " + fs.name + " " + std::to_string(fs.sent));
                fs.active = false;
                break;
            }
        }
    }
}

// 文件选择对话框
bool PickFile(std::wstring& outPath) {
#ifdef _WIN32
    wchar_t buf[1024] = {0};
    OPENFILENAME_MINE ofn;
    memset(&ofn, 0, sizeof(ofn));
    ofn.lStructSize = sizeof(ofn);
    ofn.hwndOwner = (HWND)GetWindowHandle();
    ofn.lpstrFilter = L"所有文件\0*.*\0\0";
    ofn.lpstrFile = buf;
    ofn.nMaxFile = 1024;
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY;
    ofn.lpstrTitle = L"选择要发送的文件";
    if (GetOpenFileNameW(&ofn)) {
        outPath = buf;
        return true;
    }
    return false;
#else
    (void)outPath;
    return false;
#endif
}

// ==================== 服务器/客户端运行逻辑 ====================
bool StartServer() {
    // 从默认端口开始尝试; 被占用时自动向后找可用端口
    for (int port = DEFAULT_PORT; port < DEFAULT_PORT + PORT_FALLBACK_RANGE; ++port) {
        SocketType s = socket(AF_INET6, SOCK_STREAM, 0);
        if (s == INVALID_SOCKET) return false;
        int opt = 1;
#ifdef _WIN32
        // 独占端口: 若端口已被其他进程监听则绑定失败, 避免连接被“抢走”
        setsockopt(s, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char*)&opt, sizeof(opt));
#else
        setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (const char*)&opt, sizeof(opt));
#endif
        int no = 0;
        setsockopt(s, IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&no, sizeof(no));
        sockaddr_in6 serverAddr;
        memset(&serverAddr, 0, sizeof(serverAddr));
        serverAddr.sin6_family = AF_INET6;
        serverAddr.sin6_addr = in6addr_any;
        serverAddr.sin6_port = htons((unsigned short)port);
        if (bind(s, (sockaddr*)&serverAddr, sizeof(serverAddr)) == SOCKET_ERROR_CODE) {
            CLOSE_SOCKET(s);
            continue; // 端口被占用, 尝试下一个
        }
        if (listen(s, MAX_CLIENTS) == SOCKET_ERROR_CODE) {
            CLOSE_SOCKET(s);
            continue;
        }
        listenSock = s;
        g_serverPort = port;
        SetNonBlocking(listenSock);
        return true;
    }
    return false;
}

// 解析 "host" / "host:port" / "[ipv6]:port", 返回主机名与端口
bool ParseHostPort(const std::string& input, std::string& host, int& port) {
    host = input;
    port = DEFAULT_PORT;
    // 去除首尾空格
    size_t b = host.find_first_not_of(" \t");
    if (b == std::string::npos) return false;
    size_t e2 = host.find_last_not_of(" \t");
    host = host.substr(b, e2 - b + 1);
    if (host.empty()) return false;
    if (host.front() == '[') { // [ipv6]:port
        size_t close = host.find(']');
        if (close == std::string::npos) return false;
        std::string addr = host.substr(1, close - 1);
        std::string rest = host.substr(close + 1);
        if (!rest.empty()) {
            if (rest[0] != ':') return false;
            std::string ps = rest.substr(1);
            if (ps.find_first_not_of("0123456789") != std::string::npos) return false;
            int p = std::atoi(ps.c_str());
            if (p < 1 || p > 65535) return false;
            port = p;
        }
        if (addr.empty()) return false;
        host = addr;
        return true;
    }
    size_t colon = host.rfind(':');
    if (colon != std::string::npos && host.find(':') == colon) {
        // 只有一个冒号: host:port 形式
        std::string ps = host.substr(colon + 1);
        if (!ps.empty()) {
            if (ps.find_first_not_of("0123456789") != std::string::npos) return false;
            int p = std::atoi(ps.c_str());
            if (p < 1 || p > 65535) return false;
            port = p;
        }
        host = host.substr(0, colon);
    }
    return !host.empty();
}

bool ConnectClient(const std::string& host, int port) {
    addrinfo hints;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* res = nullptr;
    std::string portStr = std::to_string(port);
    if (getaddrinfo(host.c_str(), portStr.c_str(), &hints, &res) != 0) {
        return false;
    }
    SocketType sock = INVALID_SOCKET;
    for (addrinfo* ai = res; ai; ai = ai->ai_next) {
        sock = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (sock == INVALID_SOCKET) continue;
        if (connect(sock, ai->ai_addr, (int)ai->ai_addrlen) == 0) break;
        CLOSE_SOCKET(sock);
        sock = INVALID_SOCKET;
    }
    freeaddrinfo(res);
    if (sock == INVALID_SOCKET) return false;
    clientSock = sock;
    SetNonBlocking(clientSock);
    g_lastRecvAt = GetTime();
    return true;
}

void ShutdownServer() {
    for (auto& c : g_clients) CLOSE_SOCKET(c.sock);
    g_clients.clear();
    if (listenSock != INVALID_SOCKET) CLOSE_SOCKET(listenSock);
    listenSock = INVALID_SOCKET;
}

void ShutdownClient() {
    if (clientSock != INVALID_SOCKET) CLOSE_SOCKET(clientSock);
    clientSock = INVALID_SOCKET;
    g_roster.clear();
    g_myId = 0;
    g_myName.clear();
    g_clientRecvBuf.clear();
    g_clientSendBuf.clear();
    g_clientXorEnabled = false;
    g_clientPendingXorEnable = false;
    for (auto& fs : g_fileSends) {
        if (fs.active && fs.f) { fclose(fs.f); fs.active = false; }
    }
    for (auto& kv : g_fileRecv) {
        if (kv.second.f) fclose(kv.second.f);
    }
    g_fileRecv.clear();
}

bool IsHostChar(uint32_t cp) {
    return (cp >= '0' && cp <= '9') || (cp >= 'a' && cp <= 'z') || (cp >= 'A' && cp <= 'Z')
        || cp == '.' || cp == ':' || cp == '-' || cp == '_' || cp == '~';
}

// ---- 模式切换与发送消息(UI 与自动化测试共用) ----
void DoStartServer() {
    ClearMessages();
    serverInput.clear();
    g_serverTargetId = 0;
    g_serverPassword = passwordInput;
    g_serverPort = DEFAULT_PORT;
    if (StartServer()) {
        g_mode = AppMode::Server;
        std::wstring serverTitle = L"聊天服务器 : " + std::to_wstring(g_serverPort);
        SetWindowTitleC(serverTitle.c_str(), "Chat Server");
        if (g_serverPort != DEFAULT_PORT) {
            AddMessageSimple(0, "系统", "默认端口 " + std::to_string(DEFAULT_PORT) + " 被占用，已改用端口 " + std::to_string(g_serverPort));
        }
        AddMessageSimple(0, "系统", "服务器已启动，监听端口 " + std::to_string(g_serverPort) + "（IPv4/IPv6 双栈）");
        if (!g_serverPassword.empty()) {
            AddMessageSimple(0, "系统", "已设置访问密码，通信将加密");
        }
    } else {
        AddMessageSimple(0, "系统", "启动服务器失败：端口 " + std::to_string(DEFAULT_PORT) + "~" +
            std::to_string(DEFAULT_PORT + PORT_FALLBACK_RANGE - 1) + " 均已被占用，请关闭占用程序后重试");
    }
}

void DoStartClient() {
    ClearMessages();
    clientInput.clear();
    g_clientTargetId = 0;
    g_clientPassword = passwordInput;
    g_room = "大厅";
    std::string host;
    int port = DEFAULT_PORT;
    if (!ParseHostPort(ipInput, host, port)) {
        AddMessageSimple(0, "系统", "服务器地址格式不正确，请使用 IP 或 IP:端口");
        return;
    }
    g_clientHost = host;
    g_clientPort = port;
    if (ConnectClient(g_clientHost, g_clientPort)) {
        g_mode = AppMode::Client;
        SetWindowTitleC(L"聊天客户端", "Chat Client");
        AddMessageSimple(0, "系统", "正在连接服务器 " + g_clientHost + ":" + std::to_string(g_clientPort) + " ...");
        // 立即发送昵称与 AUTH(明文阶段)
        if (!nameInput.empty()) {
            std::string nick = SanitizeName(nameInput);
            if (!nick.empty()) {
                g_clientSendBuf += "NAME|" + nick + "\n";
                LogEvent("SEND NAME|" + nick);
            }
        }
        if (!g_clientPassword.empty()) {
            g_clientSendBuf += "AUTH|" + g_clientPassword + "\n";
        }
    } else {
        AddMessageSimple(0, "系统", "无法连接到服务器 " + g_clientHost + ":" + std::to_string(g_clientPort) + "，请检查地址和端口");
    }
}

void DoSendMessage(const std::string& text) {
    std::string quote = g_reply.active ? g_reply.text : "";
    std::string bq = Base64Encode((const unsigned char*)quote.data(), quote.size());
    std::string bt = Base64Encode((const unsigned char*)text.data(), text.size());
    if (g_mode == AppMode::Server) {
        if (g_serverTargetId == 0) {
            long long msgId = g_serverMsgId++;
            AddMessageSimple(0, "服务器", text, quote, "大厅", msgId);
            ServerBroadcastRoom("大厅", "MSG|" + std::to_string(msgId) + "|0|服务器|大厅|" + bq + "|" + bt + "\n", INVALID_SOCKET);
            g_msgRoutes[msgId] = RecallRoute{ 0, 0, "大厅" };
        } else {
            long long msgId = g_serverMsgId++;
            AddMessageSimple(0, "服务器 → " + TargetLabel(g_serverTargetId), text, quote, "", msgId);
            ServerSendToClient(g_serverTargetId, "PMSG|" + std::to_string(msgId) + "|0|服务器|" + bq + "|" + bt + "\n");
            g_msgRoutes[msgId] = RecallRoute{ 0, g_serverTargetId, "大厅" };
        }
    } else if (g_mode == AppMode::Client) {
        std::string displayName = "你";
        if (g_clientTargetId == 0) {
            AddMessageSimple(g_myId, displayName, text, quote, g_room, 0, true);
            g_clientSendBuf += "MSG|" + g_room + "|" + bq + "|" + bt + "\n";
            LogEvent("SEND MSG|" + g_room + "|" + text);
        } else {
            AddMessageSimple(g_myId, displayName + " → " + TargetLabel(g_clientTargetId), text, quote, "", 0, true);
            g_clientSendBuf += "PMSG|" + std::to_string(g_clientTargetId) + "|" + bq + "|" + bt + "\n";
            LogEvent("SEND PMSG|" + std::to_string(g_clientTargetId) + "|" + text);
        }
    }
    g_reply.active = false;
}

// 撤回自己最近一条已确认消息
void DoRecallLast() {
    if (g_mode != AppMode::Client) return;
    long long lastId = 0;
    {
        std::lock_guard<std::mutex> lock(messagesMutex);
        for (auto it = chatMessages.rbegin(); it != chatMessages.rend(); ++it) {
            if (it->senderId == g_myId && it->msgId != 0 && !it->pendingAck) {
                lastId = it->msgId;
                break;
            }
        }
    }
    if (lastId != 0) {
        g_clientSendBuf += "RECALL|" + std::to_string(lastId) + "\n";
        LogEvent("SEND RECALL|" + std::to_string(lastId));
    }
}

// ==================== 在线用户面板 ====================
constexpr float PANEL_W = 200;

float PanelX(int screenWidth) { return (float)screenWidth - PANEL_W - 10; }
float PanelEntryH() { return (float)(g_fontSize + 4); }

// 房间输入框的 y(标题/自己/房间标签按字号动态排布, 避免重叠)
float RoomBoxY() { return 3.0f * g_fontSize + 18; }

// 面板条目起始 y
float PanelEntriesStartY() {
    if (g_mode == AppMode::Client) {
        return RoomBoxY() + (g_fontSize + 6) + 8;
    }
    return RoomBoxY();
}

std::vector<std::pair<int, std::string>> PanelEntries() {
    std::vector<std::pair<int, std::string>> entries;
    entries.push_back(std::make_pair(0, std::string("【广播】")));
    if (g_mode == AppMode::Server) {
        for (const auto& c : g_clients) entries.push_back(std::make_pair(c.id, c.name));
    } else {
        for (const auto& kv : g_roster) entries.push_back(std::make_pair(kv.first, kv.second));
    }
    return entries;
}

void DrawUserPanel(int screenWidth, int screenHeight, int selectedId, const std::string& selfLabel) {
    float px = PanelX(screenWidth);
    // 头部按字号动态排布(标题/自己/房间标签/房间框/条目互不重叠)
    float py = 10;
    DrawTextC("在线用户", px, py, g_fontSize, g_theme.dim);
    py += g_fontSize + 4;
    if (!selfLabel.empty()) {
        std::string sl = TruncateToWidth(selfLabel, g_fontSize - 4, PANEL_W - 20);
        DrawTextC(sl.c_str(), px, py, g_fontSize - 4, g_theme.dim);
    }
    py += (g_fontSize - 4) + 6;
    std::string roomLabel = "房间：" + (g_mode == AppMode::Server ? "大厅" : g_room);
    roomLabel = TruncateToWidth(roomLabel, g_fontSize - 4, PANEL_W - 20);
    DrawTextC(roomLabel.c_str(), px, py, g_fontSize - 4, g_theme.dim);
    py += (g_fontSize - 4) + 6;
    if (g_mode == AppMode::Client) {
        float roomH = (float)(g_fontSize + 6);
        Rectangle roomBox = Rectangle{ px, py, PANEL_W - 10, roomH };
        DrawRectangleRec(roomBox, g_theme.panel);
        DrawRectangleLines((int)roomBox.x, (int)roomBox.y, (int)roomBox.width, (int)roomBox.height,
                           g_roomFocus ? g_theme.dim : g_theme.sep);
        DrawFieldText(g_roomInput, g_roomCaret, roomBox.x + 4, roomBox.y + 3, g_theme.text, g_roomFocus);
        py += roomH + 8;
    }
    auto entries = PanelEntries();
    float entryH = PanelEntryH();
    float y = py;
    for (const auto& e : entries) {
        Rectangle r = Rectangle{ px, y, PANEL_W - 10, entryH };
        if (e.first == selectedId) {
            DrawRectangleRec(r, g_theme.hl);
        } else {
            DrawRectangleRec(r, g_theme.panel);
        }
        // 用户颜色块
        if (e.first != 0) {
            DrawRectangle((int)px + 6, (int)y + (int)(entryH / 2 - 6), 12, 12, UserColor(e.first));
        }
        // 名称按面板宽度截断, 防止溢出
        std::string nm = TruncateToWidth(e.second, g_fontSize - 2, PANEL_W - 10 - 34);
        DrawTextC(nm.c_str(), px + 24, y + 4, g_fontSize - 2, e.first == selectedId ? g_theme.text : g_theme.dim);
        y += entryH + 2;
    }
    // 服务器管理按钮
    if (g_mode == AppMode::Server && selectedId > 0 && FindClientById(selectedId)) {
        ClientInfo* c = FindClientById(selectedId);
        float kh = (float)(g_fontSize + 6);
        Rectangle kickBtn = Rectangle{ px, y + 2, (PANEL_W - 10) / 2 - 3, kh };
        Rectangle muteBtn = Rectangle{ px + (PANEL_W - 10) / 2 + 3, y + 2, (PANEL_W - 10) / 2 - 3, kh };
        DrawRectangleRec(kickBtn, Color{ 200, 70, 70, 255 });
        DrawTextCenteredInRect("踢出", kickBtn, g_fontSize - 2, WHITE);
        DrawRectangleRec(muteBtn, c->muted ? Color{ 60, 140, 60, 255 } : Color{ 180, 140, 40, 255 });
        DrawTextCenteredInRect(c->muted ? "解禁" : "禁言", muteBtn, g_fontSize - 2, WHITE);
    }
    (void)screenHeight;
}

int PanelHitTest(int screenWidth, Vector2 mousePos) {
    float px = PanelX(screenWidth);
    auto entries = PanelEntries();
    float entryH = PanelEntryH();
    float y = PanelEntriesStartY();
    for (const auto& e : entries) {
        Rectangle r = Rectangle{ px, y, PANEL_W - 10, entryH };
        if (CheckCollisionPointRec(mousePos, r)) return e.first;
        y += entryH + 2;
    }
    return -1;
}

// ==================== 进度条 ====================
void DrawProgressBar(float x, float y, float w, float h, float frac, const std::string& label,
                     bool showCancel, int cancelFileId, int cancelKind) {
    if (frac < 0) frac = 0;
    if (frac > 1) frac = 1;
    DrawRectangleRec(Rectangle{ x, y, w, h }, g_theme.panel);
    DrawRectangleRec(Rectangle{ x, y, w * frac, h }, g_theme.hl);
    DrawRectangleLines((int)x, (int)y, (int)w, (int)h, g_theme.sep);
    DrawTextC(label.c_str(), x + 4, y + 1, 16, g_theme.text);
    if (showCancel) {
        Rectangle c = Rectangle{ x + w - 22, y + 2, 20, 16 };
        DrawRectangleRec(c, Color{ 200, 70, 70, 255 });
        DrawTextC("x", c.x + 7, c.y - 1, 16, WHITE);
        if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON) && CheckCollisionPointRec(GetMousePosition(), c)) {
            if (cancelKind == 0) CancelFileSend(cancelFileId);
            else CancelFileReceive(cancelFileId);
        }
    }
}

void DrawTransferBars(int screenWidth, float topY) {
    float y = topY;
    float w = PanelX(screenWidth) - 20;
    int shown = 0;
    for (auto& fs : g_fileSends) {
        if (!fs.active) continue;
        if (shown >= 3) break;
        float frac = fs.size > 0 ? (float)fs.sent / (float)fs.size : 0.0f;
        std::string label = "发送中：" + fs.name + " " + std::to_string((int)(frac * 100)) + "% " + fs.speed.SpeedStr();
        DrawProgressBar(10, y, w, 20, frac, label, true, fs.fileId, 0);
        y -= 24;
        ++shown;
    }
    for (auto it = g_fileRecv.rbegin(); it != g_fileRecv.rend() && shown < 6; ++it) {
        float frac = it->second.size > 0 ? (float)it->second.received / (float)it->second.size : 0.0f;
        std::string label = "接收中：" + it->second.name + "（" + it->second.senderLabel + "）" + std::to_string((int)(frac * 100)) + "% " + it->second.speed.SpeedStr();
        DrawProgressBar(10, y, w, 20, frac, label, true, it->second.fileId, 1);
        y -= 24;
        ++shown;
    }
}

// ==================== 滚动消息区 ====================
// 计算输入框行数(当前模式输入)
int CurrentInputLines() {
    const std::string& s = (g_mode == AppMode::Server) ? serverInput : clientInput;
    int n = 1;
    for (char ch : s) if (ch == '\n') ++n;
    return std::min(n, MAX_INPUT_LINES);
}

// ==================== 底部统一布局(消除元素重叠) ====================
// 从屏幕底部往上依次排布: 输入框 -> 提示行(发送到/回复) -> 按钮行 -> 传输进度条 -> 消息区
struct ChatLayout {
    float lineH = 0;         // 文本行高
    float inputH = 0;        // 输入框高度
    float inputTop = 0;      // 输入框顶部 y
    float promptTop = 0;     // 提示行顶部 y
    float buttonY = 0;       // 按钮行顶部 y
    float buttonH = 30;      // 按钮高度(随字号缩放)
    float barsStartY = 0;    // 最下方进度条的 y
    int bars = 0;            // 进度条数量
    float messageBottom = 0; // 消息区底边 y
};

ChatLayout ComputeChatLayout(int screenHeight, bool replyActive) {
    ChatLayout L;
    L.lineH = (float)(g_fontSize + 5);
    int lines = CurrentInputLines();
    L.inputH = lines * L.lineH;
    L.inputTop = (float)screenHeight - 6 - L.inputH;
    int promptLines = 1 + (replyActive ? 1 : 0);
    L.promptTop = L.inputTop - promptLines * 24.0f;
    L.buttonH = (float)(g_fontSize + 10);
    L.buttonY = L.promptTop - L.buttonH - 4;
    int bars = 0;
    for (auto& fs : g_fileSends) if (fs.active) ++bars;
    bars += (int)g_fileRecv.size();
    L.bars = bars;
    L.barsStartY = L.buttonY - 4;
    L.messageBottom = L.barsStartY - bars * 24.0f - 6;
    if (L.messageBottom < 100) L.messageBottom = 100;
    return L;
}

// 绘制消息区(带滚动/搜索过滤/右键菜单/回复)
void DrawChatMessages(int screenWidth, float bottomY, const char* title) {
    int chatW = (int)PanelX(screenWidth) - 20;
    // 消息区顶部 = 标题行 + 搜索行(随字号动态计算, 避免与头部文字重叠)
    float chatTop = 10.0f + g_fontSize + (g_searchActive ? (g_fontSize + 4) : 8.0f);
    float viewH = bottomY - chatTop;
    if (viewH < 20) viewH = 20;
    float lineH = (float)(g_fontSize + 5);

    // 构建可见消息列表(房间过滤 + 搜索过滤), 并预计算按宽度换行
    struct VisMsg {
        const ChatMessage* m;
        std::vector<std::string> qlines;  // 引用行(已换行)
        std::vector<std::string> tlines;  // 正文行(已换行)
        float rowH;
        float textX;
    };
    std::vector<VisMsg> visible;
    {
        std::lock_guard<std::mutex> lock(messagesMutex);
        for (const auto& m : chatMessages) {
            if (g_mode == AppMode::Client && !m.room.empty() && m.room != g_room) continue;
            if (g_searchActive && !g_searchText.empty()) {
                if (m.text.find(g_searchText) == std::string::npos
                    && m.quote.find(g_searchText) == std::string::npos) continue;
            }
            VisMsg v;
            v.m = &m;
            // 文本起始 x = 头像 + 时间戳 + 名称
            std::string ts = "[" + m.time + "] ";
            std::string nm = m.senderName + "：";
            v.textX = 28.0f + MeasureTextC(ts.c_str(), g_fontSize - 2) + 4
                      + MeasureTextC(nm.c_str(), g_fontSize) + 4;
            float maxW = (float)((int)PanelX(screenWidth) - 10) - v.textX - 10;
            if (maxW < 40) maxW = 40;
            if (!m.quote.empty()) v.qlines = WrapText(m.quote, g_fontSize - 2, maxW - 12);
            v.tlines = WrapText(m.text, g_fontSize, maxW);
            int lines = (int)v.qlines.size() + (int)v.tlines.size();
            if (lines < 1) lines = 1;
            v.rowH = lines * lineH + 2;
            visible.push_back(v);
        }
    }
    float totalH = 0;
    for (const auto& v : visible) totalH += v.rowH;
    float maxScroll = std::max(0.0f, totalH - viewH);
    g_chatScrollPx = std::max(0, std::min(g_chatScrollPx, (int)maxScroll));

    // 绘制
    g_msgRows.clear();
    float y = chatTop - g_chatScrollPx;
    for (const auto& v : visible) {
        const ChatMessage& m = *v.m;
        float h = v.rowH;
        if (y + h < chatTop || y > bottomY) { y += h; continue; }
        float cy = y;
        // 头像色块 + 时间戳 + 名称
        Color uc = UserColor(m.senderId);
        DrawRectangle(12, (int)cy + 5, 10, 10, uc);
        float cx = 28.0f;
        std::string ts = "[" + m.time + "] ";
        DrawTextC(ts.c_str(), cx, cy, g_fontSize - 2, g_theme.dim);
        cx += MeasureTextC(ts.c_str(), g_fontSize - 2) + 4;
        std::string nm = m.senderName + "：";
        DrawTextC(nm.c_str(), cx, cy, g_fontSize, uc);
        float ty = cy;
        for (const auto& ql : v.qlines) {
            DrawTextC(ql.c_str(), v.textX + 12, ty, g_fontSize - 2, g_theme.dim);
            ty += lineH;
        }
        for (const auto& tl : v.tlines) {
            DrawTextC(tl.c_str(), v.textX, ty, g_fontSize, g_theme.text);
            ty += lineH;
        }
        g_msgRows.push_back(MsgRow{ m.msgId, m.senderId, cy, h });
        y += h;
    }

    // 滚动条
    if (totalH > viewH) {
        int trackX = 10 + chatW - 12;
        int trackW = 10;
        float thumbH = std::max(20.0f, viewH * viewH / std::max(1.0f, totalH));
        float thumbY = chatTop + (maxScroll > 0 ? (viewH - thumbH) * g_chatScrollPx / maxScroll : 0);
        DrawRectangle(trackX, (int)chatTop, trackW, (int)viewH, g_theme.panel);
        DrawRectangle(trackX, (int)thumbY, trackW, (int)thumbH, g_theme.sep);
        Vector2 m = GetMousePosition();
        Rectangle track = Rectangle{ (float)trackX, chatTop, (float)trackW, viewH };
        if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON) && CheckCollisionPointRec(m, track)) {
            g_scrollDragging = true;
        }
        if (g_scrollDragging) {
            if (IsMouseButtonReleased(MOUSE_LEFT_BUTTON)) {
                g_scrollDragging = false;
            } else if (maxScroll > 0) {
                float frac = (m.y - chatTop - thumbH / 2.0f) / (viewH - thumbH);
                g_chatScrollPx = std::max(0, std::min((int)maxScroll, (int)llroundf(frac * maxScroll)));
            }
        }
    }
    // 滚轮
    int wheel = (int)GetMouseWheelMove();
    Vector2 m = GetMousePosition();
    if (wheel != 0 && m.x >= 10 && m.x <= 10 + chatW && m.y >= chatTop && m.y <= bottomY) {
        g_chatScrollPx = std::max(0, std::min((int)maxScroll, g_chatScrollPx - wheel * 60));
    }
    if (IsKeyPressed(KEY_PAGE_UP)) g_chatScrollPx = std::max(0, std::min((int)maxScroll, g_chatScrollPx + (int)viewH - 40));
    if (IsKeyPressed(KEY_PAGE_DOWN)) g_chatScrollPx = std::max(0, std::min((int)maxScroll, g_chatScrollPx - (int)viewH + 40));

    DrawTextC(title, 10, 10, g_fontSize, g_theme.dim);
    if (g_searchActive) {
        std::string st = "搜索: " + g_searchText + "（" + std::to_string(visible.size()) + " 条匹配）";
        DrawTextC(st.c_str(), 10, 10 + g_fontSize + 2, g_fontSize - 4, g_theme.dim);
    }
    // FPS: 空间不足(会与标题重叠)时自动隐藏
    std::string fps = "FPS: " + std::to_string(GetFPS());
    float titleW = (float)MeasureTextC(title, g_fontSize);
    float fpsW = (float)MeasureTextC(fps.c_str(), 16);
    if (10 + titleW + 20 + fpsW < PanelX(screenWidth) - 10) {
        DrawTextC(fps.c_str(), PanelX(screenWidth) - 10 - fpsW, 10, 16, g_theme.dim);
    }
}

// ==================== 右键菜单 ====================
void DrawContextMenu() {
    if (!g_ctx.open) return;
    float w = 130, h = (float)(g_fontSize + 4);
    float x = std::min(g_ctx.x, (float)GetScreenWidth() - w - 4);
    float y = std::min(g_ctx.y, (float)GetScreenHeight() - h * 3 - 4);
    std::vector<std::string> items = { "复制", "回复" };
    bool own = (g_ctx.senderId == g_myId && g_ctx.msgId != 0);
    if (own) items.push_back("撤回");
    float mh = h * items.size();
    DrawRectangleRec(Rectangle{ x, y, w, mh }, g_theme.panel);
    DrawRectangleLines((int)x, (int)y, (int)w, (int)mh, g_theme.sep);
    for (size_t i = 0; i < items.size(); ++i) {
        DrawTextC(items[i].c_str(), x + 8, y + i * h + 4, g_fontSize - 2, g_theme.text);
    }
    if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
        Vector2 mp = GetMousePosition();
        for (size_t i = 0; i < items.size(); ++i) {
            Rectangle r = Rectangle{ x, y + i * h, w, h };
            if (CheckCollisionPointRec(mp, r)) {
                // 查找消息文本
                std::string copyText;
                {
                    std::lock_guard<std::mutex> lock(messagesMutex);
                    for (const auto& m : chatMessages) {
                        if (m.msgId == g_ctx.msgId) {
                            copyText = m.senderName + "：" + (m.quote.empty() ? m.text : m.quote + "\n" + m.text);
                            if (i == 1) { g_reply.active = true; g_reply.name = m.senderName; g_reply.text = m.text; }
                            break;
                        }
                    }
                }
                if (i == 0) SetClipboardText(copyText);
                if (i == 2) {
                    g_clientSendBuf += "RECALL|" + std::to_string(g_ctx.msgId) + "\n";
                    LogEvent("SEND RECALL|" + std::to_string(g_ctx.msgId));
                }
                g_ctx.open = false;
                break;
            }
        }
    }
    if (IsKeyPressed(KEY_ESCAPE)) g_ctx.open = false;
}

// ==================== 表情面板 ====================
void DrawEmojiPanel(float x, float y) {
    const int cols = 8;
    const float cell = (float)(g_fontSize + 10);
    for (size_t i = 0; i < g_emojiList.size(); ++i) {
        int r = (int)i / cols, c = (int)i % cols;
        float cx = x + c * cell, cy = y + r * cell;
        DrawRectangleRec(Rectangle{ cx, cy, cell - 2, cell - 2 }, g_theme.panel);
        std::string e;
        AppendUtf8(e, g_emojiList[i]);
        DrawTextC(e.c_str(), cx + 5, cy + 4, g_fontSize, g_theme.text);
        if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON) && CheckCollisionPointRec(GetMousePosition(), Rectangle{ cx, cy, cell - 2, cell - 2 })) {
            std::string& inp = (g_mode == AppMode::Server) ? serverInput : clientInput;
            size_t& caret = (g_mode == AppMode::Server) ? g_serverCaret : g_clientCaret;
            InsertUtf8At(inp, caret, g_emojiList[i]);
        }
    }
}

// ==================== 自动化测试钩子 ====================
struct AutoTestState {
    std::vector<std::string> actions;
    size_t index = 0;
    long long nextTimeMs = 0;
} g_autoTest;

long long NowMsAuto() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

void InitAutoTest() {
    const char* a = std::getenv("TCPCHAT_AUTOTEST");
    if (!a || !*a) return;
    std::string s = a;
    size_t pos = 0;
    while (pos <= s.size()) {
        size_t p = s.find(';', pos);
        std::string item = s.substr(pos, p == std::string::npos ? std::string::npos : p - pos);
        if (!item.empty()) g_autoTest.actions.push_back(item);
        if (p == std::string::npos) break;
        pos = p + 1;
    }
    g_autoTest.nextTimeMs = NowMsAuto() + 1500;
}

void ProcessAutoTest() {
    if (g_autoTest.index >= g_autoTest.actions.size()) return;
    if (NowMsAuto() < g_autoTest.nextTimeMs) return;
    std::string a = g_autoTest.actions[g_autoTest.index++];
    int delayMs = 700;
    LogEvent("AUTO " + a);
    if (a == "server") {
        if (g_mode == AppMode::Select) DoStartServer();
    } else if (a == "client") {
        if (g_mode == AppMode::Select) DoStartClient();
    } else if (a.rfind("wait:", 0) == 0) {
        delayMs = (int)(std::atoll(a.c_str() + 5) * 1000);
    } else if (a.rfind("target:", 0) == 0) {
        int t = (int)std::atoll(a.c_str() + 7);
        if (g_mode == AppMode::Server) g_serverTargetId = t;
        else if (g_mode == AppMode::Client) g_clientTargetId = t;
    } else if (a.rfind("room:", 0) == 0) {
        std::string r = a.substr(5);
        if (g_mode == AppMode::Client) {
            g_clientSendBuf += "ROOM|join|" + r + "\n";
        }
    } else if (a.rfind("msg:", 0) == 0) {
        DoSendMessage(a.substr(4));
    } else if (a == "sendfile") {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE");
        if (tf) {
            if (g_mode == AppMode::Client) StartFileSend(AnsiToWide(tf), g_clientTargetId);
            else if (g_mode == AppMode::Server) StartFileSend(AnsiToWide(tf), g_serverTargetId);
        }
    } else if (a == "sendfile2") {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE2");
        if (tf) {
            if (g_mode == AppMode::Client) StartFileSend(AnsiToWide(tf), g_clientTargetId);
            else if (g_mode == AppMode::Server) StartFileSend(AnsiToWide(tf), g_serverTargetId);
        }
    } else if (a == "cancelsend") {
        for (auto& fs : g_fileSends) {
            if (fs.active) { CancelFileSend(fs.fileId); break; }
        }
    } else if (a == "recall") {
        DoRecallLast();
    } else if (a == "quit") {
        g_quitRequested = true;
    }
    g_autoTest.nextTimeMs = NowMsAuto() + delayMs;
}

// ==================== 调试输出 ====================
void WriteDebugDump() {
    const char* dumpPath = std::getenv("TCPCHAT_DEBUG_CODEPOINTS");
    if (dumpPath && *dumpPath) {
        std::ofstream f(dumpPath, std::ios::out | std::ios::trunc);
        if (f.is_open()) {
            for (uint32_t cp : g_debugLog) {
                f << "cp:0x" << std::hex << cp << std::dec << "\n";
            }
            f.close();
        }
    }
    const char* logPath = std::getenv("TCPCHAT_DEBUG_LOG");
    if (logPath && *logPath) {
        std::ofstream f(logPath, std::ios::out | std::ios::trunc);
        if (f.is_open()) {
            for (const auto& e : g_eventLog) {
                f << e << "\n";
            }
            f.close();
        }
    }
}

// ==================== 选择模式 ====================
void UpdateSelectFrame() {
    int screenWidth = GetScreenWidth();
    float margin = 20;
    float titleY = margin;
    float lh = (float)(g_fontSize + 6);
    float nameLabelY = 52;
    float nameBoxY = nameLabelY + lh - 6;
    float pwdLabelY = nameBoxY + 46;
    float pwdBoxY = pwdLabelY + lh - 6;
    float ipLabelY = pwdBoxY + 46;
    float ipBoxY = ipLabelY + lh - 6;
    float buttonY = ipBoxY + 48;

    Rectangle nameBox = Rectangle{ margin, nameBoxY, (float)screenWidth - 2 * margin, lh };
    Rectangle pwdBox = Rectangle{ margin, pwdBoxY, (float)screenWidth - 2 * margin, lh };
    Rectangle ipBox = Rectangle{ margin, ipBoxY, (float)screenWidth - 2 * margin, lh };
    float buttonHeight = (float)(g_fontSize + 16);
    float serverW = (float)MeasureTextC("启动服务器", g_fontSize) + 40;
    float clientW = (float)MeasureTextC("启动客户端", g_fontSize) + 40;
    float gap = 20;
    float startX = (screenWidth - (serverW + clientW + gap)) / 2;
    Rectangle serverBtn = Rectangle{ startX, buttonY, serverW, buttonHeight };
    Rectangle clientBtn = Rectangle{ startX + serverW + gap, buttonY, clientW, buttonHeight };

    std::string& curText = (g_selFocus == SelFocus::Name) ? nameInput : (g_selFocus == SelFocus::Pwd ? passwordInput : ipInput);
    size_t& curCaret = (g_selFocus == SelFocus::Name) ? g_nameCaret : (g_selFocus == SelFocus::Pwd ? g_pwdCaret : g_ipCaret);
    HandleCaretKeys(curText, curCaret);
    for (uint32_t cp : TakeInputCodepoints()) {
        bool ok;
        if (g_selFocus == SelFocus::Name) ok = (cp >= 32 && cp != 127);
        else if (g_selFocus == SelFocus::Pwd) ok = (cp >= 32 && cp != 127);
        else ok = IsHostChar(cp);
        if (ok) InsertUtf8At(curText, curCaret, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(curText, curCaret);
    if (IsKeyPressed(KEY_DELETE)) DelCharAt(curText, curCaret);
    if (IsKeyPressed(KEY_TAB)) {
        if (g_selFocus == SelFocus::Name) g_selFocus = SelFocus::Pwd;
        else if (g_selFocus == SelFocus::Pwd) g_selFocus = SelFocus::Ip;
        else g_selFocus = SelFocus::Name;
    }
    if (IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) {
        if (g_selFocus == SelFocus::Name) { g_selFocus = SelFocus::Pwd; }
        else if (g_selFocus == SelFocus::Pwd) { g_selFocus = SelFocus::Ip; g_ipCaret = ipInput.size(); }
        else { DoStartClient(); return; }
    }

    if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
        Vector2 mousePos = GetMousePosition();
        if (CheckCollisionPointRec(mousePos, serverBtn)) {
            DoStartServer();
            return;
        }
        if (CheckCollisionPointRec(mousePos, clientBtn)) {
            DoStartClient();
            return;
        }
        if (CheckCollisionPointRec(mousePos, nameBox)) {
            g_selFocus = SelFocus::Name;
            g_nameCaret = CaretFromX(nameInput, g_fontSize, mousePos.x, nameBox.x + 5);
        } else if (CheckCollisionPointRec(mousePos, pwdBox)) {
            g_selFocus = SelFocus::Pwd;
            g_pwdCaret = CaretFromX(passwordInput, g_fontSize, mousePos.x, pwdBox.x + 5);
        } else if (CheckCollisionPointRec(mousePos, ipBox)) {
            g_selFocus = SelFocus::Ip;
            g_ipCaret = CaretFromX(ipInput, g_fontSize, mousePos.x, ipBox.x + 5);
        }
    }

    UpdateIBeamCursor({ nameBox, pwdBox, ipBox });

    BeginDrawing();
    ClearBackground(g_theme.bg);
    const char* title = "选择模式";
    int titleWidth = MeasureTextC(title, g_fontSize);
    DrawTextC(title, (screenWidth - titleWidth) / 2.0f, titleY, g_fontSize, g_theme.dim);

    DrawTextC("昵称（留空自动分配）：", margin, nameLabelY, g_fontSize, g_theme.dim);
    DrawRectangleRec(nameBox, g_theme.panel);
    DrawRectangleLines((int)nameBox.x, (int)nameBox.y, (int)nameBox.width, (int)nameBox.height,
                       g_selFocus == SelFocus::Name ? g_theme.dim : g_theme.sep);
    DrawFieldText(nameInput, g_nameCaret, nameBox.x + 5, nameBox.y + 3, g_theme.text, g_selFocus == SelFocus::Name);

    DrawTextC("密码（可选，用于验证与加密，服务器和客户端需一致）：", margin, pwdLabelY, g_fontSize, g_theme.dim);
    DrawRectangleRec(pwdBox, g_theme.panel);
    DrawRectangleLines((int)pwdBox.x, (int)pwdBox.y, (int)pwdBox.width, (int)pwdBox.height,
                       g_selFocus == SelFocus::Pwd ? g_theme.dim : g_theme.sep);
    std::string masked(pwdBox.width > 0 ? passwordInput.size() : 0, '*');
    DrawFieldText(masked, g_pwdCaret, pwdBox.x + 5, pwdBox.y + 3, g_theme.text, g_selFocus == SelFocus::Pwd);

    DrawTextC("服务器地址（可含端口，如 192.168.1.5:45678）：", margin, ipLabelY, g_fontSize, g_theme.dim);
    DrawRectangleRec(ipBox, g_theme.panel);
    DrawRectangleLines((int)ipBox.x, (int)ipBox.y, (int)ipBox.width, (int)ipBox.height,
                       g_selFocus == SelFocus::Ip ? g_theme.dim : g_theme.sep);
    DrawFieldText(ipInput, g_ipCaret, ipBox.x + 5, ipBox.y + 3, g_theme.text, g_selFocus == SelFocus::Ip);

    DrawRectangleRec(serverBtn, SKYBLUE);
    DrawTextCenteredInRect("启动服务器", serverBtn, g_fontSize, BLACK);
    DrawRectangleRec(clientBtn, GREEN);
    DrawTextCenteredInRect("启动客户端", clientBtn, g_fontSize, BLACK);
    EndDrawing();
}

// ==================== 聊天帧公共输入处理 ====================
// 处理聊天输入框(多行/历史/表情/搜索/回复)
void ProcessChatInput(std::string& input, size_t& caret) {
    bool shift = IsKeyDown(KEY_LEFT_SHIFT) || IsKeyDown(KEY_RIGHT_SHIFT);
    bool ctrl = IsKeyDown(KEY_LEFT_CONTROL) || IsKeyDown(KEY_RIGHT_CONTROL);

    if (g_searchActive) {
        HandleCaretKeys(g_searchText, g_searchCaret);
        for (uint32_t cp : TakeInputCodepoints()) {
            if (cp >= 32 && cp != 127) InsertUtf8At(g_searchText, g_searchCaret, cp);
        }
        if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(g_searchText, g_searchCaret);
        if (IsKeyPressed(KEY_DELETE)) DelCharAt(g_searchText, g_searchCaret);
        if (IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_ESCAPE)) g_searchActive = false;
        return;
    }

    if (ctrl && IsKeyPressed(KEY_F)) {
        g_searchActive = true;
        g_searchCaret = g_searchText.size();
        return;
    }
    if (ctrl && IsKeyPressed(KEY_L)) {
        ClearMessages();
        return;
    }
    if (ctrl && IsKeyPressed(KEY_Q)) {
        g_quitRequested = true;
        return;
    }
    if (ctrl && IsKeyPressed(KEY_E)) {
        g_emojiOpen = !g_emojiOpen;
        return;
    }

    HandleCaretKeys(input, caret);
    for (uint32_t cp : TakeInputCodepoints()) {
        if (cp >= 32 && cp != 127) InsertUtf8At(input, caret, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(input, caret);
    if (IsKeyPressed(KEY_DELETE)) DelCharAt(input, caret);

    // 输入历史
    if (IsKeyPressed(KEY_UP)) {
        if (!g_inputHistory.empty()) {
            if (g_historyIdx == -1) {
                g_historyIdx = (int)g_inputHistory.size();
                g_historySaved = input;
            }
            if (g_historyIdx > 0) {
                --g_historyIdx;
                input = g_inputHistory[g_historyIdx];
                caret = input.size();
            }
        }
    } else if (IsKeyPressed(KEY_DOWN)) {
        if (g_historyIdx != -1) {
            ++g_historyIdx;
            if (g_historyIdx >= (int)g_inputHistory.size()) {
                g_historyIdx = -1;
                input = g_historySaved;
            } else {
                input = g_inputHistory[g_historyIdx];
            }
            caret = input.size();
        }
    }

    if (IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) {
        if (shift) {
            // Shift+Enter 换行
            input.insert(caret, "\n");
            caret += 1;
        } else if (!input.empty()) {
            std::string text = input;
            DoSendMessage(text);
            if (!g_inputHistory.empty() && g_inputHistory.back() != text) {
                g_inputHistory.push_back(text);
            } else if (g_inputHistory.empty()) {
                g_inputHistory.push_back(text);
            }
            g_historyIdx = -1;
            input.clear();
            caret = 0;
        }
    }
    if (IsKeyPressed(KEY_ESCAPE)) {
        if (g_emojiOpen) g_emojiOpen = false;
        else if (g_reply.active) g_reply.active = false;
    }
}

// 房间输入处理(客户端)
void ProcessRoomInput() {
    if (!g_roomFocus) return;
    HandleCaretKeys(g_roomInput, g_roomCaret);
    for (uint32_t cp : TakeInputCodepoints()) {
        if (cp >= 32 && cp != 127) InsertUtf8At(g_roomInput, g_roomCaret, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(g_roomInput, g_roomCaret);
    if (IsKeyPressed(KEY_DELETE)) DelCharAt(g_roomInput, g_roomCaret);
    if (IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) {
        std::string r = SanitizeName(g_roomInput);
        if (!r.empty()) {
            g_clientSendBuf += "ROOM|join|" + r + "\n";
        }
        g_roomInput.clear();
        g_roomCaret = 0;
        g_roomFocus = false;
    }
    if (IsKeyPressed(KEY_ESCAPE)) g_roomFocus = false;
}

// ==================== 服务器模式 ====================
void UpdateServerFrame() {
    int screenWidth = GetScreenWidth();
    int screenHeight = GetScreenHeight();

    // 接受新连接
    sockaddr_storage clientAddr;
    socklen_t clientLen = sizeof(clientAddr);
    SocketType newClient = accept(listenSock, (sockaddr*)&clientAddr, &clientLen);
    if (newClient != INVALID_SOCKET) {
        SetNonBlocking(newClient);
        ClientInfo info;
        info.sock = newClient;
        info.id = g_nextClientId++;
        info.name = "用户" + std::to_string(info.id);
        info.lastPong = GetTime();
        info.authed = g_serverPassword.empty();
        g_clients.push_back(info);
        std::string ip = SockAddrToString((const sockaddr*)&clientAddr);
        AddMessageSimple(0, "系统", info.name + " 已加入（" + ip + "）");
        LogEvent("CLIENT_JOIN " + std::to_string(info.id) + " " + ip);
        if (info.authed) {
            ServerSendToClient(info.id, "WELCOME|" + std::to_string(info.id) + "|" + info.name + "\n");
            ServerSendToClient(info.id, RosterLine() + "\n");
            ServerBroadcastOthers("JOIN|" + std::to_string(info.id) + "|" + info.name + "\n", info.sock);
        } else {
            info.authDeadline = GetTime() + 10.0;
            ServerSendToClient(info.id, "AUTH_REQ\n");
        }
    }

    // 发送队列刷出 + XOR 加密
    for (auto& c : g_clients) {
        if (c.xorEnabled && !c.sendBuf.empty()) {
            c.xorSend.Apply((unsigned char*)&c.sendBuf[0], c.sendBuf.size());
        }
        FlushSendQueue(c.sock, c.sendBuf, nullptr);
        if (c.pendingXorEnable && c.sendBuf.empty()) {
            c.pendingXorEnable = false;
            c.xorEnabled = true;
            if (c.pendingWelcome) {
                c.pendingWelcome = false;
                AddMessageSimple(0, "系统", c.name + " 验证通过");
                ServerSendToClient(c.id, "WELCOME|" + std::to_string(c.id) + "|" + c.name + "\n");
                ServerSendToClient(c.id, RosterLine() + "\n");
                ServerBroadcastOthers("JOIN|" + std::to_string(c.id) + "|" + c.name + "\n", c.sock);
            }
        }
    }

    // 心跳
    double nowT = GetTime();
    if (nowT - g_lastPingTime >= PING_INTERVAL) {
        g_lastPingTime = nowT;
        ServerBroadcastAll("PING|" + std::to_string((long long)(nowT * 1000)) + "\n");
        for (size_t i = 0; i < g_clients.size(); ) {
            if (!g_clients[i].authed && nowT > g_clients[i].authDeadline) {
                AddMessageSimple(0, "系统", g_clients[i].name + " 未通过验证，已断开");
                CLOSE_SOCKET(g_clients[i].sock);
                g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
                continue;
            }
            if (g_clients[i].authed && nowT - g_clients[i].lastPong > PONG_TIMEOUT) {
                AddMessageSimple(0, "系统", g_clients[i].name + " 心跳超时，已断开");
                ServerBroadcastOthers("LEAVE|" + std::to_string(g_clients[i].id) + "|" + g_clients[i].name + "\n", g_clients[i].sock);
                ServerBroadcastAll(RosterLine() + "\n");
                CLOSE_SOCKET(g_clients[i].sock);
                g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
                continue;
            }
            ++i;
        }
    }

    // 接收数据(行重组 + XOR 解密)
    for (size_t i = 0; i < g_clients.size(); ) {
        ClientInfo& c = g_clients[i];
        char buffer[BUFFER_SIZE];
        int n = recv(c.sock, buffer, sizeof(buffer), 0);
        if (n > 0) {
            if (c.xorEnabled) {
                c.xorRecv.Apply((unsigned char*)buffer, (size_t)n);
            }
            c.lastPong = GetTime();
            c.recvBuf.append(buffer, (size_t)n);
            if (c.recvBuf.size() > MAX_LINE_LENGTH) c.recvBuf.clear();
            size_t pos;
            while ((pos = c.recvBuf.find('\n')) != std::string::npos) {
                std::string line = c.recvBuf.substr(0, pos);
                c.recvBuf.erase(0, pos + 1);
                TrimLineEnd(line);
                if (!line.empty()) ServerHandleLine(line, c);
                if (c.sock == INVALID_SOCKET) break; // AUTH 失败已关闭
            }
            if (c.sock == INVALID_SOCKET) {
                AddMessageSimple(0, "系统", c.name + " 已离开");
                ServerBroadcastOthers("LEAVE|" + std::to_string(c.id) + "|" + c.name + "\n", INVALID_SOCKET);
                ServerBroadcastAll(RosterLine() + "\n");
                g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
                continue;
            }
            ++i;
        } else if (n == 0) {
            CLOSE_SOCKET(c.sock);
            AddMessageSimple(0, "系统", c.name + " 已离开");
            ServerBroadcastOthers("LEAVE|" + std::to_string(c.id) + "|" + c.name + "\n", c.sock);
            ServerBroadcastAll(RosterLine() + "\n");
            g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
        } else {
#ifdef _WIN32
            int err = WSAGetLastError();
#else
            int err = errno;
#endif
#ifdef _WIN32
            if (err == WSAEWOULDBLOCK) { ++i; }
#else
            if (err == EAGAIN || err == EWOULDBLOCK) { ++i; }
#endif
            else {
                CLOSE_SOCKET(c.sock);
                AddMessageSimple(0, "系统", c.name + " 已离开");
                ServerBroadcastOthers("LEAVE|" + std::to_string(c.id) + "|" + c.name + "\n", c.sock);
                ServerBroadcastAll(RosterLine() + "\n");
                g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
            }
        }
    }

    // 文件发送泵
    PumpFileSend();

    // 输入处理
    ProcessChatInput(serverInput, g_serverCaret);

    // 拖放文件
    if (IsFileDropped()) {
        FilePathList fl = LoadDroppedFiles();
        int sent = 0;
        for (unsigned int i = 0; i < fl.count; ++i) {
            if (g_serverTargetId <= 0) {
                AddMessageSimple(0, "系统", "请先在右侧选择接收用户，再拖放文件");
                break;
            }
            StartFileSend(Utf8ToWide(fl.paths[i]), g_serverTargetId);
            ++sent;
        }
        UnloadDroppedFiles(fl);
        if (sent > 0) AddMessageSimple(0, "系统", "已加入发送队列：" + std::to_string(sent) + " 个文件");
    }

    // 鼠标: 面板 / 按钮 / 输入框 / 右键消息(统一布局, 与绘制区一致)
    {
        ChatLayout L = ComputeChatLayout(screenHeight, g_reply.active);
        float emojiW = (float)MeasureTextC("表情", g_fontSize - 2) + 18;
        float searchW = (float)MeasureTextC("搜索", g_fontSize - 2) + 18;
        float fileW = (float)MeasureTextC("发送文件…", g_fontSize - 2) + 24;
        Rectangle inputBox = Rectangle{ 10, L.inputTop, PanelX(screenWidth) - 20, L.inputH };
        Rectangle emojiBtn = Rectangle{ 10, L.buttonY, emojiW, L.buttonH };
        Rectangle searchBtn = Rectangle{ 12 + emojiW, L.buttonY, searchW, L.buttonH };
        Rectangle fileBtn = Rectangle{ (float)screenWidth - fileW - 10, L.buttonY, fileW, L.buttonH };
        if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
            Vector2 mousePos = GetMousePosition();
            int hit = PanelHitTest(screenWidth, mousePos);
            if (hit >= 0) g_serverTargetId = hit;
            // 输入框点击定位光标
            if (CheckCollisionPointRec(mousePos, inputBox)) {
                g_serverCaret = CaretFromX(serverInput, g_fontSize, mousePos.x, 15);
            }
            // 表情/搜索按钮
            if (CheckCollisionPointRec(mousePos, emojiBtn)) g_emojiOpen = !g_emojiOpen;
            if (CheckCollisionPointRec(mousePos, searchBtn)) { g_searchActive = !g_searchActive; g_searchCaret = g_searchText.size(); }
            // 服务器管理按钮
            if (g_serverTargetId > 0 && FindClientById(g_serverTargetId)) {
                ClientInfo* c = FindClientById(g_serverTargetId);
                float px = PanelX(screenWidth);
                float y = PanelEntriesStartY() + (PanelEntries().size()) * (PanelEntryH() + 2);
                float kh = (float)(g_fontSize + 6);
                Rectangle kickBtn = Rectangle{ px, y + 2, (PANEL_W - 10) / 2 - 3, kh };
                Rectangle muteBtn = Rectangle{ px + (PANEL_W - 10) / 2 + 3, y + 2, (PANEL_W - 10) / 2 - 3, kh };
                if (CheckCollisionPointRec(mousePos, kickBtn)) {
                    ServerSendToClient(c->id, "KICK|已被服务器踢出\n");
                    AddMessageSimple(0, "系统", c->name + " 已被踢出");
                    CLOSE_SOCKET(c->sock);
                    ServerBroadcastOthers("LEAVE|" + std::to_string(c->id) + "|" + c->name + "\n", INVALID_SOCKET);
                    ServerBroadcastAll(RosterLine() + "\n");
                    for (size_t i = 0; i < g_clients.size(); ++i) {
                        if (g_clients[i].id == c->id) { g_clients.erase(g_clients.begin() + (ptrdiff_t)i); break; }
                    }
                    g_serverTargetId = 0;
                } else if (CheckCollisionPointRec(mousePos, muteBtn)) {
                    c->muted = !c->muted;
                    ServerSendToClient(c->id, std::string("MUTED|") + (c->muted ? "1" : "0") + "\n");
                    AddMessageSimple(0, "系统", c->name + (c->muted ? " 已被禁言" : " 已解除禁言"));
                }
            }
            // 发送文件按钮
            if (CheckCollisionPointRec(mousePos, fileBtn)) {
                if (g_serverTargetId <= 0) {
                    AddMessageSimple(0, "系统", "请先在右侧选择接收用户，再发送文件");
                } else {
                    std::wstring path;
                    if (PickFile(path)) StartFileSend(path, g_serverTargetId);
                }
            }
        }
        UpdateIBeamCursor({ inputBox });
    }
    // 右键消息
    if (IsMouseButtonPressed(MOUSE_RIGHT_BUTTON)) {
        Vector2 mp = GetMousePosition();
        for (auto it = g_msgRows.rbegin(); it != g_msgRows.rend(); ++it) {
            if (mp.y >= it->y && mp.y <= it->y + it->h) {
                g_ctx.open = true;
                g_ctx.msgId = it->msgId;
                g_ctx.senderId = it->senderId;
                g_ctx.x = mp.x;
                g_ctx.y = mp.y;
                break;
            }
        }
    }

    // 测试钩子 F8
    if (IsKeyPressed(KEY_F8)) {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE");
        if (tf && g_serverTargetId > 0) StartFileSend(AnsiToWide(tf), g_serverTargetId);
    }

    UpdateIBeamCursor({ Rectangle{ 10, (float)screenHeight - 26 - (CurrentInputLines() - 1) * (g_fontSize + 5), PanelX(screenWidth) - 20, 20 } });

    // 布局与绘制(统一布局: 输入框 -> 提示 -> 按钮 -> 进度条 -> 消息区, 各区域互不重叠)
    ChatLayout L = ComputeChatLayout(screenHeight, g_reply.active);
    float emojiW = (float)MeasureTextC("表情", g_fontSize - 2) + 18;
    float searchW = (float)MeasureTextC("搜索", g_fontSize - 2) + 18;
    float fileW = (float)MeasureTextC("发送文件…", g_fontSize - 2) + 24;
    Rectangle inputBox = Rectangle{ 10, L.inputTop, PanelX(screenWidth) - 20, L.inputH };
    Rectangle emojiBtn = Rectangle{ 10, L.buttonY, emojiW, L.buttonH };
    Rectangle searchBtn = Rectangle{ 12 + emojiW, L.buttonY, searchW, L.buttonH };
    Rectangle fileBtn = Rectangle{ (float)screenWidth - fileW - 10, L.buttonY, fileW, L.buttonH };

    BeginDrawing();
    ClearBackground(g_theme.bg);
    DrawChatMessages(screenWidth, L.messageBottom, "聊天服务器 - 消息记录：");
    DrawUserPanel(screenWidth, screenHeight, g_serverTargetId, "（服务器）");
    DrawTransferBars(screenWidth, L.barsStartY);
    DrawContextMenu();
    if (g_emojiOpen) DrawEmojiPanel(10, L.buttonY - 100);

    // 提示行(从 promptTop 依次向下排)
    float py = L.promptTop;
    if (g_reply.active) {
        std::string rp = "回复 " + g_reply.name + "（Esc 取消）";
        DrawTextC(rp.c_str(), 10, py, g_fontSize - 2, g_theme.dim);
        py += 24;
    }
    DrawTextC(("发送到：" + TargetLabel(g_serverTargetId) + "（回车发送，Shift+Enter 换行）").c_str(), 10, py, g_fontSize - 2, g_theme.dim);

    // 输入框(多行)
    DrawRectangleRec(inputBox, g_theme.panel);
    DrawRectangleLines((int)inputBox.x, (int)inputBox.y, (int)inputBox.width, (int)inputBox.height, g_theme.sep);
    {
        // 逐行绘制输入内容与光标
        size_t pos = 0;
        int li = 0;
        std::string line;
        float ly = L.inputTop + 3;
        while (pos <= serverInput.size() && li < MAX_INPUT_LINES) {
            size_t nl = serverInput.find('\n', pos);
            line = serverInput.substr(pos, nl == std::string::npos ? std::string::npos : nl - pos);
            DrawTextC(line.c_str(), inputBox.x + 5, ly, g_fontSize, g_theme.inputText);
            size_t lineStart = pos;
            size_t lineEnd = nl == std::string::npos ? serverInput.size() : nl;
            if (g_serverCaret >= lineStart && g_serverCaret <= lineEnd) {
                if (fmod(GetTime(), 1.0) < 0.5) {
                    float cx = inputBox.x + 5 + MeasureTextC(serverInput.substr(lineStart, g_serverCaret - lineStart).c_str(), g_fontSize);
                    DrawRectangle((int)cx, (int)ly + 2, 2, g_fontSize - 2, g_theme.dim);
                }
            }
            ly += L.lineH;
            ++li;
            if (nl == std::string::npos) break;
            pos = nl + 1;
        }
    }
    // 按钮
    DrawRectangleRec(emojiBtn, g_theme.hl);
    DrawTextCenteredInRect("表情", emojiBtn, g_fontSize - 2, g_theme.text);
    DrawRectangleRec(searchBtn, g_theme.hl);
    DrawTextCenteredInRect("搜索", searchBtn, g_fontSize - 2, g_theme.text);
    DrawRectangleRec(fileBtn, ORANGE);
    DrawTextCenteredInRect("发送文件…", fileBtn, g_fontSize - 2, BLACK);
    EndDrawing();
}

// ==================== 客户端模式 ====================
void UpdateClientFrame() {
    int screenWidth = GetScreenWidth();
    int screenHeight = GetScreenHeight();

    // 断线重连
    if (clientSock == INVALID_SOCKET) {
        double nowT = GetTime();
        if (nowT >= g_reconnectAt) {
            g_reconnectAt = nowT + RECONNECT_DELAY;
            if (ConnectClient(g_clientHost, g_clientPort)) {
                AddMessageSimple(0, "系统", "已重新连接到服务器 " + g_clientHost + ":" + std::to_string(g_clientPort));
                if (!g_clientPassword.empty()) {
                    g_clientSendBuf += "AUTH|" + g_clientPassword + "\n";
                }
                if (!nameInput.empty()) {
                    std::string nick = SanitizeName(nameInput);
                    if (!nick.empty()) g_clientSendBuf += "NAME|" + nick + "\n";
                }
                if (g_room != "大厅") {
                    g_clientSendBuf += "ROOM|join|" + g_room + "\n";
                }
            }
        }
        // 重连中界面
        BeginDrawing();
        ClearBackground(g_theme.bg);
        DrawChatMessages(screenWidth, (float)screenHeight - 80, "聊天客户端 - 消息记录：");
        DrawTextC(("连接已断开，" + std::to_string(std::max(0.0, g_reconnectAt - nowT)).substr(0, 3) + " 秒后重连...").c_str(), 10, (float)screenHeight - 50, g_fontSize, g_theme.dim);
        EndDrawing();
        if (IsKeyPressed(KEY_ESCAPE) || (IsKeyDown(KEY_LEFT_CONTROL) && IsKeyPressed(KEY_Q))) {
            g_mode = AppMode::Select;
            SetWindowTitleC(L"选择模式", "Select Mode");
        }
        return;
    }

    // 发送队列刷出(XOR)
    if (g_clientXorEnabled && !g_clientSendBuf.empty()) {
        g_clientXorSend.Apply((unsigned char*)&g_clientSendBuf[0], g_clientSendBuf.size());
    }
    FlushSendQueue(clientSock, g_clientSendBuf, nullptr);
    if (g_clientPendingXorEnable && g_clientSendBuf.empty()) {
        g_clientPendingXorEnable = false;
        g_clientXorEnabled = true;
        // 认证后重发身份信息(此后全部加密)
        if (!nameInput.empty()) {
            std::string nick = SanitizeName(nameInput);
            if (!nick.empty()) {
                g_clientSendBuf += "NAME|" + nick + "\n";
                LogEvent("SEND NAME(post-auth)|" + nick);
            }
        }
        if (g_room != "大厅") {
            g_clientSendBuf += "ROOM|join|" + g_room + "\n";
        }
    }

    // 接收(XOR 解密 + 行重组)
    char buffer[BUFFER_SIZE];
    int n = recv(clientSock, buffer, sizeof(buffer), 0);
    if (n > 0) {
        if (g_clientXorEnabled) {
            g_clientXorRecv.Apply((unsigned char*)buffer, (size_t)n);
        }
        g_lastRecvAt = GetTime();
        g_clientRecvBuf.append(buffer, (size_t)n);
        if (g_clientRecvBuf.size() > MAX_LINE_LENGTH) g_clientRecvBuf.clear();
        size_t pos;
        while ((pos = g_clientRecvBuf.find('\n')) != std::string::npos) {
            std::string line = g_clientRecvBuf.substr(0, pos);
            g_clientRecvBuf.erase(0, pos + 1);
            TrimLineEnd(line);
            if (!line.empty()) ClientHandleLine(line);
        }
    } else if (n == 0) {
        AddMessageSimple(0, "系统", "与服务器的连接已断开");
        ShutdownClient();
        g_reconnectAt = GetTime() + RECONNECT_DELAY;
        return;
    } else {
#ifdef _WIN32
        int err = WSAGetLastError();
#else
        int err = errno;
#endif
#ifdef _WIN32
        if (err != WSAEWOULDBLOCK) {
#else
        if (err != EAGAIN && err != EWOULDBLOCK) {
#endif
            AddMessageSimple(0, "系统", "与服务器的连接已断开");
            ShutdownClient();
            g_reconnectAt = GetTime() + RECONNECT_DELAY;
            return;
        }
    }
    // 超时检测
    if (GetTime() - g_lastRecvAt > CLIENT_TIMEOUT) {
        AddMessageSimple(0, "系统", "与服务器的连接超时");
        ShutdownClient();
        g_reconnectAt = GetTime() + RECONNECT_DELAY;
        return;
    }

    // 文件发送泵
    PumpFileSend();

    // 输入处理(房间输入框聚焦时优先)
    if (g_roomFocus) {
        ProcessRoomInput();
    } else {
        ProcessChatInput(clientInput, g_clientCaret);
    }

    // 拖放文件
    if (IsFileDropped()) {
        FilePathList fl = LoadDroppedFiles();
        int sent = 0;
        for (unsigned int i = 0; i < fl.count; ++i) {
            StartFileSend(Utf8ToWide(fl.paths[i]), g_clientTargetId);
            ++sent;
        }
        UnloadDroppedFiles(fl);
        if (sent > 0) AddMessageSimple(0, "系统", "已加入发送队列：" + std::to_string(sent) + " 个文件");
    }

    // 鼠标: 面板/房间/输入框/按钮(统一布局, 与绘制区一致)
    {
        ChatLayout L = ComputeChatLayout(screenHeight, g_reply.active);
        float emojiW = (float)MeasureTextC("表情", g_fontSize - 2) + 18;
        float searchW = (float)MeasureTextC("搜索", g_fontSize - 2) + 18;
        float fileW = (float)MeasureTextC("发送文件…", g_fontSize - 2) + 24;
        Rectangle inputBox = Rectangle{ 10, L.inputTop, PanelX(screenWidth) - 20, L.inputH };
        Rectangle emojiBtn = Rectangle{ 10, L.buttonY, emojiW, L.buttonH };
        Rectangle searchBtn = Rectangle{ 12 + emojiW, L.buttonY, searchW, L.buttonH };
        Rectangle fileBtn = Rectangle{ (float)screenWidth - fileW - 10, L.buttonY, fileW, L.buttonH };
        if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
            Vector2 mousePos = GetMousePosition();
            int hit = PanelHitTest(screenWidth, mousePos);
            if (hit >= 0) g_clientTargetId = hit;
            // 房间输入框
            Rectangle roomBox = Rectangle{ PanelX(screenWidth), RoomBoxY(), PANEL_W - 10, (float)(g_fontSize + 6) };
            if (CheckCollisionPointRec(mousePos, roomBox)) {
                g_roomFocus = true;
                g_roomCaret = CaretFromX(g_roomInput, g_fontSize - 2, mousePos.x, roomBox.x + 4);
            } else {
                g_roomFocus = false;
            }
            // 输入框点击定位光标
            if (CheckCollisionPointRec(mousePos, inputBox)) {
                g_clientCaret = CaretFromX(clientInput, g_fontSize, mousePos.x, 15);
            }
            // 表情/搜索按钮
            if (CheckCollisionPointRec(mousePos, emojiBtn)) g_emojiOpen = !g_emojiOpen;
            if (CheckCollisionPointRec(mousePos, searchBtn)) { g_searchActive = !g_searchActive; g_searchCaret = g_searchText.size(); }
            // 发送文件按钮
            if (CheckCollisionPointRec(mousePos, fileBtn)) {
                std::wstring path;
                if (PickFile(path)) StartFileSend(path, g_clientTargetId);
            }
        }
        UpdateIBeamCursor({ inputBox });
    }
    if (IsMouseButtonPressed(MOUSE_RIGHT_BUTTON)) {
        Vector2 mp = GetMousePosition();
        for (auto it = g_msgRows.rbegin(); it != g_msgRows.rend(); ++it) {
            if (mp.y >= it->y && mp.y <= it->y + it->h) {
                g_ctx.open = true;
                g_ctx.msgId = it->msgId;
                g_ctx.senderId = it->senderId;
                g_ctx.x = mp.x;
                g_ctx.y = mp.y;
                break;
            }
        }
    }

    // 测试钩子 F8
    if (IsKeyPressed(KEY_F8)) {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE");
        if (tf) StartFileSend(AnsiToWide(tf), g_clientTargetId);
    }

    // 布局与绘制(统一布局: 输入框 -> 提示 -> 按钮 -> 进度条 -> 消息区, 各区域互不重叠)
    ChatLayout L = ComputeChatLayout(screenHeight, g_reply.active);
    float emojiW = (float)MeasureTextC("表情", g_fontSize - 2) + 18;
    float searchW = (float)MeasureTextC("搜索", g_fontSize - 2) + 18;
    float fileW = (float)MeasureTextC("发送文件…", g_fontSize - 2) + 24;
    Rectangle inputBox = Rectangle{ 10, L.inputTop, PanelX(screenWidth) - 20, L.inputH };
    Rectangle emojiBtn = Rectangle{ 10, L.buttonY, emojiW, L.buttonH };
    Rectangle searchBtn = Rectangle{ 12 + emojiW, L.buttonY, searchW, L.buttonH };
    Rectangle fileBtn = Rectangle{ (float)screenWidth - fileW - 10, L.buttonY, fileW, L.buttonH };

    BeginDrawing();
    ClearBackground(g_theme.bg);
    DrawChatMessages(screenWidth, L.messageBottom, "聊天客户端 - 消息记录：");
    std::string selfLabel = "（我是 " + (g_myName.empty() ? "?" : g_myName) + "）";
    DrawUserPanel(screenWidth, screenHeight, g_clientTargetId, selfLabel);
    DrawTransferBars(screenWidth, L.barsStartY);
    DrawContextMenu();
    if (g_emojiOpen) DrawEmojiPanel(10, L.buttonY - 100);

    // 提示行(从 promptTop 依次向下排)
    float py = L.promptTop;
    if (g_reply.active) {
        std::string rp = "回复 " + g_reply.name + "（Esc 取消）";
        DrawTextC(rp.c_str(), 10, py, g_fontSize - 2, g_theme.dim);
        py += 24;
    }
    DrawTextC(("发送到：" + TargetLabel(g_clientTargetId) + "（回车发送，Shift+Enter 换行）").c_str(), 10, py, g_fontSize - 2, g_theme.dim);

    // 输入框(多行)
    DrawRectangleRec(inputBox, g_theme.panel);
    DrawRectangleLines((int)inputBox.x, (int)inputBox.y, (int)inputBox.width, (int)inputBox.height, g_theme.sep);
    {
        size_t pos = 0;
        int li = 0;
        std::string line;
        float ly = L.inputTop + 3;
        while (pos <= clientInput.size() && li < MAX_INPUT_LINES) {
            size_t nl = clientInput.find('\n', pos);
            line = clientInput.substr(pos, nl == std::string::npos ? std::string::npos : nl - pos);
            DrawTextC(line.c_str(), inputBox.x + 5, ly, g_fontSize, g_theme.inputText);
            size_t lineStart = pos;
            size_t lineEnd = nl == std::string::npos ? clientInput.size() : nl;
            if (g_clientCaret >= lineStart && g_clientCaret <= lineEnd) {
                if (fmod(GetTime(), 1.0) < 0.5) {
                    float cx = inputBox.x + 5 + MeasureTextC(clientInput.substr(lineStart, g_clientCaret - lineStart).c_str(), g_fontSize);
                    DrawRectangle((int)cx, (int)ly + 2, 2, g_fontSize - 2, g_theme.dim);
                }
            }
            ly += L.lineH;
            ++li;
            if (nl == std::string::npos) break;
            pos = nl + 1;
        }
    }
    // 按钮
    DrawRectangleRec(emojiBtn, g_theme.hl);
    DrawTextCenteredInRect("表情", emojiBtn, g_fontSize - 2, g_theme.text);
    DrawRectangleRec(searchBtn, g_theme.hl);
    DrawTextCenteredInRect("搜索", searchBtn, g_fontSize - 2, g_theme.text);
    DrawRectangleRec(fileBtn, ORANGE);
    DrawTextCenteredInRect("发送文件…", fileBtn, g_fontSize - 2, BLACK);
    EndDrawing();
}

// ==================== 快捷键/托盘/字号/主题处理 ====================
void ProcessGlobalShortcuts() {
    bool ctrl = IsKeyDown(KEY_LEFT_CONTROL) || IsKeyDown(KEY_RIGHT_CONTROL);
    // 字号调节 Ctrl+滚轮
    int wheel = (int)GetMouseWheelMove();
    if (ctrl && wheel != 0) {
        int newSize = g_fontSize + (wheel > 0 ? 2 : -2);
        if (newSize < 16) newSize = 16;
        if (newSize > 40) newSize = 40;
        if (newSize != g_fontSize) {
            g_fontSize = newSize;
            ReloadFonts();
        }
    }
    // 主题切换 F2
    if (IsKeyPressed(KEY_F2)) {
        g_darkMode = !g_darkMode;
        ApplyTheme();
    }
#ifdef _WIN32
    // 最小化到托盘
    if (IsWindowMinimized() && !g_inTray) {
        HWND hwnd = (HWND)GetWindowHandle();
        if (hwnd) {
            ShowWindow(hwnd, SW_HIDE);
            TrayAdd();
        }
    }
#endif
}

// ==================== 主函数 ====================
int main() {
    if (!InitNetwork()) {
        return 1;
    }

    const char* testIp = std::getenv("TCPCHAT_TEST_IP");
    if (testIp && *testIp) ipInput = testIp;
    const char* testName = std::getenv("TCPCHAT_TEST_NAME");
    if (testName && *testName) {
        nameInput = testName;
        g_nameCaret = nameInput.size();
    }
    const char* testPwd = std::getenv("TCPCHAT_TEST_PASSWORD");
    if (testPwd && *testPwd) {
        passwordInput = testPwd;
        g_pwdCaret = passwordInput.size();
    }

    SetConfigFlags(FLAG_WINDOW_RESIZABLE);
    InitWindow(DEFAULT_SCREEN_WIDTH, DEFAULT_SCREEN_HEIGHT, "TCP Chat");
    int refresh = GetMonitorRefreshRate(GetCurrentMonitor());
    SetTargetFPS(refresh > 0 ? refresh : 60);
    SetExitKey(KEY_NULL); // ESC 由应用自行处理

    g_font = LoadCjkFont();
    g_emojiFont = LoadEmojiFont();
    ApplyTheme();
#ifdef _WIN32
    InstallInputHook();
#endif
    SetWindowTitleC(L"选择模式", "Select Mode");

    InitAutoTest();
    while (!WindowShouldClose() && !g_quitRequested) {
        ProcessAutoTest();
        ProcessGlobalShortcuts();
        switch (g_mode) {
            case AppMode::Select: UpdateSelectFrame(); break;
            case AppMode::Server:  UpdateServerFrame();  break;
            case AppMode::Client:  UpdateClientFrame();  break;
        }
    }

    // 清理
    if (g_mode == AppMode::Server) ShutdownServer();
    if (g_mode == AppMode::Client) ShutdownClient();
    for (auto& fs : g_fileSends) {
        if (fs.active && fs.f) fclose(fs.f);
    }
    for (auto& kv : g_fileRecv) {
        if (kv.second.f) fclose(kv.second.f);
    }
    WriteDebugDump();
#ifdef _WIN32
    TrayRemove();
    RemoveInputHook();
#endif
    if (g_fontLoaded) UnloadFont(g_font);
    if (g_emojiLoaded) UnloadFont(g_emojiFont);
    CloseWindow();
    CleanupNetwork();
    return 0;
}
