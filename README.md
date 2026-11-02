<p align="center">
  <img src="Assets/logo.png" width="110" height="110" alt="Pinn-Notch Logo">
</p>

<h1 align="center">Pinn-Notch 🚀</h1>

<p align="center">
  <b>The Ultimate macOS-Style Floating Dock & Dynamic Island Experience for Windows 10 & 11</b>
</p>

<p align="center">
  <a href="https://github.com/Gimm17/Pinn-Notch/releases/latest">
    <img src="https://img.shields.io/github/v/release/Gimm17/Pinn-Notch?style=for-the-badge&color=00c853&labelColor=202028&logo=github&logoColor=ffffff" alt="Latest Release">
  </a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=for-the-badge&labelColor=202028&logo=windows&logoColor=ffffff" alt="Windows 10 / 11">
  <img src="https://img.shields.io/badge/.NET-8.0%20WPF-512bd4?style=for-the-badge&labelColor=202028&logo=dotnet&logoColor=ffffff" alt=".NET 8">
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-Apache%202.0-blue?style=for-the-badge&labelColor=202028" alt="License">
  </a>
  <a href="https://github.com/Gimm17/Pinn-Notch/stargazers">
    <img src="https://img.shields.io/github/stars/Gimm17/Pinn-Notch?style=for-the-badge&color=ffb300&labelColor=202028&logo=star&logoColor=ffffff" alt="Stars">
  </a>
</p>

---

## 🌟 Overview

**Pinn-Notch** is a powerful desktop companion and productivity shell for Windows. It fuses the elegance of Apple's **macOS Floating Dock** with an interactive **Dynamic Island Notch**, seamlessly integrated with real-time hardware telemetry, media playback, translucent taskbars, and privacy controls.

> ℹ️ **Open Source Attribution**: Pinn-Notch is an enhanced fork and derivative work of [V-Notch by w0otdev](https://github.com/w0otdev/V-Notch), developed under the **Apache License 2.0**. All enhancements, original architectural additions, and standalone modules are actively maintained by [Gimm17](https://github.com/Gimm17).

---

## ✨ Exclusive Features in Pinn-Notch

### 1. 🖥️ macOS-Style Floating Bottom Dock
* **Smooth Physics & Magnification:** Hover over dock icons for parabolic zoom physics with customizable scaling and frosted acrylic blur.
* **Auto-Hide & Screen Collision:** Automatically dodges active maximized windows and slides down; smoothly reveals on bottom screen edge touch.
* **Event-Driven 0ms Window Tracking:** Powered by native Win32 `SetWinEventHook` for zero-latency active window tracking.
* **Fullscreen Game Shield:** Automatically suspends dock polling and rendering when playing fullscreen 3D/DirectX/Vulkan games for 0% CPU impact.

### 2. 📊 Rigid System Status Bar Capsule (Anti-Jitter)
* **Real-Time Internet Speed Meter:** Download & upload speed with Byte standardization (`B/s`, `KB/s`, `MB/s`, `GB/s`). Fixed-width container prevents dock twitching regardless of digit length.
* **Seamless Apple-Style Battery:** Single-silhouette fused battery nub with zero-gap geometry and an integrated vector charging bolt (`⚡`).
* **Scroll-to-Adjust Master Volume:** Quick volume mute/unmute via click, plus **mouse wheel scrolling** anywhere on the volume capsule to adjust master volume by $\pm 2\%$.

### 3. 🪟 Translucent Taskbar & Freeze Engine
* **Native Taskbar Transparency:** Deep integration with TranslucentTB's native Windows composition engine.
* **Taskbar Freeze Synchronization:** Automatically hides or freezes the default Windows Taskbar in sync with the Pinn-Notch dock.

### 4. 📷 Interactive Camera Companion
* **Instant Auto-Start:** Webcam hardware activates automatically upon opening the Camera tab.
* **Live Mirror / Flip:** Toggle horizontal flip with an interactive frosted glass button or by **right-clicking anywhere** on the camera preview.

### 5. 🎵 Smart Media & WebView2 Video Throttling
* **YouTube Music & Spotify Integration:** Synced lyrics, album art blur, and playback controls.
* **Zero-GPU Background Throttling:** Automatically hides video and canvas elements when minimized, saving up to 900MB RAM and reducing GPU usage to ~0%.

---

## 🚀 Getting Started

### Prerequisites
* Windows 10 (version 19041+) or Windows 11 (x64)
* [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### Building from Source
```powershell
# Clone repository
git clone https://github.com/Gimm17/Pinn-Notch.git
cd Pinn-Notch

# Build Release
dotnet build V-Notch.csproj -c Release -p:SkipNativeCompile=true

# Launch Pinn-Notch
.\bin\Release\net8.0-windows10.0.19041.0\win-x64\Pinn-Notch.exe
```

---

## ⌨️ Shortcuts & Interaction

| Shortcut | Description |
|---|---|
| `Win + Alt + Space` | Open Pinn-Notch Settings (Customizable) |
| `Alt + Space` | Spotlight Search & App Launcher |
| Mouse Wheel on Volume Widget | Adjust Volume ($\pm 2\%$) |
| Right-Click on Camera Preview | Mirror / Flip Camera View |

---

## 📜 License & Acknowledgments

This project is licensed under the [Apache License 2.0](LICENSE).
* Original base project: [V-Notch](https://github.com/w0otdev/V-Notch) by w0otdev.
* Translucent taskbar composition: inspired by [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB).
* Maintained & Developed by **Gimm17**.
