# Liquid Glass Effect - WinUI 3

A WinUI 3 application recreating the [liquid glass effect](https://github.com/archisvaze/liquid-glass) with Win2D, featuring draggable glass panels with real-time visual effects.

## Features

- **Draggable glass panel** with liquid distortion effects
- **Real-time controls** for blur, tint, distortion, and saturation
- **Win2D-powered effects** using displacement mapping and turbulence
- **Radial edge masking** for organic distortion fade
- **Background sampling** for accurate glass effect

## Technical Implementation

- **Win2D Canvas**: Custom GPU-accelerated rendering
- **Effect Chain**: Blur → Turbulence → Displacement → Tint → Blend
- **Radial Masking**: Edge fade for natural distortion
- **Background Sampling**: Real-time backdrop capture

## Build & Run

```bash
# Build
dotnet build -p:Platform=x64

# Run
dotnet run --project . -p:Platform=x64
```

## Usage

1. Drag the glass panel around the background
2. Adjust effects using the control panel:
   - **Frost Blur**: Background blur amount
   - **Distortion**: Liquid distortion strength
   - **Tint**: Glass color and opacity
   - **Noise Freq**: Distortion frequency

## Requirements

- Windows 10/11
- Visual Studio 2022 or .NET 8+
- Windows App SDK
