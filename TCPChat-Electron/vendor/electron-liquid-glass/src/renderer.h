// Glass renderer
// One pass: analytic rounded-rect SDF lens displacement + RGB dispersion +
//           saturation + rounded-corner AA coverage
// Input is a region copy of the DDA desktop texture (kept around so fades can
// redraw without a fresh frame), sampled at full resolution and shown through
// the lens as it is. There is no blur: the two separable Gaussian passes that
// used to sit here were removed with the setting that drove them.
#pragma once
#include <d3d11.h>
#include <wrl/client.h>

#include <utility>
#include <vector>

#include "panel.h"

using Microsoft::WRL::ComPtr;

struct LumaBand;
struct LumaBandStats;

class GlassRenderer {
public:
    HRESULT Initialize(ID3D11Device* device);

    // Copy the panel region out of the desktop frame texture, run the lens pass
    // and Present
    HRESULT RenderFromDesktop(ID3D11DeviceContext* ctx, ID3D11Texture2D* desktopTex,
                              const RECT& desktopRect, GlassPanel& panel, float dpr);
    // Redraw from the most recent region texture (advances fades when no new
    // frame is available)
    HRESULT Redraw(ID3D11DeviceContext* ctx, GlassPanel& panel, float dpr);

    // Read back per-band statistics from the region texture (mean color + luma
    // p15/p85); band rects are panel-local physical pixels
    HRESULT SampleBands(ID3D11DeviceContext* ctx, const std::vector<LumaBand>& bands,
                        float dpr, std::vector<LumaBandStats>* out);

    // Declare that luma sampling will follow: the render path then queues the
    // staging copy before the lens pass, so that submission and Present hide the
    // copy latency and SampleBands' Map barely waits
    void SetLumaWanted(bool wanted) { lumaWanted_ = wanted; }

    bool hasFrame() const { return hasFrame_; }
    // The panel's sampling region (including the margin the lens reaches into,
    // and any source offset) as a rect in the virtual desktop
    static RECT RegionForPanel(const RECT& panelBounds, const GlassParams& params, float dpr);

private:
    HRESULT EnsureTargets(ID3D11Device* device, UINT regionW, UINT regionH);
    HRESULT EnsureStaging(ID3D11DeviceContext* ctx);
    void RunPasses(ID3D11DeviceContext* ctx, GlassPanel& panel, float dpr);

    ComPtr<ID3D11VertexShader> vs_;
    ComPtr<ID3D11PixelShader> lensPs_;
    ComPtr<ID3D11SamplerState> sampler_;
    ComPtr<ID3D11Buffer> cb_;

    // Region texture (desktop crop snapshot, full resolution)
    ComPtr<ID3D11Texture2D> regionTex_;
    ComPtr<ID3D11ShaderResourceView> regionSrv_;
    // Staging texture for luma readback (lazily created, same size as the region)
    ComPtr<ID3D11Texture2D> stagingTex_;

    UINT regionW_ = 0;
    UINT regionH_ = 0;
    bool hasFrame_ = false;
    bool lumaWanted_ = false;   // luma bands configured: render path pre-queues the staging copy
    bool stagingValid_ = false; // staging holds the latest region copy
};
