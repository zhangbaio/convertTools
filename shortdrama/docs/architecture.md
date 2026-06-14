# ShortDrama MVP Architecture

This MVP focuses on one end-to-end slice:

1. Parse `短剧信息.txt`
2. Resolve `config/sign.png` and `config/seal.png`
3. Render `成本报表.png` directly with ImageSharp
4. Optionally fill and save the original Word cost report template
5. Expose the flow through CLI for OpenClaw
6. Batch transcode source videos with FFmpeg
7. Generate project images from sampled video frames
8. Rewrite project metadata using an OpenAI-compatible chat endpoint
9. Rename poster images using project title-based naming
10. Batch rename episode video files using configurable templates

## Projects

- `ShortDrama.Core`
  - domain models
  - service interfaces
- `ShortDrama.Infrastructure`
  - TXT parsing
  - OpenXML document editing
  - ImageSharp-based cost report rendering
- `ShortDrama.Cli`
  - command-line entry point
- `ShortDrama.Agent`
  - reserved for future local HTTP wrapper

## Current Scope

- Implemented:
  - project info parsing
  - upward config lookup
  - direct PNG rendering for cost report
  - optional Word template editing for archived original docx
  - CLI command skeleton
  - batch video transcode service and CLI/HTTP entry points
  - project image generation service and CLI/HTTP entry points
  - AI rewrite service and CLI/HTTP entry points
  - poster rename service and CLI/HTTP entry points
  - batch file rename service and CLI/HTTP entry points
- Deferred:
  - workflow orchestration
  - AI rewrite
  - poster / project image generation
  - OpenClaw HTTP mode

## External Dependencies

- .NET 8 SDK
- FFmpeg / FFprobe

Cost report image generation no longer depends on LibreOffice, Poppler, or ImageMagick.
