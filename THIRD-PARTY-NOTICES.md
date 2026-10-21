# Third-party notices

SDE for Mac includes the software and fonts below. Each is used under its own license; the license texts ship with the
packages (in the NuGet cache when you build from source) and at the links given. Packages used only to build or test
SDE (xunit, the test SDK and the like) are not shipped and are not listed.

SDE itself is MIT-licensed (see `LICENSE`). It is an unofficial port of FEMA's Substantial Damage Estimator and is not
produced or endorsed by FEMA.

## NuGet packages shipped in SDE.app

Versions are those resolved for the macOS app. Licenses are taken from each package's `.nuspec`.

### Application framework

| Package | Version | License | Project |
| --- | --- | --- | --- |
| Avalonia | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.Desktop | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.Native | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.Skia | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.HarfBuzz | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.Themes.Fluent | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.Controls.DataGrid | 12.1.2 | MIT | https://avaloniaui.net/ |
| Avalonia.Fonts.Inter | 12.1.3 | MIT (the Inter font inside it is SIL OFL 1.1, see below) | https://avaloniaui.net/ |
| Avalonia.Remote.Protocol | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.X11, Avalonia.Win32 | 12.1.3 | MIT | https://avaloniaui.net/ |
| Avalonia.Angle.Windows.Natives (Windows only, pulled in by Avalonia.Win32; a build of Google's ANGLE) | 2.1.27548.20260419 | BSD-3-Clause (the package's `LICENSE` file) | https://avaloniaui.net/ |
| Avalonia.BuildServices | 11.3.2 | MIT | https://avaloniaui.net/ |
| MicroCom.Runtime | 0.11.6 | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | 0.94.1 | MIT | https://github.com/tmds/Tmds.DBus |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |

AvaloniaUI.DiagnosticsSupport (developer tools) is referenced for Debug builds only and is not part of a release build.

### Graphics and text

| Package | Version | License | Project |
| --- | --- | --- | --- |
| SkiaSharp | 3.119.4 | MIT | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.macOS (also .Linux, .Win32, .WebAssembly) | 3.119.4 | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp | 8.3.1.3 | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp.NativeAssets.macOS (also .Linux, .Win32, .WebAssembly) | 8.3.1.3 | MIT | https://github.com/mono/SkiaSharp |
| SixLabors.Fonts | 1.0.0 | Apache-2.0 | https://github.com/SixLabors/Fonts |

The SkiaSharp native library is a build of Google's Skia (BSD-3-Clause) and carries its own bundled components
(FreeType, libpng, zlib, expat and others) under their respective permissive licenses; the HarfBuzzSharp native library
is a build of HarfBuzz (MIT-style "Old MIT"). See https://github.com/mono/SkiaSharp/blob/main/external/skia/LICENSE
and https://github.com/harfbuzz/harfbuzz/blob/main/COPYING.

### Database

| Package | Version | License | Project |
| --- | --- | --- | --- |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | https://learn.microsoft.com/dotnet/standard/data/sqlite/ |
| SQLitePCLRaw.bundle_e_sqlite3, .core, .lib.e_sqlite3, .provider.e_sqlite3 | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Dapper | 2.1.89 | Apache-2.0 | https://github.com/DapperLib/Dapper |

`libe_sqlite3` (in SQLitePCLRaw.lib.e_sqlite3) is a build of SQLite, which is in the public domain
(https://sqlite.org/copyright.html).

### Spreadsheets, documents and reports

| Package | Version | License | Project |
| --- | --- | --- | --- |
| ClosedXML | 0.105.1 | MIT | https://github.com/ClosedXML/ClosedXML |
| ClosedXML.Parser | 2.0.0 | MIT | https://github.com/ClosedXML/ClosedXML.Parser |
| ExcelNumberFormat | 1.1.0 | MIT | https://github.com/andersnm/ExcelNumberFormat |
| ExcelDataReader | 3.9.0 | MIT | https://github.com/ExcelDataReader/ExcelDataReader |
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework | 3.3.0 | MIT | https://github.com/dotnet/Open-XML-SDK |
| PDFsharp, PDFsharp-MigraDoc | 6.2.4 | MIT | https://docs.pdfsharp.net/ |
| RBush.Signed | 4.0.0 | MIT | https://github.com/viceroypenguin/RBush |
| Newtonsoft.Json | 13.0.4 | MIT | https://www.newtonsoft.com/json |
| QRCoder (the phone form's pairing QR code) | 1.6.0 | MIT | https://github.com/Shane32/QRCoder |

PDFsharp and MigraDoc are kept on 6.2.x: version 6.5 and later are licensed under the AGPL-3.0 or a commercial
license, not MIT.

### Photos

| Package | Version | License | Project |
| --- | --- | --- | --- |
| MetadataExtractor (reads the date and GPS position in photos) | 2.9.3 | Apache-2.0 | https://github.com/drewnoakes/metadata-extractor-dotnet |
| XmpCore (pulled in by MetadataExtractor) | 6.1.10.1 | BSD-3-Clause | https://github.com/drewnoakes/xmp-core-dotnet |

XmpCore's `.nuspec` has no license expression, only a link to Adobe's license for the XMP library it is ported from
(https://www.adobe.com/devnet/xmp/library/eula-xmp-library-java.html). Its README says the same BSD license applies; Adobe
publishes its XMP Toolkit SDK under BSD-3-Clause (https://github.com/adobe/XMP-Toolkit-SDK).

### Microsoft libraries pulled in by the packages above

| Package | Version | License | Project |
| --- | --- | --- | --- |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | https://github.com/dotnet/runtime |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| System.IO.Packaging | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.Pkcs | 8.0.2 | MIT | https://github.com/dotnet/runtime |

SDE is published self-contained, so the .NET runtime (MIT, https://github.com/dotnet/runtime) travels inside the app,
together with the ASP.NET Core shared framework (MIT, https://github.com/dotnet/aspnetcore), whose Kestrel web server
runs the phone form.

## Fonts

| Font | License | Source |
| --- | --- | --- |
| Public Sans (Regular, Italic, SemiBold, Bold, Bold Italic), embedded in the PDF reports | SIL Open Font License 1.1; Copyright 2015 The Public Sans Project Authors | https://github.com/uswds/public-sans |
| Inter, the app's interface font (inside Avalonia.Fonts.Inter) | SIL Open Font License 1.1; Copyright The Inter Project Authors | https://github.com/rsms/inter |

Public Sans is a modified version of Libre Franklin (also OFL 1.1) by the U.S. General Services Administration. The
full license text is in `src/Sde.Reports/Fonts/OFL.txt` and `src/Sde.Reports/Fonts/LICENSE-PublicSans.md`, and is
reproduced at https://openfontlicense.org/open-font-license-official-text/. The fonts are used unmodified and are not
sold on their own.

## mdbtools (separate programs)

| Program | License | Source |
| --- | --- | --- |
| `mdb-export`, `mdb-tables` (mdbtools 1.0.1) | GPL-2.0-or-later (the `libmdb` library is LGPL-2.0-or-later) | https://github.com/mdbtools/mdbtools/releases/tag/v1.0.1 |

SDE reads FEMA's Microsoft Access (`.mdb`) database by running these two command-line programs. They are shipped as
separate executables in `SDE.app/Contents/MacOS/mdbtools/`, next to a copy of mdbtools' `COPYING` file. SDE does not link
against them, and SDE's own code is not a derivative of them; the two are connected only by SDE starting the programs
and reading their text output. The GPL applies to those programs, not to the rest of SDE.

The complete corresponding source for the shipped programs is the mdbtools 1.0.1 source at the link above (the
`mdbtools-1.0.1.tar.gz` archive on that release page), together with the build script in this repository
(`tools/packaging/build_mdbtools.sh`) that was used to produce them. You may copy, modify and redistribute them under the
terms of the GPL: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html.

The build uses mdbtools' own replacement for GLib (`--disable-glib`, part of the same source) and links `libmdb`
statically, so no other third-party library is linked into the two programs. They use only macOS's own `libiconv`
and `libSystem`.
