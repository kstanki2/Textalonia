# Third-party notices

Textalonia is distributed under the MIT license in LICENSE. Dependencies retain
their own licenses and copyright notices; Textalonia does not relicense them.

The core package declares only Avalonia and AngleSharp. The optional PDF package
has its own inventory below. The following core inventory
was read from the restored .NET 8 package nuspecs on 2026-09-27. The candidate
runner retains the exact resolved inventory and hashes alongside its artifacts.

| Dependency | Minimum / pinned version | License | Upstream attribution |
| --- | --- | --- | --- |
| Avalonia | 12.1.3 (declared range: >=12.1.3, <13) | MIT | Copyright 2013-2026, The AvaloniaUI Project |
| AngleSharp | 1.8.2 | MIT | Copyright 2013-2026, AngleSharp |
| Avalonia.BuildServices (transitive build dependency) | 11.3.2 | MIT | Copyright 2023-2025, The AvaloniaUI Project |
| Avalonia.Remote.Protocol (transitive) | 12.1.3 | MIT | Copyright 2013-2026, The AvaloniaUI Project |
| MicroCom.Runtime (transitive) | 0.11.6 | MIT | Copyright 2021, Nikita Tsukanov |

Upstream projects: [Avalonia](https://github.com/AvaloniaUI/Avalonia),
[AngleSharp](https://github.com/AngleSharp/AngleSharp),
[MicroCom](https://github.com/AvaloniaUI/MicroCom).
Consult the license files and metadata in each resolved NuGet package for its
complete notice. Their binaries are restored as dependencies, not bundled inside
Textalonia.nupkg. Preserve those notices when redistributing an application.

XAML, Markdown, and the ICodeHighlighter extension point are implemented in the
core package and introduce no parser or syntax-highlighter dependency. A host
that installs a highlighting adapter or resource provider must review that
adapter and its dependencies separately. No third-party adapter ships here.

Demo, headless tests, benchmarks, and mobile launchers are not shipped packages.
Their Avalonia.Desktop, Skia/native assets, Inter font, Android/iOS host packages,
and test tooling are application/development dependencies. A redistributed host
must retain the notices for its own resolved graph, including font and native
licenses; this core-package inventory is not an application redistribution audit.

## Optional PDF backend

`Textalonia.Pdf.Skia` references Textalonia and Avalonia.Skia. It adds no dependency
to an application that installs only the core package. The following optional
backend inventory was checked against restored package metadata on 2026-09-27.

| Dependency | Minimum / pinned version | License | Upstream attribution |
| --- | --- | --- | --- |
| Avalonia.Skia | 12.1.3 (declared range: >=12.1.3, <13) | MIT | Copyright 2013-2026, The AvaloniaUI Project |
| SkiaSharp | 3.119.4 | MIT | Copyright 2015-2016 Xamarin, Inc.; 2017-2018 Microsoft Corporation |
| HarfBuzzSharp | 8.3.1.3 | MIT | Microsoft Corporation; see the package LICENSE.txt |
| SkiaSharp.NativeAssets.Win32 / macOS / Linux / WebAssembly | 3.119.4 | MIT package; native third-party notices also apply | Microsoft Corporation and native component authors |
| HarfBuzzSharp.NativeAssets.Win32 / macOS / Linux / WebAssembly | 8.3.1.3 | MIT package; native third-party notices also apply | Microsoft Corporation and native component authors |

[SkiaSharp and HarfBuzzSharp](https://github.com/mono/SkiaSharp) wrap native
libraries with additional bundled-component terms. Preserve `LICENSE.txt` and
`THIRD-PARTY-NOTICES.txt` from every native asset package actually redistributed,
including the underlying Skia, HarfBuzz and related component notices. Fonts used
or embedded in output retain their own licensing and embedding restrictions.
The optional backend does not grant rights to installed or document-supplied fonts.

Native assets in the dependency graph describe upstream availability, not a
Textalonia runtime certification for every platform. See [output qualification
and support boundaries](docs/OUTPUT.md). Consumer runtime identifiers and other
application dependencies can change the final resolved graph; retain that graph
and the associated upstream notices with a redistributed application.
