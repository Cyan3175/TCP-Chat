#include "panel.h"

#include "glass_debug.h"

#include <dxgi1_3.h>

namespace {

constexpr wchar_t kClassName[] = L"ElectronLiquidGlassPanel";

LRESULT CALLBACK PanelWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    // Hit-test straight through: the panel never takes mouse interaction
    if (msg == WM_NCHITTEST) return HTTRANSPARENT;
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void EnsureWindowClass() {
    static bool registered = false;
    if (registered) return;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = PanelWndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kClassName;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassExW(&wc);
    registered = true;
}

}  // namespace

GlassPanel::~GlassPanel() {
    rtv_.Reset();
    swapchain_.Reset();
    dcompVisual_.Reset();
    dcompTarget_.Reset();
    dcompDevice_.Reset();
    if (hwnd_) {
        DestroyWindow(hwnd_);
        hwnd_ = nullptr;
    }
}

HRESULT GlassPanel::Create(ID3D11Device* device, const RECT& bounds, const GlassParams& params,
                           bool excludeFromCapture) {
    EnsureWindowClass();
    device_ = device;
    bounds_ = bounds;
    params_ = params;

    // WS_EX_NOREDIRECTIONBITMAP: content comes entirely from the DComp
    // composition chain; no GDI surface exists
    hwnd_ = CreateWindowExW(
        WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
        kClassName, L"", WS_POPUP,
        bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!hwnd_) {
        if (glassdbg::enabled()) {
            std::fprintf(stderr, "[glass-probe] CreateWindowExW failed err=%lu bounds=(%ld,%ld,%ld,%ld)\n",
                         GetLastError(), bounds.left, bounds.top, bounds.right, bounds.bottom);
            std::fflush(stderr);
        }
        return HRESULT_FROM_WIN32(GetLastError());
    }
    if (glassdbg::enabled()) {
        std::fprintf(stderr, "[glass-probe] panel window created hwnd=%p bounds=(%ld,%ld,%ld,%ld) exclude=%d\n",
                     static_cast<void*>(hwnd_), bounds.left, bounds.top, bounds.right, bounds.bottom,
                     excludeFromCapture ? 1 : 0);
        std::fflush(stderr);
    }

    if (excludeFromCapture) SetExcludeFromCapture(true);

    ComPtr<IDXGIDevice> dxgiDevice;
    HRESULT hr = device_->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    if (FAILED(hr)) return hr;
    hr = DCompositionCreateDevice(dxgiDevice.Get(), IID_PPV_ARGS(&dcompDevice_));
    if (FAILED(hr)) return hr;
    hr = dcompDevice_->CreateTargetForHwnd(hwnd_, TRUE, &dcompTarget_);
    if (FAILED(hr)) return hr;
    hr = dcompDevice_->CreateVisual(&dcompVisual_);
    if (FAILED(hr)) return hr;

    hr = EnsureSwapchain(device, bounds.right - bounds.left, bounds.bottom - bounds.top);
    if (FAILED(hr)) return hr;

    hr = dcompVisual_->SetContent(swapchain_.Get());
    if (FAILED(hr)) return hr;
    hr = dcompTarget_->SetRoot(dcompVisual_.Get());
    if (FAILED(hr)) return hr;
    hr = dcompDevice_->Commit();
    if (FAILED(hr)) return hr;

    // Clear both flip buffers to fully transparent before showing: otherwise
    // DComp composites uninitialized VRAM garbage (magenta/cyan blocks) until
    // the first content frame arrives
    ComPtr<ID3D11DeviceContext> ctx;
    device_->GetImmediateContext(&ctx);
    const float transparent[4] = { 0, 0, 0, 0 };
    for (int i = 0; i < 2; i++) {
        ID3D11RenderTargetView* rtv = AcquireBackBuffer();
        if (!rtv) break;
        ctx->ClearRenderTargetView(rtv, transparent);
        Present();
    }
    return S_OK;
}

HRESULT GlassPanel::EnsureSwapchain(ID3D11Device* device, UINT width, UINT height) {
    width = width ? width : 1;
    height = height ? height : 1;
    if (swapchain_ && width == swapW_ && height == swapH_) return S_OK;

    rtv_.Reset();
    if (swapchain_) {
        HRESULT hr = swapchain_->ResizeBuffers(2, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, 0);
        if (FAILED(hr)) return hr;
    } else {
        ComPtr<IDXGIDevice> dxgiDevice;
        HRESULT hr = device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
        if (FAILED(hr)) return hr;
        ComPtr<IDXGIAdapter> adapter;
        hr = dxgiDevice->GetAdapter(&adapter);
        if (FAILED(hr)) return hr;
        ComPtr<IDXGIFactory2> factory;
        hr = adapter->GetParent(IID_PPV_ARGS(&factory));
        if (FAILED(hr)) return hr;

        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = width;
        desc.Height = height;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        hr = factory->CreateSwapChainForComposition(device, &desc, nullptr, &swapchain_);
        if (FAILED(hr)) return hr;
    }
    swapW_ = width;
    swapH_ = height;
    return S_OK;
}

void GlassPanel::SetBounds(const RECT& bounds) {
    SetBoundsDeferred(bounds);
    ApplyWindowPosition();
}

void GlassPanel::SetBoundsDeferred(const RECT& bounds) {
    bounds_ = bounds;
    // The swapchain has to be the right size *before* the render pass writes
    // into it, so the resize stays here; only the window move is deferred.
    if (hwnd_) {
        EnsureSwapchain(device_.Get(), bounds.right - bounds.left, bounds.bottom - bounds.top);
    }
}

void GlassPanel::ApplyWindowPosition() {
    if (!hwnd_) return;
    SetWindowPos(hwnd_, nullptr, bounds_.left, bounds_.top,
                 bounds_.right - bounds_.left, bounds_.bottom - bounds_.top,
                 SWP_NOZORDER | SWP_NOACTIVATE);
}

void GlassPanel::Show() {
    if (glassdbg::enabled()) {
        std::fprintf(stderr, "[glass-probe] Show() hwnd=%p visible=%d\n",
                     static_cast<void*>(hwnd_), visible_ ? 1 : 0);
        std::fflush(stderr);
    }
    if (!hwnd_ || visible_) return;
    ShowWindow(hwnd_, SW_SHOWNOACTIVATE);
    visible_ = true;
}

void GlassPanel::Hide() {
    if (!hwnd_ || !visible_) return;
    ShowWindow(hwnd_, SW_HIDE);
    visible_ = false;
    opacity_ = 0.0f;
    fadeTarget_ = 0.0f;
}

namespace {

// Is `window` somewhere below `anchor` in z-order?
//
// The session re-asserts anchoring twice a second as a defence against other
// topmost windows cutting in. Doing that unconditionally is expensive in a way
// that is easy to miss: SetWindowPos pulls the window out of the z-order and
// puts it back, which makes DWM recompose it. One panel hides that; with a
// panel per message bubble it becomes a permanent flicker with nothing on
// screen to explain it.
//
// Walking up from the panel is the right test for several panels sharing one
// anchor, where only the topmost of them is *directly* below it and an
// "immediately above" check would churn the rest forever.
bool IsBelowInZOrder(HWND window, HWND anchor) {
    for (HWND w = GetWindow(window, GW_HWNDPREV); w; w = GetWindow(w, GW_HWNDPREV)) {
        if (w == anchor) return true;
    }
    return false;
}

}  // namespace

void GlassPanel::AnchorBelow(HWND anchor) {
    if (!hwnd_) return;
    if (anchor && IsWindow(anchor)) {
        // insertAfter=anchor: the panel sits immediately below the anchor window
        if (IsBelowInZOrder(hwnd_, anchor)) return;  // already where it belongs
        SetWindowPos(hwnd_, anchor, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    } else {
        SetWindowPos(hwnd_, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}

void GlassPanel::SetExcludeFromCapture(bool exclude) {
    if (!hwnd_) return;
    SetWindowDisplayAffinity(hwnd_, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
}

bool GlassPanel::FadeStep() {
    if (opacity_ < fadeTarget_) {
        opacity_ = opacity_ + fadeStep_ > fadeTarget_ ? fadeTarget_ : opacity_ + fadeStep_;
        return opacity_ < fadeTarget_;
    }
    if (opacity_ > fadeTarget_) {
        opacity_ = opacity_ - fadeStep_ < fadeTarget_ ? fadeTarget_ : opacity_ - fadeStep_;
        return opacity_ > fadeTarget_;
    }
    return false;
}

ID3D11RenderTargetView* GlassPanel::AcquireBackBuffer() {
    if (!swapchain_) return nullptr;
    if (!rtv_) {
        ComPtr<ID3D11Texture2D> backBuffer;
        if (FAILED(swapchain_->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) return nullptr;
        if (FAILED(device_->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv_))) return nullptr;
    }
    return rtv_.Get();
}

void GlassPanel::Present() {
    if (!swapchain_) return;
    // Present(0): queue immediately without waiting for vblank; DWM picks up
    // the latest at composition time
    swapchain_->Present(0, 0);
    // With the FLIP model the RTV is invalid after Present; re-acquire next frame
    rtv_.Reset();
}

HRESULT GlassPanel::ReadBack(ID3D11DeviceContext* ctx, std::vector<unsigned char>* out,
                             UINT* width, UINT* height) {
    if (!swapchain_ || !device_ || !ctx || !out) return E_FAIL;

    ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(swapchain_->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) return E_FAIL;

    D3D11_TEXTURE2D_DESC desc{};
    backBuffer->GetDesc(&desc);

    // The same staging arrangement glass_debug uses to dump this texture: this
    // is that path with the pixels handed back rather than written to a BMP.
    D3D11_TEXTURE2D_DESC sd = desc;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    sd.MipLevels = 1;
    sd.ArraySize = 1;
    sd.SampleDesc.Count = 1;
    sd.SampleDesc.Quality = 0;

    ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device_->CreateTexture2D(&sd, nullptr, staging.GetAddressOf()))) return E_FAIL;
    ctx->CopyResource(staging.Get(), backBuffer.Get());

    D3D11_MAPPED_SUBRESOURCE map{};
    if (FAILED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map))) return E_FAIL;

    const UINT w = desc.Width;
    const UINT h = desc.Height;
    const size_t rowBytes = static_cast<size_t>(w) * 4;
    out->resize(rowBytes * static_cast<size_t>(h));
    for (UINT y = 0; y < h; ++y) {
        const auto* row = static_cast<const unsigned char*>(map.pData) +
                          static_cast<size_t>(y) * map.RowPitch;
        std::memcpy(out->data() + static_cast<size_t>(y) * rowBytes, row, rowBytes);
    }
    ctx->Unmap(staging.Get(), 0);

    if (width) *width = w;
    if (height) *height = h;
    return S_OK;
}
