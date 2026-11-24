# Windows runtime support policy

## Decision

LiveWall 1.0 requires the **.NET 8 Desktop Runtime** (`Microsoft.WindowsDesktop.App` **8.x**, x64).

- A stable 8.x runtime satisfies the requirement. Patch and minor 8.x releases are selected by
  the app's default within-major roll-forward policy.
- .NET 9.x and 10.x Desktop Runtimes do **not** satisfy the requirement on their own.
- Preview and release-candidate builds do not satisfy the requirement.
- If no 8.x runtime is present, setup installs the latest .NET 8 Desktop Runtime, even when a
  newer major is already installed.

## Why exact 8.x

`LiveWall/LiveWall.csproj` targets `net8.0-windows`. The published
`LiveWall.runtimeconfig.json` requests `Microsoft.WindowsDesktop.App` **8.0.0** and does not
include a `rollForward` setting. The .NET host's default is to roll forward only within the
requested major; it does not cross from 8.x to 9.x or 10.x. Claiming otherwise (as the old
installer comment did) made setup skip the runtime on a 9/10-only machine, after which the app
could not start.

Pinning the runtime also keeps the supported WinForms/WebView2 combination bounded and matches
the build and test matrix for this release. Moving to a newer major is a deliberate app change:
retarget the project, test the app on that runtime, then update this policy and the setup probe
in the same change.

## Installer behavior

`installer/DesktopRuntime.cs` probes these x64 roots:

1. `%ProgramFiles%\dotnet`
2. `%DOTNET_ROOT%`, when set

Within each root it looks for `shared\Microsoft.WindowsDesktop.App\<version>` where `<version>`
parses as `8.x` and is not a preview/RC name. `%ProgramFiles(x86)%` is intentionally excluded:
the setup and app are x64, so a 32-bit runtime cannot host the apphost.

When no 8.x runtime is found, setup downloads and silently installs the .NET 8 Desktop Runtime
(elevating once if needed) and then re-checks the probe before continuing. A machine with only
9.x or 10.x therefore gets 8.x installed.

## Verification matrix

Covered by `.github/workflows/lw2-runtime-matrix.yml` on 64-bit Windows 10 and Windows 11
runners. Each case records the installed runtime set, the setup log, the installer action, the
installed `runtimeconfig.json`, and an actual `LiveWall.exe` launch.

| Case | Before setup | Expected setup action | Expected app launch |
| --- | --- | --- | --- |
| `dotnet8-only` | 8.x | skip .NET download | starts on 8.x |
| `dotnet9-only` | 9.x only | install 8.x | starts on 8.x |
| `dotnet10-only` | 10.x only | install 8.x | starts on 8.x |
| `none` | no Desktop Runtime | install 8.x | starts on 8.x |
| `dotnet8-and-9` | 8.x + 9.x | skip .NET download | starts on 8.x |

The launch assertion requires the managed app to create `%LOCALAPPDATA%\LiveWall` and stay
alive, then records the `coreclr.dll` / `System.Windows.Forms.dll` paths loaded by the process.
That distinguishes a real managed start from a failed apphost that leaves an error dialog open.
