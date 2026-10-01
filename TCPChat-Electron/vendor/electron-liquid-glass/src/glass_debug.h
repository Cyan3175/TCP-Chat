// Temporary debugging aid for the black-frame investigation.
//
// Copies a GPU texture into a staging texture, samples it on a coarse grid and
// writes per-channel statistics to stderr. Everything here is diagnostic: it is
// how we find out whether the DXGI duplication is handing back real pixels or
// nothing at all, without guessing from the rendered result.
//
// Set GLASS_DEBUG=0 to compile it out.
#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include <cstdio>
#include <cstring>
#include <cstdlib>

using Microsoft::WRL::ComPtr;

namespace glassdbg {

inline bool enabled() {
    static const bool on = [] {
        const char* v = std::getenv("GLASS_DEBUG");
        return v && v[0] != '0';
    }();
    return on;
}

struct Stats {
    bool valid = false;
    unsigned minR = 0, minG = 0, minB = 0, minA = 0;
    unsigned maxR = 0, maxG = 0, maxB = 0, maxA = 0;
    double meanR = 0, meanG = 0, meanB = 0, meanA = 0;
    unsigned samples = 0;
    unsigned width = 0, height = 0;
    unsigned format = 0;
};

// Sample a texture and return per-channel statistics. Works for the BGRA8 and
// RGBA8 formats the duplication and our own targets use.
inline Stats Probe(ID3D11Device* device, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex) {
    Stats s{};
    if (!device || !ctx || !tex) return s;

    D3D11_TEXTURE2D_DESC desc{};
    tex->GetDesc(&desc);
    s.width = desc.Width;
    s.height = desc.Height;
    s.format = static_cast<unsigned>(desc.Format);

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
    if (FAILED(device->CreateTexture2D(&sd, nullptr, staging.GetAddressOf()))) return s;
    ctx->CopyResource(staging.Get(), tex);

    D3D11_MAPPED_SUBRESOURCE map{};
    if (FAILED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map))) return s;

    const unsigned bpp = 4;
    unsigned lo[4] = { 255, 255, 255, 255 };
    unsigned hi[4] = { 0, 0, 0, 0 };
    double sum[4] = { 0, 0, 0, 0 };
    unsigned n = 0;

    // A 64x64 grid is plenty to tell "black" from "a desktop".
    const unsigned stepX = desc.Width > 64 ? desc.Width / 64 : 1;
    const unsigned stepY = desc.Height > 64 ? desc.Height / 64 : 1;
    for (unsigned y = 0; y < desc.Height; y += stepY) {
        const auto* row = static_cast<const unsigned char*>(map.pData) + static_cast<size_t>(y) * map.RowPitch;
        for (unsigned x = 0; x < desc.Width; x += stepX) {
            const unsigned char* px = row + static_cast<size_t>(x) * bpp;
            for (int c = 0; c < 4; ++c) {
                const unsigned v = px[c];
                if (v < lo[c]) lo[c] = v;
                if (v > hi[c]) hi[c] = v;
                sum[c] += v;
            }
            n++;
        }
    }
    ctx->Unmap(staging.Get(), 0);

    if (!n) return s;
    s.valid = true;
    s.samples = n;
    s.minR = lo[0]; s.minG = lo[1]; s.minB = lo[2]; s.minA = lo[3];
    s.maxR = hi[0]; s.maxG = hi[1]; s.maxB = hi[2]; s.maxA = hi[3];
    s.meanR = sum[0] / n; s.meanG = sum[1] / n; s.meanB = sum[2] / n; s.meanA = sum[3] / n;
    return s;
}

inline void Log(const char* tag, const Stats& s) {
    if (!s.valid) {
        std::fprintf(stderr, "[glass-probe] %s: probe failed\n", tag);
        std::fflush(stderr);
        return;
    }
    std::fprintf(stderr,
                 "[glass-probe] %s: %ux%u fmt=%u samples=%u "
                 "min=(%u,%u,%u,%u) max=(%u,%u,%u,%u) mean=(%.1f,%.1f,%.1f,%.1f)\n",
                 tag, s.width, s.height, s.format, s.samples,
                 s.minR, s.minG, s.minB, s.minA,
                 s.maxR, s.maxG, s.maxB, s.maxA,
                 s.meanR, s.meanG, s.meanB, s.meanA);
    std::fflush(stderr);
}

// —— Pixel dump ——
//
// Statistics can tell you a texture is not black; they cannot tell you *what it
// shows*. When the question is "is our own window in the capture?", the only
// honest answer is the image itself. Set GLASS_DEBUG=2 to write BMPs next to the
// process's temp directory.
inline bool dumpEnabled() {
    static const bool on = [] {
        const char* v = std::getenv("GLASS_DEBUG");
        return v && v[0] == '2';
    }();
    return on;
}

inline void DumpBmp(ID3D11Device* device, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex,
                    const char* name) {
    if (!dumpEnabled() || !device || !ctx || !tex) return;

    D3D11_TEXTURE2D_DESC desc{};
    tex->GetDesc(&desc);
    // Only BGRA8 is handled; anything else would need a conversion pass.
    if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) return;

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
    if (FAILED(device->CreateTexture2D(&sd, nullptr, staging.GetAddressOf()))) return;
    ctx->CopyResource(staging.Get(), tex);

    D3D11_MAPPED_SUBRESOURCE map{};
    if (FAILED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map))) return;

    const unsigned w = desc.Width;
    const unsigned h = desc.Height;
    const unsigned rowBytes = w * 4;
    const unsigned pad = (4 - (rowBytes % 4)) % 4;
    const unsigned imageBytes = (rowBytes + pad) * h;

    // 14-byte file header + 40-byte DIB header, both little-endian.
    unsigned char fileHeader[14] = { 'B', 'M' };
    const unsigned fileSize = 14 + 40 + imageBytes;
    std::memcpy(fileHeader + 2, &fileSize, 4);
    const unsigned pixelOffset = 14 + 40;
    std::memcpy(fileHeader + 10, &pixelOffset, 4);

    unsigned char dib[40] = {};
    const unsigned dibSize = 40;
    std::memcpy(dib, &dibSize, 4);
    const int bw = static_cast<int>(w);
    const int bh = static_cast<int>(h);
    std::memcpy(dib + 4, &bw, 4);
    std::memcpy(dib + 8, &bh, 4);   // positive height = bottom-up rows
    const unsigned short planes = 1;
    const unsigned short bpp = 32;
    std::memcpy(dib + 12, &planes, 2);
    std::memcpy(dib + 14, &bpp, 2);
    std::memcpy(dib + 20, &imageBytes, 4);

    const char* tmp = std::getenv("TEMP");
    char path[MAX_PATH];
    std::snprintf(path, sizeof(path), "%s\\glass-%s.bmp", tmp ? tmp : ".", name);

    if (FILE* fp = std::fopen(path, "wb")) {
        std::fwrite(fileHeader, 1, sizeof(fileHeader), fp);
        std::fwrite(dib, 1, sizeof(dib), fp);
        // BMP rows run bottom-up.
        for (int y = static_cast<int>(h) - 1; y >= 0; --y) {
            const auto* row =
                static_cast<const unsigned char*>(map.pData) + static_cast<size_t>(y) * map.RowPitch;
            std::fwrite(row, 1, rowBytes, fp);
            for (unsigned p = 0; p < pad; ++p) std::fputc(0, fp);
        }
        std::fclose(fp);
        std::fprintf(stderr, "[glass-probe] dumped %s (%ux%u)\n", path, w, h);
        std::fflush(stderr);
    }
    ctx->Unmap(staging.Get(), 0);
}
inline void ProbeAndLog(ID3D11Device* device, ID3D11DeviceContext* ctx,
                        ID3D11Texture2D* tex, const char* tag) {
    if (!enabled()) return;
    Log(tag, Probe(device, ctx, tex));
}

}  // namespace glassdbg
