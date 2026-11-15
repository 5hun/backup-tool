# Third-party components

- 7-Zip Extra 26.06, Copyright (C) 1999–2026 Igor Pavlov. Distributed as an unmodified external executable under GNU LGPL 2.1 or later, with BSD-licensed portions. The full upstream notices accompany the executable in `tools/7zip/License.txt` when published. [Official download and source](https://7-zip.org/download.html), [matching source release](https://github.com/ip7z/7zip/releases/tag/26.06).
- Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core: Microsoft, MIT license. [Source and license](https://github.com/dotnet/efcore).
- SQLitePCLRaw components: Eric Sink and contributors, Apache-2.0 license. [Source and license](https://github.com/ericsink/SQLitePCL.raw).
- SQLite native library: public domain. [Upstream](https://sqlite.org/copyright.html).

7-Zip binaries are downloaded by `scripts/setup-7zip.ps1`; they are not committed to this repository. Dependency versions and transitive license information can be inspected through NuGet project assets in `Backup.Infrastructure/obj` after restore.
