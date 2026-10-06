# ZinkRecorder

`ZinkRecorder` is ZINK's native gaming replay backend. Its public ABI is kept
small so the WinUI layer can retain the existing recording UI and lifecycle.

The backend owns:

- Windows Graphics Capture and its D3D11 device.
- An asynchronous bounded GPU-texture queue.
- Adapter-aware NVIDIA NVENC and Intel Quick Sync/Arc Media Foundation MFTs.
- SDR NV12/H.264 and HDR10 P010/HEVC Main10 encoding.
- Keyframe-aligned encoded rolling video storage.
- Rolling desktop and microphone PCM storage.
- Media Foundation MP4/AAC writing, including the default combined track and
  optional isolated desktop and microphone tracks.

FFmpeg is not part of this backend and must not be used as a fallback. A backend
startup failure is reported to the existing recording service so ZINK never
silently changes codec, colour format, frame rate, or audio-track behaviour.
