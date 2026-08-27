// TCP Chat 9.0-snapshot5
// 图形化局域网聊天程序: 完整中文支持 + IPv6 + 私聊 + 文件传输
//   消息滚动条 / 输入光标 / FPS 跟随显示器 / 传输进度条 / SHA-256 校验 / 自定义昵称
// 依赖: raylib 5.5 + Winsock2(Windows) / BSD socket(Linux)
// 协议: 换行分隔的 UTF-8 文本行, 字段以 '|' 分隔(末字段可含任意文本)
//   MSG|<text>                客户端->服务器: 广播消息
//   PMSG|<targetId>|<text>    客户端->服务器: 私聊(0=服务器)
//   MSG|<id>|<name>|<text>    服务器->客户端: 广播
//   PMSG|<id>|<name>|<text>   服务器->客户端: 私聊
//   NAME|<nick>               客户端->服务器: 设置昵称(连接后首行)
//   WELCOME|<id>|<name> / ROSTER|<id:name,...> / JOIN|<id>|<name> / LEAVE|<id>|<name>
//   RENAME|<id>|<oldName>|<newName>  服务器->其他客户端: 昵称变更
//   FILE_OFFER|<targetId>|<fileName>|<size>|<fileId>    (客户端->服务器)
//   FILE_OFFER|<senderId>|<senderName>|<fileName>|<size>|<fileId>  (服务器->客户端)
//   FILE_DATA|<fileId>|<base64> / FILE_DONE|<fileId>|<bytes>|<sha256hex> / FILE_ERROR|<fileId>|<msg>

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
#include <fstream>
#include <cstdlib>

#ifdef _WIN32
// raylib 与 Windows 头文件的兼容宏
// NOGDI/NOUSER: 排除 wingdi/winuser 中与 raylib 冲突的声明
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

typedef LRESULT (CALLBACK* WNDPROC)(HWND, UINT, WPARAM, LPARAM);

extern "C" {
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
__declspec(dllimport) HIMC      WINAPI ImmGetContext(HWND hWnd);
__declspec(dllimport) BOOL      WINAPI ImmReleaseContext(HWND hWnd, HIMC hIMC);
__declspec(dllimport) LONG      WINAPI ImmGetCompositionStringW(HIMC hIMC, DWORD dwIndex, LPVOID lpBuf, DWORD dwBufLen);
// 注: CreateDirectoryW / MultiByteToWideChar / WideCharToMultiByte 由核心头
// (fileapi.h / stringapiset.h) 提供, 无需手动声明
}

// ---- 文件打开对话框(comdlg32) ----
// OPENFILENAMEW 结构(布局与 Windows SDK 一致, 64 位下大小 152 字节)
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
constexpr int PORT = 5555;
constexpr int MAX_CLIENTS = 10;
constexpr int BUFFER_SIZE = 16384;
constexpr size_t MAX_LINE_LENGTH = 1024 * 1024;
constexpr int DEFAULT_SCREEN_WIDTH = 800;
constexpr int DEFAULT_SCREEN_HEIGHT = 600;
constexpr int UI_FONT_SIZE = 20;
constexpr size_t FILE_CHUNK_RAW = 24 * 1024;   // 每块 24KB
constexpr int FILE_CHUNKS_PER_FRAME = 6;       // 每帧最多发 6 块

// ==================== 全局状态 ====================
std::mutex messagesMutex;
std::vector<std::string> chatMessages;
int g_chatScroll = 0;          // 消息区滚动偏移(0=底部/最新)
bool g_scrollDragging = false; // 滚动条拖拽中

Font g_font;
bool g_fontLoaded = false;

// ---- 输入法字符队列 ----
std::mutex inputMutex;
std::vector<uint32_t> inputQueue;
std::vector<uint32_t> g_debugLog;   // 调试: 输入码点(TCPCHAT_DEBUG_CODEPOINTS)
std::vector<std::string> g_eventLog; // 调试: 事件日志(TCPCHAT_DEBUG_LOG)
uint32_t g_pendingHighSurrogate = 0;

void LogEvent(const std::string& e) {
    std::string s = e.size() > 200 ? e.substr(0, 200) + "..." : e;
    if (g_eventLog.size() < 200000) g_eventLog.push_back(s);
}

// ==================== UTF 转换工具 ====================
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

void Utf8PopChar(std::string& s) {
    if (s.empty()) return;
    size_t n = s.size() - 1;
    while (n > 0 && ((unsigned char)s[n] & 0xC0) == 0x80) --n;
    s.erase(n);
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
// Linux 简化实现(主目标平台为 Windows, 这里仅保证可编译)
std::wstring AnsiToWide(const std::string& s) {
    std::wstring w;
    for (unsigned char c : s) w += (wchar_t)c;
    return w;
}
std::string WideToUtf8(const std::wstring& s) {
    std::string u;
    for (wchar_t c : s) u += (char)(c & 0xFF);
    return u;
}
std::wstring Utf8ToWide(const std::string& s) { return AnsiToWide(s); }
#endif

// 文件打开/定位辅助(Windows 用宽字符路径)
FILE* OpenFileWrite(const std::wstring& path) {
#ifdef _WIN32
    return _wfopen(path.c_str(), L"wb");
#else
    return fopen(WideToUtf8(path).c_str(), "wb");
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

void FileSeekStart(FILE* f) {
#ifdef _WIN32
    _fseeki64(f, 0, SEEK_SET);
#else
    fseeko(f, 0, SEEK_SET);
#endif
}

void EnsureReceivedDir() {
#ifdef _WIN32
    CreateDirectoryW(L"received", NULL);
#else
    mkdir("received", 0755);
#endif
}

// 去掉行尾换行符
void TrimLineEnd(std::string& s) {
    while (!s.empty() && (s.back() == '\n' || s.back() == '\r')) s.pop_back();
}

// 按 '|' 切分字段, 最多 maxFields 个(末字段为剩余全部)
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

long long ParseLL(const std::string& s) {
    return std::atoll(s.c_str());
}

std::string SizeStr(long long bytes) {
    if (bytes < 1024) return std::to_string(bytes) + " B";
    if (bytes < 1024 * 1024) return std::to_string(bytes / 1024) + " KB";
    return std::to_string(bytes / (1024 * 1024)) + " MB";
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
        bitLen = 0;
        bufLen = 0;
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
            bufLen += take;
            p += take;
            len -= take;
            if (bufLen == 64) {
                Transform(buf);
                bufLen = 0;
            }
        }
        while (len >= 64) {
            Transform(p);
            p += 64;
            len -= 64;
        }
        if (len > 0) {
            memcpy(buf, p, len);
            bufLen = len;
        }
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

// ==================== 光标/UTF-8 编辑工具 ====================
// 前向声明(绘制工具定义在文件后方)
void DrawTextC(const char* text, float x, float y, int size, Color color);
int MeasureTextC(const char* text, int size);

// 下一个/上一个 UTF-8 字符边界(字节偏移)
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

// 在 caret 处插入码点(UTF-8), caret 后移
void InsertUtf8At(std::string& s, size_t& caret, uint32_t cp) {
    std::string u;
    AppendUtf8(u, cp);
    s.insert(caret, u);
    caret += u.size();
}

// 删除 caret 前一个字符
void DelCharBefore(std::string& s, size_t& caret) {
    if (caret == 0) return;
    size_t q = Utf8Prev(s, caret);
    s.erase(q, caret - q);
    caret = q;
}

// 删除 caret 处字符
void DelCharAt(std::string& s, size_t& caret) {
    if (caret >= s.size()) return;
    size_t q = Utf8Next(s, caret);
    s.erase(caret, q - caret);
}

// 光标键处理(左右/Home/End)
void HandleCaretKeys(const std::string& s, size_t& caret) {
    if (IsKeyPressed(KEY_LEFT)) caret = Utf8Prev(s, caret);
    if (IsKeyPressed(KEY_RIGHT)) caret = Utf8Next(s, caret);
    if (IsKeyPressed(KEY_HOME)) caret = 0;
    if (IsKeyPressed(KEY_END)) caret = s.size();
}

// 根据点击 x 坐标计算光标位置(取宽度最接近的字符边界)
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

// 绘制带光标的输入框文本
void DrawFieldText(const std::string& s, size_t caret, float x, float y, Color textColor, bool focused) {
    DrawTextC(s.c_str(), x, y, UI_FONT_SIZE, textColor);
    if (focused && fmod(GetTime(), 1.0) < 0.5) {
        float cx = x + (float)MeasureTextC(s.substr(0, caret).c_str(), UI_FONT_SIZE);
        DrawRectangle((int)cx, (int)y + 2, 2, UI_FONT_SIZE - 2, DARKGRAY);
    }
}

// 鼠标悬停在输入框上时切换为 I 型光标
void UpdateIBeamCursor(const std::vector<Rectangle>& boxes) {
    Vector2 m = GetMousePosition();
    bool over = false;
    for (const auto& r : boxes) {
        if (CheckCollisionPointRec(m, r)) { over = true; break; }
    }
    SetMouseCursor(over ? MOUSE_CURSOR_IBEAM : MOUSE_CURSOR_DEFAULT);
}

// 昵称清理: 去除协议分隔符并限制长度
std::string SanitizeName(const std::string& raw) {
    std::string out;
    size_t chars = 0;
    size_t p = 0;
    while (p < raw.size() && chars < 16) {
        // 取一个码点
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

// 非阻塞发送队列刷出(EWOULDBLOCK 时留待下帧)
void FlushSendQueue(SocketType sock, std::string& buf) {
    while (!buf.empty()) {
        int n = send(sock, buf.data(), (int)buf.size(), 0);
        if (n == SOCKET_ERROR_CODE) {
#ifdef _WIN32
            if (WSAGetLastError() == WSAEWOULDBLOCK) break;
#else
            if (errno == EAGAIN || errno == EWOULDBLOCK) break;
#endif
            break; // 实际错误由 recv 路径检测
        }
        buf.erase(0, (size_t)n);
    }
}

void AddMessage(const std::string& msg) {
    std::lock_guard<std::mutex> lock(messagesMutex);
    chatMessages.push_back(msg);
    if (chatMessages.size() > 100) {
        chatMessages.erase(chatMessages.begin());
    }
    // 用户上翻查看历史时, 新消息到来保持视口稳定
    if (g_chatScroll > 0) g_chatScroll = std::min(g_chatScroll + 1, 100000);
}

void ClearMessages() {
    std::lock_guard<std::mutex> lock(messagesMutex);
    chatMessages.clear();
}

// 地址转字符串(IPv4-mapped IPv6 还原为 IPv4 显示)
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

// ==================== 应用状态机 ====================
enum class AppMode { Select, Server, Client };
AppMode g_mode = AppMode::Select;

// 选择模式
std::string ipInput = "127.0.0.1";
std::string nameInput;          // 自定义昵称(留空自动分配)
size_t g_ipCaret = ipInput.size();
size_t g_nameCaret = 0;
enum class SelFocus { Name, Ip };
SelFocus g_selFocus = SelFocus::Name;

// 服务器模式
SocketType listenSock = INVALID_SOCKET;
struct ClientInfo {
    SocketType sock = INVALID_SOCKET;
    int id = 0;
    std::string name;
    std::string recvBuf;  // 行重组缓冲
    std::string sendBuf;  // 待发送队列
};
std::vector<ClientInfo> g_clients;
int g_nextClientId = 1;
std::string serverInput;
size_t g_serverCaret = 0;
int g_serverTargetId = 0;  // 0=广播

// 客户端模式
SocketType clientSock = INVALID_SOCKET;
std::string g_clientRecvBuf;
std::string g_clientSendBuf;
std::string clientInput;
size_t g_clientCaret = 0;
int g_myId = 0;
std::string g_myName;
std::map<int, std::string> g_roster;  // id -> name
int g_clientTargetId = 0;             // 0=广播(服务器)

// ---- 文件传输 ----
struct FileReceiveState {
    int fileId = 0;
    std::wstring path;
    FILE* f = nullptr;
    long long size = 0, received = 0;
    std::string name, senderLabel;
    Sha256 sha;   // 接收数据哈希
};
std::map<int, FileReceiveState> g_fileRecv;

struct FileSendState {
    bool active = false;
    int fileId = 0;
    std::wstring path;
    std::string name;
    long long size = 0, sent = 0;
    FILE* f = nullptr;
    int targetId = 0;
    Sha256 sha;   // 发送数据哈希
};
FileSendState g_fileSend;
int g_nextFileId = 1000;
std::map<std::pair<int, int>, int> g_fileIdMap;     // (senderId, clientFileId) -> serverFileId (服务器中转用)
std::map<int, int> g_fileRelayTarget;               // serverFileId -> 目标客户端 id (服务器中转用)

// ==================== 中文字体 ====================
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

// ==================== Windows 输入子系统(IME + 粘贴) ====================
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
            g_pendingHighSurrogate = 0;
        }
    } else if (cp >= 0xD800 && cp <= 0xDBFF) {
        g_pendingHighSurrogate = cp;
        return;
    }
    inputQueue.push_back(cp);
    g_debugLog.push_back(cp);
}

// 读取剪贴板文本(Ctrl+V / Shift+Insert 粘贴)
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

// 子类化窗口过程
// 注意: RGFW 创建的是 ANSI 窗口, Unicode 字符消息会被系统破坏性转换,
// 因此中文输入统一通过 WM_IME_COMPOSITION + ImmGetCompositionStringW 读取;
// WM_CHAR 仅可靠传递 ASCII。
LRESULT CALLBACK ChatWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
        case WM_CHAR: {
            uint32_t cp = (uint32_t)wParam;
            if (cp >= 32 && cp < 0x80) {
                PushInputChar(cp);
            }
            return 0;
        }
        case WM_IME_CHAR: {
            return 0; // 忽略: 完整结果由 WM_IME_COMPOSITION 提供
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
            // 消费该消息: 防止 DefWindowProc 生成被破坏的 WM_CHAR
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
#endif // _WIN32

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

// 文件接收(服务器与客户端共用)
bool PrepareReceiveFile(int fileId, const std::string& fileNameUtf8, long long size, const std::string& senderLabel) {
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
    std::wstring path = L"received" + sep + wname;
    int suffix = 1;
    while (true) {
        FILE* probe = OpenFileRead(path);
        if (!probe) break;
        fclose(probe);
        path = L"received" + sep + wname.substr(0, wname.find_last_of(L'.')) + L"_" + std::to_wstring(suffix) + (wname.find_last_of(L'.') != std::wstring::npos ? wname.substr(wname.find_last_of(L'.')) : L"");
        ++suffix;
    }
    FILE* f = OpenFileWrite(path);
    if (!f) return false;
    FileReceiveState st;
    st.fileId = fileId;
    st.path = path;
    st.f = f;
    st.size = size;
    st.received = 0;
    st.name = safeName;
    st.senderLabel = senderLabel;
    st.sha.Init();
    g_fileRecv[fileId] = st;
    AddMessage(senderLabel + " 发来文件：" + safeName + "（" + SizeStr(size) + "）");
    LogEvent("FILE_START " + std::to_string(fileId) + " " + safeName + " " + std::to_string(size));
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
    }
    return true;
}

void FinishReceiveFile(int fileId, long long bytes, const std::string& shaHex) {
    auto it = g_fileRecv.find(fileId);
    if (it == g_fileRecv.end()) return;
    fclose(it->second.f);
    std::wstring path = it->second.path;
    std::string name = it->second.name;
    long long size = it->second.size;
    long long recv = it->second.received;
    unsigned char digest[32];
    it->second.sha.Final(digest);
    std::string actual = Sha256::Hex(digest);
    g_fileRecv.erase(it);
    if (bytes >= 0) recv = bytes;
    if (recv != size) {
        AddMessage("文件接收不完整：" + name + "（" + std::to_string(recv) + "/" + std::to_string(size) + " 字节）");
        LogEvent("FILE_INCOMPLETE " + name + " " + std::to_string(recv) + "/" + std::to_string(size));
    } else if (actual != shaHex) {
        AddMessage("文件接收完成：" + name + "，但 SHA-256 校验失败（文件可能已损坏）");
        LogEvent("FILE_SAVED " + WideToUtf8(path) + " " + std::to_string(size) + " sha:mismatch");
    } else {
        AddMessage("文件接收完成：" + name + "（" + SizeStr(size) + "）已保存到 received 目录，SHA-256 校验通过");
        LogEvent("FILE_SAVED " + WideToUtf8(path) + " " + std::to_string(size) + " sha:ok");
    }
}

void AbortReceiveFile(int fileId, const std::string& msg) {
    auto it = g_fileRecv.find(fileId);
    if (it == g_fileRecv.end()) return;
    fclose(it->second.f);
    g_fileRecv.erase(it);
    AddMessage("文件接收中断：" + msg);
}

// 服务器: 处理一条来自客户端的协议行
void ServerHandleLine(const std::string& line, ClientInfo& from) {
    LogEvent("RECV " + from.name + " " + line);
    std::vector<std::string> f = SplitFields(line, 5);
    if (f.empty()) return;
    const std::string& t = f[0];

    if (t == "NAME" && f.size() >= 2) {
        std::string newName = SanitizeName(f[1]);
        if (!newName.empty() && newName != from.name) {
            std::string oldName = from.name;
            from.name = newName;
            AddMessage(oldName + " 改名为 " + newName);
            ServerSendToClient(from.id, "WELCOME|" + std::to_string(from.id) + "|" + newName + "\n");
            ServerBroadcastOthers("RENAME|" + std::to_string(from.id) + "|" + oldName + "|" + newName + "\n", from.sock);
            ServerBroadcastAll(RosterLine() + "\n");
        }
    } else if (t == "MSG" && f.size() >= 2) {
        AddMessage(from.name + "：" + f[1]);
        ServerBroadcastOthers("MSG|" + std::to_string(from.id) + "|" + from.name + "|" + f[1] + "\n", from.sock);
    } else if (t == "PMSG" && f.size() >= 3) {
        int targetId = (int)ParseLL(f[1]);
        if (targetId == 0) {
            AddMessage(from.name + "（私聊服务器）：" + f[2]);
        } else if (FindClientById(targetId)) {
            AddMessage(from.name + " → " + ClientNameOf(targetId) + "：" + f[2]);
            ServerSendToClient(targetId, "PMSG|" + std::to_string(from.id) + "|" + from.name + "|" + f[2] + "\n");
        }
    } else if (t == "FILE_OFFER" && f.size() >= 5) {
        int targetId = (int)ParseLL(f[1]);
        std::string fileName = f[2];
        long long size = ParseLL(f[3]);
        int clientFileId = (int)ParseLL(f[4]);
        int serverFileId = g_nextFileId++;
        g_fileIdMap[std::make_pair(from.id, clientFileId)] = serverFileId;
        if (targetId == 0) {
            PrepareReceiveFile(serverFileId, fileName, size, from.name);
        } else if (FindClientById(targetId)) {
            g_fileRelayTarget[serverFileId] = targetId;
            ServerSendToClient(targetId, "FILE_OFFER|" + std::to_string(from.id) + "|" + from.name + "|" + fileName + "|" + std::to_string(size) + "|" + std::to_string(serverFileId) + "\n");
        } else {
            g_fileIdMap.erase(std::make_pair(from.id, clientFileId));
            ServerSendToClient(from.id, "FILE_ERROR|" + std::to_string(clientFileId) + "|对方已离线\n");
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
        }
    }
}

// ==================== 客户端逻辑 ====================
void ClientHandleLine(const std::string& line) {
    LogEvent("RECV " + line);
    std::vector<std::string> f = SplitFields(line, 6);
    if (f.empty()) return;
    const std::string& t = f[0];

    if (t == "WELCOME" && f.size() >= 3) {
        g_myId = (int)ParseLL(f[1]);
        g_myName = f[2];
        AddMessage("已连接到服务器，你是 " + g_myName);
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
        AddMessage(f[2] + " 已加入");
    } else if (t == "LEAVE" && f.size() >= 3) {
        int id = (int)ParseLL(f[1]);
        auto it = g_roster.find(id);
        AddMessage((it != g_roster.end() ? it->second : f[2]) + " 已离开");
        g_roster.erase(id);
    } else if (t == "RENAME" && f.size() >= 4) {
        int id = (int)ParseLL(f[1]);
        std::string oldName = f[2], newName = f[3];
        g_roster[id] = newName;
        AddMessage(oldName + " 改名为 " + newName);
    } else if (t == "MSG" && f.size() >= 4) {
        AddMessage(f[2] + "：" + f[3]);
    } else if (t == "PMSG" && f.size() >= 4) {
        AddMessage(f[2] + "（私聊）：" + f[3]);
    } else if (t == "FILE_OFFER" && f.size() >= 6) {
        int fileId = (int)ParseLL(f[5]);
        PrepareReceiveFile(fileId, f[3], ParseLL(f[4]), f[2]);
    } else if (t == "FILE_DATA" && f.size() >= 3) {
        ReceiveFileChunk((int)ParseLL(f[1]), f[2]);
    } else if (t == "FILE_DONE" && f.size() >= 4) {
        FinishReceiveFile((int)ParseLL(f[1]), ParseLL(f[2]), f[3]);
    } else if (t == "FILE_ERROR" && f.size() >= 3) {
        int fileId = (int)ParseLL(f[1]);
        std::string msg = f.size() > 2 ? f[2] : "对方取消";
        if (g_fileSend.active && g_fileSend.fileId == fileId) {
            fclose(g_fileSend.f);
            g_fileSend.active = false;
            AddMessage("文件发送失败：" + msg);
            LogEvent("FILE_SEND_ABORT " + msg);
        } else {
            AbortReceiveFile(fileId, msg);
        }
    }
}

// ==================== 文件发送 ====================
void StartFileSend(const std::wstring& path, int targetId) {
    if (g_fileSend.active) {
        AddMessage("已有文件正在发送");
        return;
    }
    FILE* f = OpenFileRead(path);
    if (!f) {
        AddMessage("无法打开文件：" + WideToUtf8(path));
        return;
    }
    FileSeekEnd(f);
    long long size = FileTell(f);
    FileSeekStart(f);
    if (size <= 0) {
        fclose(f);
        AddMessage("文件为空，无法发送");
        return;
    }
    size_t slash = path.find_last_of(L"\\/");
    std::wstring wname = slash == std::wstring::npos ? path : path.substr(slash + 1);
    std::string name = WideToUtf8(wname);
    for (auto& ch : name) {
        if (ch == '|' || ch == '\n' || ch == '\r') ch = '_';
    }

    g_fileSend.active = true;
    g_fileSend.fileId = g_nextFileId++;
    g_fileSend.path = path;
    g_fileSend.name = name;
    g_fileSend.size = size;
    g_fileSend.sent = 0;
    g_fileSend.f = f;
    g_fileSend.targetId = targetId;
    g_fileSend.sha.Init();

    std::string offer;
    if (g_mode == AppMode::Client) {
        offer = "FILE_OFFER|" + std::to_string(targetId) + "|" + name + "|" + std::to_string(size) + "|" + std::to_string(g_fileSend.fileId) + "\n";
        g_clientSendBuf += offer;
        LogEvent("SEND " + offer);
    } else {
        // 服务器发文件给指定客户端
        if (targetId <= 0 || !FindClientById(targetId)) {
            g_fileSend.active = false;
            fclose(f);
            AddMessage("请先在右侧选择接收用户");
            return;
        }
        offer = "FILE_OFFER|0|服务器|" + name + "|" + std::to_string(size) + "|" + std::to_string(g_fileSend.fileId) + "\n";
        ServerSendToClient(targetId, offer);
    }
    AddMessage("开始发送文件：" + name + "（" + SizeStr(size) + "）");
    LogEvent("FILE_SEND_START " + name + " " + std::to_string(size));
}

// 每帧泵送文件数据(客户端与服务器共用)
void PumpFileSend() {
    if (!g_fileSend.active) return;
    int chunks = 0;
    while (g_fileSend.active && chunks < FILE_CHUNKS_PER_FRAME) {
        std::vector<unsigned char> raw(FILE_CHUNK_RAW);
        size_t n = fread(raw.data(), 1, raw.size(), g_fileSend.f);
        if (n > 0) {
            g_fileSend.sha.Update(raw.data(), n);
            std::string b64 = Base64Encode(raw.data(), n);
            std::string line = "FILE_DATA|" + std::to_string(g_fileSend.fileId) + "|" + b64 + "\n";
            if (g_mode == AppMode::Client) {
                g_clientSendBuf += line;
            } else {
                ServerSendToClient(g_fileSend.targetId, line);
            }
            g_fileSend.sent += (long long)n;
            ++chunks;
        }
        if (n < raw.size()) {
            // 文件结束: 附上 SHA-256 供接收端校验
            unsigned char digest[32];
            g_fileSend.sha.Final(digest);
            std::string done = "FILE_DONE|" + std::to_string(g_fileSend.fileId) + "|" + std::to_string(g_fileSend.sent) + "|" + Sha256::Hex(digest) + "\n";
            if (g_mode == AppMode::Client) {
                g_clientSendBuf += done;
                LogEvent("SEND " + done);
            } else {
                ServerSendToClient(g_fileSend.targetId, done);
            }
            fclose(g_fileSend.f);
            AddMessage("文件发送完成：" + g_fileSend.name + "（" + SizeStr(g_fileSend.sent) + "）");
            LogEvent("FILE_SEND_DONE " + g_fileSend.name + " " + std::to_string(g_fileSend.sent));
            g_fileSend.active = false;
            break;
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
    listenSock = socket(AF_INET6, SOCK_STREAM, 0);
    if (listenSock == INVALID_SOCKET) return false;
    int opt = 1;
    setsockopt(listenSock, SOL_SOCKET, SO_REUSEADDR, (const char*)&opt, sizeof(opt));
    // 双栈: 同时接受 IPv4 与 IPv6
    int no = 0;
    setsockopt(listenSock, IPPROTO_IPV6, IPV6_V6ONLY, (const char*)&no, sizeof(no));
    sockaddr_in6 serverAddr;
    memset(&serverAddr, 0, sizeof(serverAddr));
    serverAddr.sin6_family = AF_INET6;
    serverAddr.sin6_addr = in6addr_any;
    serverAddr.sin6_port = htons(PORT);
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

// 连接服务器(支持 IPv4 / IPv6 / 主机名)
bool ConnectClient(const std::string& host) {
    addrinfo hints;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* res = nullptr;
    std::string port = std::to_string(PORT);
    if (getaddrinfo(host.c_str(), port.c_str(), &hints, &res) != 0) {
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
    if (g_fileSend.active && g_fileSend.f) {
        fclose(g_fileSend.f);
        g_fileSend.active = false;
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
    if (StartServer()) {
        g_mode = AppMode::Server;
        SetWindowTitleC(L"聊天服务器", "Chat Server");
        AddMessage("服务器已启动，监听端口 " + std::to_string(PORT) + "（IPv4/IPv6 双栈）");
    } else {
        AddMessage("启动服务器失败：端口 " + std::to_string(PORT) + " 可能已被占用");
    }
}

void DoStartClient() {
    ClearMessages();
    clientInput.clear();
    g_clientTargetId = 0;
    if (ConnectClient(ipInput)) {
        g_mode = AppMode::Client;
        SetWindowTitleC(L"聊天客户端", "Chat Client");
        AddMessage("正在连接服务器 " + ipInput + " ...");
        if (!nameInput.empty()) {
            std::string nick = SanitizeName(nameInput);
            if (!nick.empty()) {
                g_clientSendBuf += "NAME|" + nick + "\n";
                LogEvent("SEND NAME|" + nick);
            }
        }
    } else {
        AddMessage("无法连接到服务器 " + ipInput + "，请检查地址和端口");
    }
}

void DoSendMessage(const std::string& text) {
    if (g_mode == AppMode::Server) {
        if (g_serverTargetId == 0) {
            AddMessage("服务器：" + text);
            ServerBroadcastAll("MSG|0|服务器|" + text + "\n");
        } else {
            AddMessage("服务器 → " + TargetLabel(g_serverTargetId) + "：" + text);
            ServerSendToClient(g_serverTargetId, "PMSG|0|服务器|" + text + "\n");
        }
    } else if (g_mode == AppMode::Client) {
        if (g_clientTargetId == 0) {
            AddMessage("你：" + text);
            g_clientSendBuf += "MSG|" + text + "\n";
            LogEvent("SEND MSG|" + text);
        } else {
            AddMessage("你 → " + TargetLabel(g_clientTargetId) + "：" + text);
            g_clientSendBuf += "PMSG|" + std::to_string(g_clientTargetId) + "|" + text + "\n";
            LogEvent("SEND PMSG|" + std::to_string(g_clientTargetId) + "|" + text);
        }
    }
}

// ---- 自动化测试钩子(TCPCHAT_AUTOTEST 环境变量) ----
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
    } else if (a.rfind("msg:", 0) == 0) {
        DoSendMessage(a.substr(4));
    } else if (a == "sendfile") {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE");
        if (tf) {
            if (g_mode == AppMode::Client) StartFileSend(AnsiToWide(tf), g_clientTargetId);
            else if (g_mode == AppMode::Server) StartFileSend(AnsiToWide(tf), g_serverTargetId);
        }
    }
    g_autoTest.nextTimeMs = NowMsAuto() + delayMs;
}

// ---- 选择模式 ----
void UpdateSelectFrame() {
    int screenWidth = GetScreenWidth();
    float margin = 20;
    float titleY = margin;
    float nameLabelY = 60, nameBoxY = 85;
    float ipLabelY = 140, ipBoxY = 165;
    float buttonY = 235;

    Rectangle nameBox = Rectangle{ margin, nameBoxY, (float)screenWidth - 2 * margin, 30 };
    Rectangle ipBox = Rectangle{ margin, ipBoxY, (float)screenWidth - 2 * margin, 30 };
    float buttonWidth = 160, buttonHeight = 40, gap = 20;
    float totalButtonsWidth = 2 * buttonWidth + gap;
    float startX = (screenWidth - totalButtonsWidth) / 2;
    Rectangle serverBtn = Rectangle{ startX, buttonY, buttonWidth, buttonHeight };
    Rectangle clientBtn = Rectangle{ startX + buttonWidth + gap, buttonY, buttonWidth, buttonHeight };

    // 输入(带插入光标)
    std::string& curText = (g_selFocus == SelFocus::Name) ? nameInput : ipInput;
    size_t& curCaret = (g_selFocus == SelFocus::Name) ? g_nameCaret : g_ipCaret;
    HandleCaretKeys(curText, curCaret);
    for (uint32_t cp : TakeInputCodepoints()) {
        bool ok = (g_selFocus == SelFocus::Name) ? (cp >= 32 && cp != 127) : IsHostChar(cp);
        if (ok) InsertUtf8At(curText, curCaret, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(curText, curCaret);
    if (IsKeyPressed(KEY_DELETE)) DelCharAt(curText, curCaret);
    if (IsKeyPressed(KEY_TAB)) g_selFocus = (g_selFocus == SelFocus::Name) ? SelFocus::Ip : SelFocus::Name;
    if (IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) {
        if (g_selFocus == SelFocus::Name) {
            g_selFocus = SelFocus::Ip;
            g_ipCaret = ipInput.size();
        } else {
            DoStartClient();
            return;
        }
    }

    // 鼠标: 按钮 / 输入框点击聚焦定位光标
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
            g_nameCaret = CaretFromX(nameInput, UI_FONT_SIZE, mousePos.x, nameBox.x + 5);
        } else if (CheckCollisionPointRec(mousePos, ipBox)) {
            g_selFocus = SelFocus::Ip;
            g_ipCaret = CaretFromX(ipInput, UI_FONT_SIZE, mousePos.x, ipBox.x + 5);
        }
    }

    UpdateIBeamCursor({ nameBox, ipBox });

    BeginDrawing();
    ClearBackground(RAYWHITE);
    const char* title = "选择模式";
    int titleWidth = MeasureTextC(title, UI_FONT_SIZE);
    DrawTextC(title, (screenWidth - titleWidth) / 2.0f, titleY, UI_FONT_SIZE, DARKGRAY);

    DrawTextC("昵称（留空自动分配）：", margin, nameLabelY, UI_FONT_SIZE, DARKGRAY);
    DrawRectangleRec(nameBox, LIGHTGRAY);
    DrawRectangleLines((int)nameBox.x, (int)nameBox.y, (int)nameBox.width, (int)nameBox.height,
                       g_selFocus == SelFocus::Name ? DARKGRAY : GRAY);
    DrawFieldText(nameInput, g_nameCaret, nameBox.x + 5, nameBox.y + 5, BLACK, g_selFocus == SelFocus::Name);

    DrawTextC("服务器地址（支持 IPv4 / IPv6 / 主机名）：", margin, ipLabelY, UI_FONT_SIZE, DARKGRAY);
    DrawRectangleRec(ipBox, LIGHTGRAY);
    DrawRectangleLines((int)ipBox.x, (int)ipBox.y, (int)ipBox.width, (int)ipBox.height,
                       g_selFocus == SelFocus::Ip ? DARKGRAY : GRAY);
    DrawFieldText(ipInput, g_ipCaret, ipBox.x + 5, ipBox.y + 5, BLACK, g_selFocus == SelFocus::Ip);

    DrawRectangleRec(serverBtn, SKYBLUE);
    DrawTextCenteredInRect("启动服务器", serverBtn, UI_FONT_SIZE, BLACK);
    DrawRectangleRec(clientBtn, GREEN);
    DrawTextCenteredInRect("启动客户端", clientBtn, UI_FONT_SIZE, BLACK);
    EndDrawing();
}

// ---- 在线用户面板 ----
constexpr float PANEL_W = 200;
constexpr float PANEL_ENTRY_H = 25;

float PanelX(int screenWidth) { return (float)screenWidth - PANEL_W - 10; }

// 面板条目: id=0 为广播
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
    DrawTextC("在线用户", px, 10, UI_FONT_SIZE, DARKGRAY);
    if (!selfLabel.empty()) {
        DrawTextC(selfLabel.c_str(), px, 35, UI_FONT_SIZE - 4, GRAY);
    }
    auto entries = PanelEntries();
    float y = 65;
    for (const auto& e : entries) {
        Rectangle r = Rectangle{ px, y, PANEL_W - 10, PANEL_ENTRY_H };
        if (e.first == selectedId) {
            DrawRectangleRec(r, SKYBLUE);
            DrawTextC(e.second.c_str(), px + 8, y + 4, UI_FONT_SIZE - 2, BLACK);
        } else {
            DrawRectangleRec(r, LIGHTGRAY);
            DrawTextC(e.second.c_str(), px + 8, y + 4, UI_FONT_SIZE - 2, DARKGRAY);
        }
        y += PANEL_ENTRY_H + 2;
    }
    (void)screenHeight;
}

// ==================== 进度条 ====================
void DrawProgressBar(float x, float y, float w, float h, float frac, const std::string& label) {
    if (frac < 0) frac = 0;
    if (frac > 1) frac = 1;
    DrawRectangleRec(Rectangle{ x, y, w, h }, LIGHTGRAY);
    DrawRectangleRec(Rectangle{ x, y, w * frac, h }, SKYBLUE);
    DrawRectangleLines((int)x, (int)y, (int)w, (int)h, GRAY);
    DrawTextC(label.c_str(), x + 4, y + 1, 14, BLACK);
}

// 传输进度条(发送 + 接收, 绘制在输入区上方)
void DrawTransferBars(int screenWidth, int screenHeight) {
    float y = (float)screenHeight - 85;
    float w = PanelX(screenWidth) - 20;
    if (g_fileSend.active) {
        float frac = g_fileSend.size > 0 ? (float)g_fileSend.sent / (float)g_fileSend.size : 0.0f;
        std::string label = "发送中：" + g_fileSend.name + " " + std::to_string((int)(frac * 100)) + "%";
        DrawProgressBar(10, y, w, 18, frac, label);
        y -= 22;
    }
    int shown = 0;
    for (auto it = g_fileRecv.rbegin(); it != g_fileRecv.rend() && shown < 2; ++it) {
        float frac = it->second.size > 0 ? (float)it->second.received / (float)it->second.size : 0.0f;
        std::string label = "接收中：" + it->second.name + "（" + it->second.senderLabel + "）" + std::to_string((int)(frac * 100)) + "%";
        DrawProgressBar(10, y, w, 18, frac, label);
        y -= 22;
        ++shown;
    }
}

// ==================== 滚动消息区 ====================
constexpr int CHAT_LINE_H = 25;
constexpr int CHAT_TOP = 40;

// 绘制消息区(带滚动条/滚轮/拖拽/翻页) + 标题 + FPS
void DrawChatMessages(int screenWidth, int screenHeight, const char* title) {
    int maxMessagesY = screenHeight - 60;
    int chatW = (int)PanelX(screenWidth) - 20;
    int chatH = maxMessagesY - CHAT_TOP;
    if (chatH < CHAT_LINE_H) chatH = CHAT_LINE_H;
    int visible = chatH / CHAT_LINE_H;

    int total = 0;
    int maxOffset = 0;
    {
        std::lock_guard<std::mutex> lock(messagesMutex);
        total = (int)chatMessages.size();
        maxOffset = std::max(0, total - visible);
        g_chatScroll = std::max(0, std::min(g_chatScroll, maxOffset));
        int end = total - g_chatScroll;
        int start = std::max(0, end - visible);
        int y = CHAT_TOP;
        for (int i = start; i < end; ++i) {
            DrawTextC(chatMessages[i].c_str(), 10, (float)y, UI_FONT_SIZE, BLACK);
            y += CHAT_LINE_H;
        }
    }

    // 滚动条与拖拽
    if (total > visible) {
        int trackX = 10 + chatW - 12;
        int trackW = 10;
        int thumbH = std::max(20, chatH * visible / std::max(1, total));
        int thumbY = CHAT_TOP + (maxOffset > 0 ? (chatH - thumbH) * g_chatScroll / maxOffset : 0);
        DrawRectangle(trackX, CHAT_TOP, trackW, chatH, LIGHTGRAY);
        DrawRectangle(trackX, thumbY, trackW, thumbH, GRAY);
        Vector2 m = GetMousePosition();
        Rectangle track = Rectangle{ (float)trackX, (float)CHAT_TOP, (float)trackW, (float)chatH };
        if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON) && CheckCollisionPointRec(m, track)) {
            g_scrollDragging = true;
        }
        if (g_scrollDragging) {
            if (IsMouseButtonReleased(MOUSE_LEFT_BUTTON)) {
                g_scrollDragging = false;
            } else if (maxOffset > 0) {
                float frac = (m.y - CHAT_TOP - thumbH / 2.0f) / (float)(chatH - thumbH);
                g_chatScroll = std::max(0, std::min(maxOffset, (int)llroundf(frac * maxOffset)));
            }
        }
    }

    // 滚轮与翻页键
    int wheel = (int)GetMouseWheelMove();
    Vector2 m = GetMousePosition();
    if (wheel != 0 && m.x >= 10 && m.x <= 10 + chatW && m.y >= CHAT_TOP && m.y <= CHAT_TOP + chatH) {
        g_chatScroll = std::max(0, std::min(maxOffset, g_chatScroll - wheel * 3));
    }
    if (IsKeyPressed(KEY_PAGE_UP)) g_chatScroll = std::max(0, std::min(maxOffset, g_chatScroll + (visible - 1)));
    if (IsKeyPressed(KEY_PAGE_DOWN)) g_chatScroll = std::max(0, std::min(maxOffset, g_chatScroll - (visible - 1)));

    DrawTextC(title, 10, 10, UI_FONT_SIZE, DARKGRAY);
    std::string fps = "FPS: " + std::to_string(GetFPS());
    DrawTextC(fps.c_str(), PanelX(screenWidth) - 90, 10, 16, GRAY);
}

// 面板点击检测
int PanelHitTest(int screenWidth, Vector2 mousePos) {
    float px = PanelX(screenWidth);
    auto entries = PanelEntries();
    float y = 65;
    for (const auto& e : entries) {
        Rectangle r = Rectangle{ px, y, PANEL_W - 10, PANEL_ENTRY_H };
        if (CheckCollisionPointRec(mousePos, r)) return e.first;
        y += PANEL_ENTRY_H + 2;
    }
    return -1;
}

// ---- 服务器模式 ----
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
        g_clients.push_back(info);
        std::string ip = SockAddrToString((const sockaddr*)&clientAddr);
        AddMessage(info.name + " 已加入（" + ip + "）");
        LogEvent("CLIENT_JOIN " + std::to_string(info.id) + " " + ip);
        ServerSendToClient(info.id, "WELCOME|" + std::to_string(info.id) + "|" + info.name + "\n");
        ServerSendToClient(info.id, RosterLine() + "\n");
        ServerBroadcastOthers("JOIN|" + std::to_string(info.id) + "|" + info.name + "\n", info.sock);
    }

    // 发送队列刷出
    for (auto& c : g_clients) FlushSendQueue(c.sock, c.sendBuf);

    // 接收数据(行重组)
    for (size_t i = 0; i < g_clients.size(); ) {
        ClientInfo& c = g_clients[i];
        char buffer[BUFFER_SIZE];
        int n = recv(c.sock, buffer, sizeof(buffer), 0);
        if (n > 0) {
            c.recvBuf.append(buffer, (size_t)n);
            if (c.recvBuf.size() > MAX_LINE_LENGTH) c.recvBuf.clear();
            size_t pos;
            while ((pos = c.recvBuf.find('\n')) != std::string::npos) {
                std::string line = c.recvBuf.substr(0, pos);
                c.recvBuf.erase(0, pos + 1);
                TrimLineEnd(line);
                if (!line.empty()) ServerHandleLine(line, c);
            }
            ++i;
        } else if (n == 0) {
            // 断开
            CLOSE_SOCKET(c.sock);
            AddMessage(c.name + " 已离开");
            ServerBroadcastOthers("LEAVE|" + std::to_string(c.id) + "|" + c.name + "\n", c.sock);
            ServerBroadcastAll(RosterLine() + "\n");
            g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
        } else {
#ifdef _WIN32
            int err = WSAGetLastError();
            if (err == WSAEWOULDBLOCK) { ++i; }
#else
            int err = errno;
            if (err == EAGAIN || err == EWOULDBLOCK) { ++i; }
#endif
            else {
                CLOSE_SOCKET(c.sock);
                AddMessage(c.name + " 已离开");
                ServerBroadcastOthers("LEAVE|" + std::to_string(c.id) + "|" + c.name + "\n", c.sock);
                ServerBroadcastAll(RosterLine() + "\n");
                g_clients.erase(g_clients.begin() + (ptrdiff_t)i);
            }
        }
    }

    // 文件发送泵
    PumpFileSend();

    // 服务器输入(带插入光标)
    HandleCaretKeys(serverInput, g_serverCaret);
    for (uint32_t cp : TakeInputCodepoints()) {
        if (cp >= 32 && cp != 127) InsertUtf8At(serverInput, g_serverCaret, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(serverInput, g_serverCaret);
    if (IsKeyPressed(KEY_DELETE)) DelCharAt(serverInput, g_serverCaret);
    if ((IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) && !serverInput.empty()) {
        DoSendMessage(serverInput);
        serverInput.clear();
        g_serverCaret = 0;
    }

    // 面板点击 / 发送文件按钮 / 输入框光标定位
    if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
        Vector2 mousePos = GetMousePosition();
        int hit = PanelHitTest(screenWidth, mousePos);
        if (hit >= 0) g_serverTargetId = hit;
        Rectangle fileBtn = Rectangle{ (float)screenWidth - 160, (float)screenHeight - 45, 150, 32 };
        if (CheckCollisionPointRec(mousePos, fileBtn)) {
            if (g_serverTargetId <= 0) {
                AddMessage("请先在右侧选择接收用户，再发送文件");
            } else {
                std::wstring path;
                if (PickFile(path)) StartFileSend(path, g_serverTargetId);
            }
        }
        Rectangle inputBox = Rectangle{ 10, (float)screenHeight - 28, PanelX(screenWidth) - 20, 22 };
        if (CheckCollisionPointRec(mousePos, inputBox)) {
            g_serverCaret = CaretFromX(serverInput, UI_FONT_SIZE, mousePos.x, 15);
        }
    }

    UpdateIBeamCursor({ Rectangle{ 10, (float)screenHeight - 28, PanelX(screenWidth) - 20, 22 } });

    // 测试钩子: F8 发送测试文件(仅环境变量设置时生效)
    if (IsKeyPressed(KEY_F8)) {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE");
        if (tf && g_serverTargetId > 0) StartFileSend(AnsiToWide(tf), g_serverTargetId);
    }

    // 绘制
    BeginDrawing();
    ClearBackground(RAYWHITE);
    DrawChatMessages(screenWidth, screenHeight, "聊天服务器 - 消息记录：");
    DrawUserPanel(screenWidth, screenHeight, g_serverTargetId, "（服务器）");
    DrawTransferBars(screenWidth, screenHeight);
    DrawTextC(("发送到：" + TargetLabel(g_serverTargetId) + "（回车发送）").c_str(), 10, (float)(screenHeight - 40), UI_FONT_SIZE, DARKGRAY);
    DrawRectangle(10, screenHeight - 20, (int)PanelX(screenWidth) - 20, 1, LIGHTGRAY);
    DrawFieldText(serverInput, g_serverCaret, 10, (float)(screenHeight - 15), BLUE, true);
    Rectangle fileBtn = Rectangle{ (float)screenWidth - 160, (float)screenHeight - 45, 150, 32 };
    DrawRectangleRec(fileBtn, ORANGE);
    DrawTextCenteredInRect("发送文件…", fileBtn, UI_FONT_SIZE, BLACK);
    EndDrawing();
}

// ---- 客户端模式 ----
void UpdateClientFrame() {
    int screenWidth = GetScreenWidth();
    int screenHeight = GetScreenHeight();

    // 发送队列刷出
    FlushSendQueue(clientSock, g_clientSendBuf);

    // 接收(行重组)
    char buffer[BUFFER_SIZE];
    int n = recv(clientSock, buffer, sizeof(buffer), 0);
    if (n > 0) {
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
        AddMessage("与服务器的连接已断开");
        ShutdownClient();
        g_mode = AppMode::Select;
        SetWindowTitleC(L"选择模式", "Select Mode");
        return;
    } else {
#ifdef _WIN32
        int err = WSAGetLastError();
        if (err != WSAEWOULDBLOCK) {
            AddMessage("与服务器的连接已断开");
            ShutdownClient();
            g_mode = AppMode::Select;
            SetWindowTitleC(L"选择模式", "Select Mode");
            return;
        }
#else
        if (errno != EAGAIN && errno != EWOULDBLOCK) {
            AddMessage("与服务器的连接已断开");
            ShutdownClient();
            g_mode = AppMode::Select;
            SetWindowTitleC(L"选择模式", "Select Mode");
            return;
        }
#endif
    }

    // 文件发送泵
    PumpFileSend();

    // 输入(带插入光标)
    HandleCaretKeys(clientInput, g_clientCaret);
    for (uint32_t cp : TakeInputCodepoints()) {
        if (cp >= 32 && cp != 127) InsertUtf8At(clientInput, g_clientCaret, cp);
    }
    if (IsKeyPressed(KEY_BACKSPACE)) DelCharBefore(clientInput, g_clientCaret);
    if (IsKeyPressed(KEY_DELETE)) DelCharAt(clientInput, g_clientCaret);
    if ((IsKeyPressed(KEY_ENTER) || IsKeyPressed(KEY_KP_ENTER)) && !clientInput.empty()) {
        DoSendMessage(clientInput);
        clientInput.clear();
        g_clientCaret = 0;
    }

    // 面板点击 / 发送文件按钮 / 输入框光标定位
    if (IsMouseButtonPressed(MOUSE_LEFT_BUTTON)) {
        Vector2 mousePos = GetMousePosition();
        int hit = PanelHitTest(screenWidth, mousePos);
        if (hit >= 0) g_clientTargetId = hit;
        Rectangle fileBtn = Rectangle{ (float)screenWidth - 160, (float)screenHeight - 45, 150, 32 };
        if (CheckCollisionPointRec(mousePos, fileBtn)) {
            std::wstring path;
            if (PickFile(path)) StartFileSend(path, g_clientTargetId);
        }
        Rectangle inputBox = Rectangle{ 10, (float)screenHeight - 28, PanelX(screenWidth) - 20, 22 };
        if (CheckCollisionPointRec(mousePos, inputBox)) {
            g_clientCaret = CaretFromX(clientInput, UI_FONT_SIZE, mousePos.x, 15);
        }
    }

    UpdateIBeamCursor({ Rectangle{ 10, (float)screenHeight - 28, PanelX(screenWidth) - 20, 22 } });

    // 测试钩子: F8 发送测试文件
    if (IsKeyPressed(KEY_F8)) {
        const char* tf = std::getenv("TCPCHAT_TEST_FILE");
        if (tf) StartFileSend(AnsiToWide(tf), g_clientTargetId);
    }

    // 绘制
    BeginDrawing();
    ClearBackground(RAYWHITE);
    DrawChatMessages(screenWidth, screenHeight, "聊天客户端 - 消息记录：");
    std::string selfLabel = "（我是 " + (g_myName.empty() ? "?" : g_myName) + "）";
    DrawUserPanel(screenWidth, screenHeight, g_clientTargetId, selfLabel);
    DrawTransferBars(screenWidth, screenHeight);
    DrawTextC(("发送到：" + TargetLabel(g_clientTargetId) + "（回车发送）").c_str(), 10, (float)(screenHeight - 40), UI_FONT_SIZE, DARKGRAY);
    DrawRectangle(10, screenHeight - 20, (int)PanelX(screenWidth) - 20, 1, LIGHTGRAY);
    DrawFieldText(clientInput, g_clientCaret, 10, (float)(screenHeight - 15), BLUE, true);
    Rectangle fileBtn = Rectangle{ (float)screenWidth - 160, (float)screenHeight - 45, 150, 32 };
    DrawRectangleRec(fileBtn, ORANGE);
    DrawTextCenteredInRect("发送文件…", fileBtn, UI_FONT_SIZE, BLACK);
    EndDrawing();
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

// ==================== 主函数 ====================
int main() {
    if (!InitNetwork()) {
        return 1;
    }

    // 测试钩子: 预设服务器地址 / 昵称
    const char* testIp = std::getenv("TCPCHAT_TEST_IP");
    if (testIp && *testIp) ipInput = testIp;
    const char* testName = std::getenv("TCPCHAT_TEST_NAME");
    if (testName && *testName) {
        nameInput = testName;
        g_nameCaret = nameInput.size();
    }

    SetConfigFlags(FLAG_WINDOW_RESIZABLE);
    InitWindow(DEFAULT_SCREEN_WIDTH, DEFAULT_SCREEN_HEIGHT, "TCP Chat");
    // 目标帧率自动跟随当前显示器的刷新率
    int refresh = GetMonitorRefreshRate(GetCurrentMonitor());
    SetTargetFPS(refresh > 0 ? refresh : 60);

    g_font = LoadCjkFont();
#ifdef _WIN32
    InstallInputHook();
#endif
    SetWindowTitleC(L"选择模式", "Select Mode");

    InitAutoTest();
    while (!WindowShouldClose()) {
        ProcessAutoTest();
        switch (g_mode) {
            case AppMode::Select: UpdateSelectFrame(); break;
            case AppMode::Server:  UpdateServerFrame();  break;
            case AppMode::Client:  UpdateClientFrame();  break;
        }
    }

    // 清理
    if (g_mode == AppMode::Server) ShutdownServer();
    if (g_mode == AppMode::Client) ShutdownClient();
    if (g_fileSend.active && g_fileSend.f) fclose(g_fileSend.f);
    for (auto& kv : g_fileRecv) if (kv.second.f) fclose(kv.second.f);
    WriteDebugDump();
#ifdef _WIN32
    RemoveInputHook();
#endif
    if (g_fontLoaded) UnloadFont(g_font);
    CloseWindow();
    CleanupNetwork();
    return 0;
}
