# Round 12 — retained image output stream

Status: **implemented and actual-SDK C# compilation passed** after final implementation,
independent review and root authorization. One offline-cache restore and one necessary build
completed successfully: **0 warnings, 0 errors**, 9.45 seconds. No build retry or functional
scenario ran.

The ephemeral project uses genuine cached Microsoft.Windows.SDK.NET.Ref 10.0.20348.57
`Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` projections for net8.0. It compiles
the actual image transform, codec preflight, decoder preflight and native lifetime sources
against current Core/Infrastructure, including `ActionOutputPolicy`. This confirms the real
`FileStream.AsRandomAccessStream()` and `BitmapEncoder.CreateAsync` signatures bind with the
new ReadWrite reservation overload. The old four-argument output helper retains its default
Write mode. No Windows API shim or production test seam is present.

Default .NET analyzers, AnalysisLevel 10.0, and warnings-as-errors remain enabled. A
SupportedOSPlatform(`windows10.0.20348`) assembly attribute declares the actual production
minimum; CA1416 was not suppressed. No WinUI/XAML or historical media subset was compiled.

Executed commands from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet restore /workspace/.tools/image-transform-native-compile/ImageTransformCompile.csproj --source /home/agent/.nuget/packages/ --ignore-failed-sources --nologo
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet build /workspace/.tools/image-transform-native-compile/ImageTransformCompile.csproj --no-restore --nologo
```

Evidence: [raw restore output](image-output-native-restore.log.txt),
[raw build output](image-output-native-build.log.txt),
[exact project copy](image-output-native-subset.csproj.txt), and
[source/reference/assembly hashes and execution receipt](image-output-native-validation-inputs.json).
The project accepts `DropSpaceRoot` and `NativeRefsRoot` properties for another local checkout.

This is a compilation check, not a functional pass. Actual WinRT encoding, Windows exclusive
handle/rename behavior, concurrent output replacement attempts and encoder cancellation or
failure cleanup remain unverified because the environment is Linux. No historical image,
hashing, QR or media scenario ran; no output image was generated.
