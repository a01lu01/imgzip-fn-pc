# Third-party components

ImgZip launches caesium-clt as a separate executable; no codec source changes are included.

| Component | Pinned version | License / upstream |
| --- | --- | --- |
| caesium-clt | 1.4.0 | Apache-2.0; `licenses/caesium-clt-LICENSE.md`; https://github.com/Lymphatus/caesium-clt/tree/v1.4.0 |
| Windows App SDK | 2.4.0 | MIT; `licenses/WindowsAppSDK-LICENSE.txt`; https://github.com/microsoft/WindowsAppSDK |
| CommunityToolkit.Mvvm | 8.4.2 | MIT; `licenses/CommunityToolkit-LICENSE.md`; https://github.com/CommunityToolkit/dotnet |
| .NET | SDK 10.0.400; runtime resolved in NuGet lock files | MIT and third-party notices; https://github.com/dotnet/runtime |

The build downloads the unmodified caesium archive from its tagged release and checks the SHA-256 recorded in `build/dependencies.json`. Preserve any notices delivered with dependency packages when redistributing build outputs. Inno Setup 7.1.0 is a build tool, not an application runtime dependency.
