# Native STEP import

The C++ worker uses Open CASCADE 8.0.1 directly. The desktop application has no
Python dependency. STEP transfer normalizes source units to millimetres, meshes
the actual faces, and generates six orthographic views through exact OCCT hidden
line removal followed by curve discretization. Visible and hidden paths remain
separate.

## Build

Run `Get-NativeDependencies.ps1` once; it downloads official, SHA-256 pinned SDK
and runtime archives under the repository's ignored `artifacts/native-deps`.
Run `Build-Native.ps1` from an x64 Visual Studio Developer PowerShell with CMake
and Ninja on PATH. Alternatively, pass `-MsvcRoot` pointing to a portable MSVC
layout containing `VC/Tools/MSVC` and `Windows Kits/10`. The build changes only
process-local environment variables and restores them afterward.

The complete runtime is produced under `artifacts/native-worker`. Package its
EXE, DLLs, `licenses`, `THIRD-PARTY-NOTICES.txt` and `native-build-info.json` into
the desktop application's `CadWorker` directory. Do not include SDKs, compiler
tools or synthetic fixture outputs. The runtime dependencies are selected from
PE imports rather than copying the entire third-party SDK.

## Protocol and tests

`Kabelaar.CadWorker.exe --protocol 1 --input file.step --output result.json
--deflection 0.1` writes camelCase JSON: protocol version, kernel version, units,
vertices/triangles, and `Front`, `Back`, `Left`, `Right`, `Top`, `Bottom` view
paths. Projected coordinates are centred, with positive Y pointing down.

Only a completed result with exit code zero is accepted. The C# service kills
the worker process tree after cancellation or a two-minute deadline; input,
output and mesh limits bound accidentally huge imports. The process isolation
protects editor availability, and is not an OS security sandbox.

Infrastructure tests build synthetic boxes in millimetres and inches, plus a
connector-shaped solid with curved bores. They check actual native conversion,
mesh indices, all six dimensions, hidden/visible curves, invalid input and
deadline handling. Set `KABELAAR_CAD_WORKER` to override the worker path when
testing an installed artifact.
