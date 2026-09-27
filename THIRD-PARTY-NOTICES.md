# Third-party notices

Textalonia is distributed under the MIT license in LICENSE. Dependencies retain
their own licenses and copyright notices; Textalonia does not relicense them.

The core package declares only Avalonia and AngleSharp. The following inventory
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
