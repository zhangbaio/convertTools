# Project Memory

## Bundled .NET SDK

- This repository uses the bundled .NET SDK at `D:\code\convertTools-main\.dotnet\dotnet.exe`.
- The required SDK version is `8.0.422`, pinned by `global.json`.
- Prefer the bundled executable for restore, build, test, and run commands. Do not rely on `C:\Program Files\dotnet\dotnet.exe`; that installation currently has no SDK.
- The current-user environment variables `DOTNET_ROOT` and `DOTNET_ROOT_X64` point to `D:\code\convertTools-main\.dotnet`, and the Windows PowerShell/PowerShell profiles prepend that directory to `Path`.
- In a newly opened PowerShell, `dotnet --version` must report `8.0.422`. If the profile has not reloaded, invoke the SDK explicitly:

  ```powershell
  & "D:\code\convertTools-main\.dotnet\dotnet.exe" --version
  ```

- Start the TikTok assistant from source with:

  ```powershell
  dotnet run --project "D:\code\convertTools-main\src\TikTokPublisher\TikTokPublisher.Desktop\TikTokPublisher.Desktop.csproj" -c Release
  ```
