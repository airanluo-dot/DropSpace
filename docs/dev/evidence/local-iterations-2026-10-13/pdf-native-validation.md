# Round 24: PDF preview native ownership

Status: final. One necessary normal probe build and one selected combined scenario
completed successfully, exit 0. No restore, retry, other scenario or App build ran.

The single opt-in `pdf-native` scenario in the existing media lifecycle probe links actual
`PdfPreviewLifetime` and `NativeAsyncLifetime`. It covered the global two-session bound
including retirement; per-owner single-operation admission without queuing; pre-cancelled
parent rejection; parent cancellation and idempotent retirement; independent native
Completed, native Cancel, owner cancellation callbacks, and resource cleanup barriers;
then fresh admission and synchronous success/failure recovery. The original synchronous
exception instance propagated, and the following render was accepted. The synchronous
delegate's managed thread ID also matched its calling thread. All waits used controlled
completion barriers with finite failure deadlines, not timing delays or polling.

Only this scenario executed using existing assets; no historical probe ran. The harness
retains its existing `EnableNETAnalyzers=false`,
without disabling analyzers in any production project.

The exact command was:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj --no-restore -- pdf-native
```

[pdf-native-run.log.txt](pdf-native-run.log.txt) contains the actual output: `PASS pdf-native`
and `PASS 1 selected production-source scenarios`. This console probe produces no TRX.
[pdf-native-validation-inputs.json](pdf-native-validation-inputs.json) records the command,
counts, compiled assembly hash and frozen source hashes. All pre-run inputs matched at the
first post-run check. The later MainPage static-only change is recorded separately below;
all compiled probe inputs still match the executed versions.
The project snapshot is [pdf-native-project.csproj.txt](pdf-native-project.csproj.txt).

Actual PDF parsing, WinRT native cancellation, WinUI dispatching/rendering and preview-host
wiring are not executed by this harness. The host integration requires independent static
review and Windows runtime validation; the successful source-linked result is not that runtime
validation. MainPage's hash is recorded separately as static-only; the probe does not compile
that file. Cleanup-failure retention remains static-only coverage.

After the run, final static review narrowed MainPage's cancellation routing: five PDF and
DataWriter operations use the helper's default off-thread cancellation; only
`BitmapImage.SetSourceAsync` uses its captured UI dispatcher for cancellation. This keeps
the specific XAML object's cancellation on its owner context without dispatching every
PDF/writer cancellation there. The original as-run MainPage hash remains intact in the
frozen snapshot; `finalStaticInputs` records the final reviewed hash and capture time.
This change caused no additional build or functional execution. Actual UI cancellation
duration, WinRT behavior and PDF rendering interaction remain unverified on Windows.

The final routing is a conservative owner-context choice based on upstream source already
read by the implementation agent, root and independent reviewer at commit
`5bc5c00049da6864f0acaf92bee8260c14d91ee6`:
[DXamlAsyncBase declaration](https://github.com/microsoft/microsoft-ui-xaml/blob/5bc5c00049da6864f0acaf92bee8260c14d91ee6/dxaml/xcp/dxaml/lib/DXamlAsyncBase.h#L17),
[alias](https://github.com/microsoft/microsoft-ui-xaml/blob/5bc5c00049da6864f0acaf92bee8260c14d91ee6/dxaml/xcp/dxaml/lib/DXamlAsyncBase.h#L180),
and [BitmapSource operation](https://github.com/microsoft/microsoft-ui-xaml/blob/5bc5c00049da6864f0acaf92bee8260c14d91ee6/dxaml/xcp/dxaml/lib/BitmapSource_Partial.cpp#L239).
The reviewed [OnCancel override](https://github.com/microsoft/microsoft-ui-xaml/blob/5bc5c00049da6864f0acaf92bee8260c14d91ee6/dxaml/xcp/dxaml/lib/DXamlAsyncBase.h#L67)
is empty. These observations do not prove that background cancellation necessarily fails
or that cancellation cannot block. This upstream commit has not been matched to the target
Microsoft.WindowsAppSDK 2.5.1 binaries. No new source fetch or runtime check was performed
for this evidence update.
