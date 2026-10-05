// Glass panel: a popup window with no redirection bitmap + a DirectComposition
// premultiplied-alpha swapchain. The window is fully click-through, never
// steals focus, stays out of the taskbar, and can optionally be excluded from
// screen capture (prevents self-capture feedback loops).
// All methods must be called on the worker thread (the window belongs to the
// message queue of its creating thread).
#pragma once
#include <d3d11.h>
#include <dcomp.h>
#include <dxgi1_2.h>
#include <windows.h>
#include <wrl/client.h>

#include <vector>

using Microsoft::WRL::ComPtr;

struct GlassParams {
    float cornerRadius = 20.0f;       // physical pixels
    /*
     * Always zero, and kept only so the renderer's blur passes still compile.
     *
     * The blur was removed from the application — the setting, the slider and the
     * value passed down are all gone — so nothing sets this any more, the renderer
     * takes its sharp path, and the lens samples the full-resolution desktop crop
     * directly. The two passes survive as straight copies.
     *
     * Deleting them properly is a separate change: the lens pass would bind the
     * region texture instead of the blur result, and the luma staging copy would
     * follow it. Recorded here rather than half-done.
     */
    float blurSigma = 0.0f;
    float displacementScale = 70.0f;
    float aberrationIntensity = 2.0f;
    float saturation = 1.4f;
    // Offset of the sampled area relative to the panel (physical pixels):
    // 0 for regular glass; non-zero makes the panel show content from
    // elsewhere (e.g. feedback-free observation windows in latency tests)
    float sourceOffsetX = 0.0f;
    float sourceOffsetY = 0.0f;
};

class GlassPanel {
public:
    ~GlassPanel();

    // Create the window and composition chain (bounds in screen physical pixels)
    HRESULT Create(ID3D11Device* device, const RECT& bounds, const GlassParams& params,
                   bool excludeFromCapture);

    void SetBounds(const RECT& bounds);
    /*
     * Split form of SetBounds, for panels that move while visible.
     *
     * SetBounds moves the window and resizes the swapchain in one go, and the
     * session calls it while pumping commands — a full AcquireFrame (up to 8 ms)
     * before anything is redrawn. DWM composites in that gap, so the panel shows
     * one frame of its *previous* region's contents at the *new* position: the
     * old message's glass appears under the new message.
     *
     * So the region is changed first (which is what the render pass reads), the
     * new region is rendered, and only then is the window moved.
     */
    void SetBoundsDeferred(const RECT& bounds);
    void ApplyWindowPosition();
    void SetParams(const GlassParams& params) { params_ = params; }
    void Show();
    void Hide();
    // Place the panel right below the anchor window in z-order; topmost when
    // the anchor is invalid
    void AnchorBelow(HWND anchor);
    void SetExcludeFromCapture(bool exclude);

    // Fade: target opacity + duration (the worker thread advances in ~16ms steps)
    void BeginFade(float target, UINT fadeMs) {
        fadeTarget_ = target;
        fadeStep_ = fadeMs <= 16 ? 1.0f : 16.0f / static_cast<float>(fadeMs);
    }
    bool FadeStep();  // returns true while still transitioning (keep redrawing)

    HWND hwnd() const { return hwnd_; }
    const RECT& bounds() const { return bounds_; }
    const GlassParams& params() const { return params_; }
    float opacity() const { return opacity_; }
    bool visible() const { return visible_; }

    ID3D11RenderTargetView* AcquireBackBuffer();  // rebuilds swapchain buffers on resize
    void Present();

    /*
     * Read the current back buffer out as tightly packed BGRA rows.
     *
     * The back buffer is the only thing that actually reaches the screen — the
     * comment in renderer.cc says as much where it probes it — so it is also the
     * only thing that can be composited into a screenshot and still be what the
     * user sees. The panel is excluded from every capture path by design, which
     * makes this the one way those pixels leave the process.
     *
     * Worker thread only, like every other method here.
     */
    HRESULT ReadBack(ID3D11DeviceContext* ctx, std::vector<unsigned char>* out, UINT* width,
                     UINT* height);


private:
    HRESULT EnsureSwapchain(ID3D11Device* device, UINT width, UINT height);

    HWND hwnd_ = nullptr;
    RECT bounds_{};
    GlassParams params_{};
    bool visible_ = false;
    float opacity_ = 0.0f;
    float fadeTarget_ = 0.0f;
    float fadeStep_ = 0.12f;

    ComPtr<ID3D11Device> device_;
    ComPtr<IDCompositionDevice> dcompDevice_;
    ComPtr<IDCompositionTarget> dcompTarget_;
    ComPtr<IDCompositionVisual> dcompVisual_;
    ComPtr<IDXGISwapChain1> swapchain_;
    ComPtr<ID3D11RenderTargetView> rtv_;
    UINT swapW_ = 0;
    UINT swapH_ = 0;
};
