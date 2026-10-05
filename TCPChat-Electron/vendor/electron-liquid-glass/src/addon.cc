// electron-liquid-glass native entry (Windows): N-API bindings + session lifecycle
#include <napi.h>
#include <windows.h>

#include <chrono>
#include <condition_variable>
#include <mutex>
#include <utility>
#include <vector>

#include "capture.h"
#include "glass_debug.h"
#include "d3d_utils.h"
#include "panel.h"
#include "session.h"
#include "stats.h"

namespace {

DWORD GetWindowsBuildNumber() {
    // GetVersionEx is affected by compatibility manifests; RtlGetVersion
    // always reports the real version
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return 0;
    auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(ntdll, "RtlGetVersion"));
    if (!rtlGetVersion) return 0;
    RTL_OSVERSIONINFOW info{};
    info.dwOSVersionInfoSize = sizeof(info);
    if (rtlGetVersion(&info) != 0) return 0;
    return info.dwBuildNumber;
}

// WDA_EXCLUDEFROMCAPTURE (prevents self-capture feedback loops) requires
// Win10 2004 (build 19041)+
Napi::Value IsSupported(const Napi::CallbackInfo& info) {
    return Napi::Boolean::New(info.Env(), GetWindowsBuildNumber() >= 19041);
}

Napi::Value OsBuild(const Napi::CallbackInfo& info) {
    return Napi::Number::New(info.Env(), static_cast<double>(GetWindowsBuildNumber()));
}

// —— Argument parsing helpers ——

LONG GetInt(Napi::Object obj, const char* key, LONG fallback = 0) {
    Napi::Value v = obj.Get(key);
    return v.IsNumber() ? v.As<Napi::Number>().Int32Value() : fallback;
}

float GetFloat(Napi::Object obj, const char* key, float fallback) {
    Napi::Value v = obj.Get(key);
    return v.IsNumber() ? v.As<Napi::Number>().FloatValue() : fallback;
}

bool GetBool(Napi::Object obj, const char* key, bool fallback) {
    Napi::Value v = obj.Get(key);
    return v.IsBoolean() ? v.As<Napi::Boolean>().Value() : fallback;
}

RECT BoundsFromObject(Napi::Object obj) {
    const LONG x = GetInt(obj, "x");
    const LONG y = GetInt(obj, "y");
    return RECT{ x, y, x + GetInt(obj, "width"), y + GetInt(obj, "height") };
}

GlassParams ParamsFromObject(Napi::Object obj, const GlassParams& base) {
    GlassParams p = base;
    p.cornerRadius = GetFloat(obj, "cornerRadius", p.cornerRadius);
    p.displacementScale = GetFloat(obj, "displacementScale", p.displacementScale);
    p.aberrationIntensity = GetFloat(obj, "aberrationIntensity", p.aberrationIntensity);
    p.saturation = GetFloat(obj, "saturation", p.saturation);
    p.sourceOffsetX = GetFloat(obj, "sourceOffsetX", p.sourceOffsetX);
    p.sourceOffsetY = GetFloat(obj, "sourceOffsetY", p.sourceOffsetY);
    return p;
}

HWND HwndFromValue(Napi::Value value) {
    if (!value.IsBuffer()) return nullptr;
    auto buffer = value.As<Napi::Buffer<uint8_t>>();
    if (buffer.Length() < sizeof(void*)) return nullptr;
    HWND hwnd = nullptr;
    memcpy(&hwnd, buffer.Data(), sizeof(void*));
    return hwnd;
}

std::vector<LumaBand> BandsFromValue(Napi::Value value) {
    std::vector<LumaBand> bands;
    if (!value.IsArray()) return bands;
    auto arr = value.As<Napi::Array>();
    for (uint32_t i = 0; i < arr.Length(); i++) {
        Napi::Value item = arr.Get(i);
        if (!item.IsObject()) continue;
        auto obj = item.As<Napi::Object>();
        LumaBand band;
        band.id = GetInt(obj, "id", static_cast<LONG>(i));
        band.rect = BoundsFromObject(obj);
        bands.push_back(band);
    }
    return bands;
}

// —— Panel API ——


// —— Capture policy ——
//
// Two different Windows mechanisms, and the difference matters:
//
//   all       SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)
//             The window disappears from every capture path — DXGI Desktop
//             Duplication *and* monitor-level WGC/GDI. Screenshots of the app
//             come out with a hole where the window is.
//
//   dda-only  SetWindowCaptureAffinity(WCA_EXCLUDED_FROM_DDA)
//             Windows 11 24H2+. The window is dropped from this module's DXGI
//             input only, so the glass stops capturing itself, while ordinary
//             screenshots and recordings still show the app.
//
// SetWindowCaptureAffinity is resolved at runtime: it does not exist before
// Win11 24H2, and a missing export must not stop the addon from loading.

enum CapturePolicy { kPolicyAll = 0, kPolicyDdaOnly = 1, kPolicyNone = 2 };

#ifndef WCA_EXCLUDED_FROM_DDA
#define WCA_EXCLUDED_FROM_DDA 0x00000002
#endif

using SetWindowCaptureAffinityFn = BOOL(WINAPI*)(HWND, int);

SetWindowCaptureAffinityFn ResolveCaptureAffinity() {
    static SetWindowCaptureAffinityFn fn = [] () -> SetWindowCaptureAffinityFn {
        HMODULE user32 = GetModuleHandleW(L"user32.dll");
        if (!user32) return nullptr;
        return reinterpret_cast<SetWindowCaptureAffinityFn>(
            GetProcAddress(user32, "SetWindowCaptureAffinity"));
    }();
    return fn;
}

// Returns false when the requested policy could not be applied. `dda-only` on
// an OS without the API fails closed: the caller should fall back to `all`
// rather than leave the panel feeding on itself.
bool ApplyCapturePolicy(HWND hwnd, CapturePolicy policy) {
    if (!hwnd || !IsWindow(hwnd)) return false;
    switch (policy) {
        case kPolicyNone:
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
            if (auto fn = ResolveCaptureAffinity()) fn(hwnd, 0);
            return true;
        case kPolicyDdaOnly:
            if (auto fn = ResolveCaptureAffinity()) {
                if (fn(hwnd, WCA_EXCLUDED_FROM_DDA)) return true;
            }
            // No selective exclusion here — fail closed.
            return false;
        case kPolicyAll:
        default:
            return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) != FALSE;
    }
}

CapturePolicy PolicyFromString(const std::string& s) {
    if (s == "dda-only") return kPolicyDdaOnly;
    if (s == "none") return kPolicyNone;
    return kPolicyAll;
}

Napi::Value SetWindowCapturePolicy(const Napi::CallbackInfo& info) {
    HWND hwnd = HwndFromValue(info[0]);
    std::string policy = info.Length() > 1 && info[1].IsString()
                             ? info[1].As<Napi::String>().Utf8Value()
                             : std::string("all");
    return Napi::Boolean::New(info.Env(), ApplyCapturePolicy(hwnd, PolicyFromString(policy)));
}
// Read back what the window's display affinity actually is. Applying a policy
// and assuming it stuck is how "the exclusion is on but the window is still
// captured" goes unnoticed.
Napi::Value GetWindowCapturePolicy(const Napi::CallbackInfo& info) {
    HWND hwnd = HwndFromValue(info[0]);
    if (!hwnd || !IsWindow(hwnd)) return Napi::String::New(info.Env(), "invalid");
    DWORD affinity = 0;
    if (!GetWindowDisplayAffinity(hwnd, &affinity)) {
        return Napi::String::New(info.Env(), "unreadable");
    }
    switch (affinity) {
        case WDA_NONE: return Napi::String::New(info.Env(), "none");
        case WDA_MONITOR: return Napi::String::New(info.Env(), "monitor");
        case WDA_EXCLUDEFROMCAPTURE: return Napi::String::New(info.Env(), "all");
        default: return Napi::String::New(info.Env(), "unknown");
    }
}
Napi::Value CreatePanel(const Napi::CallbackInfo& info) {
    auto opts = info[0].As<Napi::Object>();
    PanelConfig config;
    config.bounds = BoundsFromObject(opts);
    config.params = ParamsFromObject(opts, GlassParams{});
    config.dpr = GetFloat(opts, "dpr", 1.0f);
    // `capturePolicy` wins; `excludeFromCapture` stays as the deprecated
    // boolean form (true -> "all", false -> "none").
    if (opts.Get("capturePolicy").IsString()) {
        config.excludeFromCapture =
            PolicyFromString(opts.Get("capturePolicy").As<Napi::String>().Utf8Value()) != kPolicyNone;
    } else {
        config.excludeFromCapture = GetBool(opts, "excludeFromCapture", true);
    }
    config.anchor = HwndFromValue(opts.Get("anchorHwnd"));
    const int id = GlassSession::Instance().CreatePanel(config);
    return Napi::Number::New(info.Env(), id);
}

Napi::Value DestroyPanel(const Napi::CallbackInfo& info) {
    GlassSession::Instance().DestroyPanel(info[0].As<Napi::Number>().Int32Value());
    return info.Env().Undefined();
}

Napi::Value ShowPanel(const Napi::CallbackInfo& info) {
    if (glassdbg::enabled()) {
        std::fprintf(stderr, "[glass-probe] ShowPanel called argc=%zu arg0=%s arg1=%s\n", info.Length(),
                     info.Length() > 0 ? info[0].ToString().Utf8Value().c_str() : "(none)",
                     info.Length() > 1 ? info[1].ToString().Utf8Value().c_str() : "(none)");
        std::fflush(stderr);
    }
    const int id = info[0].As<Napi::Number>().Int32Value();
    const UINT fadeMs = info.Length() > 1 ? info[1].As<Napi::Number>().Uint32Value() : 120;
    GlassSession::Instance().ShowPanel(id, fadeMs);
    return info.Env().Undefined();
}

Napi::Value HidePanel(const Napi::CallbackInfo& info) {
    const int id = info[0].As<Napi::Number>().Int32Value();
    const UINT fadeMs = info.Length() > 1 ? info[1].As<Napi::Number>().Uint32Value() : 100;
    GlassSession::Instance().HidePanel(id, fadeMs);
    return info.Env().Undefined();
}

Napi::Value SetPanelBounds(const Napi::CallbackInfo& info) {
    const int id = info[0].As<Napi::Number>().Int32Value();
    GlassSession::Instance().SetPanelBounds(id, BoundsFromObject(info[1].As<Napi::Object>()));
    return info.Env().Undefined();
}

Napi::Value SetPanelParams(const Napi::CallbackInfo& info) {
    const int id = info[0].As<Napi::Number>().Int32Value();
    // The JS side maintains the full params object (JS guarantees omitted
    // fields keep their default-value semantics)
    GlassSession::Instance().SetPanelParams(
        id, ParamsFromObject(info[1].As<Napi::Object>(), GlassParams{}));
    return info.Env().Undefined();
}

Napi::Value AnchorPanel(const Napi::CallbackInfo& info) {
    const int id = info[0].As<Napi::Number>().Int32Value();
    GlassSession::Instance().AnchorPanel(id, HwndFromValue(info[1]));
    return info.Env().Undefined();
}

Napi::Value SetLumaBands(const Napi::CallbackInfo& info) {
    const int id = info[0].As<Napi::Number>().Int32Value();
    GlassSession::Instance().SetLumaBands(id, BandsFromValue(info[1]));
    return info.Env().Undefined();
}

// —— Luma callback (worker thread → JS main thread via TSFN) ——

using LumaPayload = std::pair<int, std::vector<LumaBandStats>>;
Napi::ThreadSafeFunction g_lumaTsfn;

Napi::Value SetLumaCallback(const Napi::CallbackInfo& info) {
    if (g_lumaTsfn) {
        g_lumaTsfn.Release();
        g_lumaTsfn = Napi::ThreadSafeFunction();
        GlassSession::Instance().SetLumaCallback(nullptr);
    }
    if (info.Length() > 0 && info[0].IsFunction()) {
        g_lumaTsfn = Napi::ThreadSafeFunction::New(
            info.Env(), info[0].As<Napi::Function>(), "liquid-glass-luma", 4, 1);
        g_lumaTsfn.Unref(info.Env());  // don't keep the process alive
        GlassSession::Instance().SetLumaCallback([](int panelId, std::vector<LumaBandStats> bands) {
            if (!g_lumaTsfn) return;
            auto* payload = new LumaPayload(panelId, std::move(bands));
            // Sampling runs at ~60Hz max: when JS is busy the queue
            // (capacity 4) may reject, and the payload must be reclaimed.
            // Only intermediate samples are dropped — the freshest one
            // arrives with the next repaint.
            const napi_status status = g_lumaTsfn.NonBlockingCall(
                payload, [](Napi::Env env, Napi::Function cb, LumaPayload* data) {
                    Napi::Object bands = Napi::Object::New(env);
                    for (const LumaBandStats& s : data->second) {
                        Napi::Object stat = Napi::Object::New(env);
                        stat.Set("r", Napi::Number::New(env, s.r));
                        stat.Set("g", Napi::Number::New(env, s.g));
                        stat.Set("b", Napi::Number::New(env, s.b));
                        stat.Set("darkTail", Napi::Number::New(env, s.darkTail));
                        stat.Set("lightTail", Napi::Number::New(env, s.lightTail));
                        bands.Set(std::to_string(s.id), stat);
                    }
                    cb.Call({ Napi::Number::New(env, data->first), bands });
                    delete data;
                });
            if (status != napi_ok) delete payload;
        });
    }
    return info.Env().Undefined();
}

Napi::Value ShutdownSession(const Napi::CallbackInfo& info) {
    GlassSession::Instance().SetLumaCallback(nullptr);
    GlassSession::Instance().Shutdown();
    if (g_lumaTsfn) {
        g_lumaTsfn.Release();
        g_lumaTsfn = Napi::ThreadSafeFunction();
    }
    return info.Env().Undefined();
}

// Snapshot of internal performance counters (diagnostics/benchmarks);
// reset=true clears them after reading
Napi::Value Stats(const Napi::CallbackInfo& info) {
    Napi::Env env = info.Env();
    GlassStats& s = GlassStats::Instance();
    Napi::Object result = Napi::Object::New(env);
    result.Set("loopIterations", Napi::Number::New(env, static_cast<double>(s.loopIterations.load())));
    result.Set("framesAcquired", Napi::Number::New(env, static_cast<double>(s.framesAcquired.load())));
    result.Set("cacheCopies", Napi::Number::New(env, static_cast<double>(s.cacheCopies.load())));
    result.Set("cacheCopyBytes", Napi::Number::New(env, static_cast<double>(s.cacheCopyBytes.load())));
    result.Set("renders", Napi::Number::New(env, static_cast<double>(s.renders.load())));
    result.Set("lumaSamples", Napi::Number::New(env, static_cast<double>(s.lumaSamples.load())));
    result.Set("lumaMapWaitUs", Napi::Number::New(env, static_cast<double>(s.lumaMapWaitUs.load())));
    result.Set("lumaCpuUs", Napi::Number::New(env, static_cast<double>(s.lumaCpuUs.load())));
    if (info.Length() > 0 && info[0].IsBoolean() && info[0].As<Napi::Boolean>().Value()) {
        s.Reset();
    }
    return result;
}
}  // namespace

/*
 * Read a panel's back buffer and hand it back as {ok, width, height, pixels}.
 *
 * Blocking, on purpose. The readback itself runs on the worker thread, so this
 * waits for it — a few milliseconds for one panel, once, when someone asks for a
 * screenshot. The alternative is a ThreadSafeFunction and a callback crossing
 * threads, and the lifetime rules there are a great deal of machinery to buy a
 * few milliseconds back on an action nobody performs twice a second.
 *
 * The wait is bounded so a stalled worker returns nothing rather than hanging the
 * caller forever.
 */
Napi::Value ReadPanel(const Napi::CallbackInfo& info) {
    Napi::Env env = info.Env();
    if (info.Length() < 1 || !info[0].IsNumber()) {
        Napi::TypeError::New(env, "readPanel(id)").ThrowAsJavaScriptException();
        return env.Undefined();
    }
    const int id = info[0].As<Napi::Number>().Int32Value();

    std::mutex mutex;
    std::condition_variable cv;
    bool done = false;
    bool ok = false;
    std::vector<unsigned char> pixels;
    UINT width = 0;
    UINT height = 0;

    GlassSession::Instance().ReadPanel(
        id, [&](bool readOk, std::vector<unsigned char> readPixels, UINT w, UINT h) {
            std::lock_guard<std::mutex> lock(mutex);
            ok = readOk;
            pixels = std::move(readPixels);
            width = w;
            height = h;
            done = true;
            cv.notify_one();
        });

    {
        std::unique_lock<std::mutex> lock(mutex);
        cv.wait_for(lock, std::chrono::seconds(5), [&] { return done; });
    }

    if (!done) {
        if (glassdbg::enabled()) {
            std::fprintf(stderr, "[glass-probe] ReadPanel(id=%d) timed out\n", id);
            std::fflush(stderr);
        }
        return env.Null();
    }

    Napi::Object out = Napi::Object::New(env);
    out.Set("ok", Napi::Boolean::New(env, ok));
    out.Set("width", Napi::Number::New(env, width));
    out.Set("height", Napi::Number::New(env, height));
    if (ok && !pixels.empty()) {
        out.Set("pixels",
                Napi::Buffer<unsigned char>::Copy(env, pixels.data(), pixels.size()));
    }
    return out;
}

Napi::Object Init(Napi::Env env, Napi::Object exports) {
    exports.Set("isSupported", Napi::Function::New(env, IsSupported));
    exports.Set("osBuild", Napi::Function::New(env, OsBuild));
    exports.Set("createPanel", Napi::Function::New(env, CreatePanel));
    exports.Set("destroyPanel", Napi::Function::New(env, DestroyPanel));
    exports.Set("showPanel", Napi::Function::New(env, ShowPanel));
    exports.Set("hidePanel", Napi::Function::New(env, HidePanel));
    exports.Set("setPanelBounds", Napi::Function::New(env, SetPanelBounds));
    exports.Set("setPanelParams", Napi::Function::New(env, SetPanelParams));
    exports.Set("anchorPanel", Napi::Function::New(env, AnchorPanel));
    exports.Set("readPanel", Napi::Function::New(env, ReadPanel));
    exports.Set("setLumaBands", Napi::Function::New(env, SetLumaBands));
    exports.Set("setLumaCallback", Napi::Function::New(env, SetLumaCallback));
    exports.Set("setWindowCapturePolicy", Napi::Function::New(env, SetWindowCapturePolicy));
    exports.Set("getWindowCapturePolicy", Napi::Function::New(env, GetWindowCapturePolicy));
    exports.Set("shutdown", Napi::Function::New(env, ShutdownSession));
    exports.Set("_stats", Napi::Function::New(env, Stats));

    // Join the worker thread before process exit so it isn't killed while
    // holding windows/devices
    env.AddCleanupHook([] { GlassSession::Instance().Shutdown(); });
    return exports;
}

NODE_API_MODULE(liquid_glass, Init)
