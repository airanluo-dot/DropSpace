# Empty clipboard views and locked evidence restore

Parent candidate: `fedc4eeb48fbeaf4a3424edb191eb2eaacf71dc8`.

## Failed evidence retained

- Windows CI `37064473709`: zh-CN passed. en-US passed Core 527,
  Infrastructure 664 (+4 skipped), App 436 (+3 skipped), and WinUI build, then
  failed the installed `--startup` clipboard smoke. Its file sequence 209 was
  observed and read, classified UnsupportedFormat, consumed with zero commits,
  and its later duplicate notification discarded. No read exception or lost
  signal was recorded. Diagnostics did not record available-format count, so
  transient empty WinRT view versus a publication issue cannot be proved from
  this trace alone. Increasing the wait timeout would not recover a consumed sequence.
- Independent Release `37064735588` passed installer lifecycle, both portable
  smokes and all four short native inference tests, then failed both full-song
  evidence variants during locked restore (`NU1004`), before inference began.
  The early generic locked restore passed. Portable self-contained/RID publish
  subsequently rewrote the shared Core/Infrastructure lock graph with win-x64
  and ILLink dependencies, incompatible with the later generic evidence restore.
  This failed run and its runtime archive are not declared an approved producer.

## Repairs

When WinRT exposes none of the supported formats, inspect only the number of
available formats. A zero-format view gets at most two probes using the existing
35 ms and 90 ms retry delays. A third empty view is normally rejected and consumed,
without incrementing FailedReads or leaving a busy/error status. A nonempty view
with genuinely unsupported formats is immediately rejected as before. Sequence
advancement and cancellation still abort stale reads; no old content is requeued.
New bounded diagnostics store only a nullable format count and an EmptyViewRetry
decision, never format names or clipboard contents.

An internal init-only reader seam retains Clipboard.GetContent as the production
default and the existing OLE owner dispatch. Four native regressions control empty
views, real mixed file/folder storage, terminal empty/unsupported views, and a
superseding publication. These controlled interleavings do not claim to establish
the historical Windows failure's exact cause.

Release evidence now runs after the generic locked harness contract test and
before RID/portable/MSIX publication. It uses the already retained exact runtime,
with all original evidence conditions, timeouts and artifact retention. Locked
restore stays enabled. A failed Baseline capture explicitly stops the AVX2 launch;
always-upload retains its failure evidence. Later packaging checks remain intact.

## Verification and remaining gates

- Independent source-extracted retry harness: 19/19 branch cases passed, including
  cancellation, sequence advancement, persistent empty, nonempty unsupported,
  and unchanged bounded COM/access retries. Native I/O and delay were stubbed;
  this is not Windows execution.
- Workflow proposal: YAML structural comparison, PowerShell syntax checks and
  synthetic exit-code fail-stop scenarios passed.
- The new native tests and installed/portable smoke still require Windows execution
  on the exact new commit. No historical failed result is discarded or called passed.
- No model prompt/sampler, memory guard, semantic approval, release tag or website
  publication is changed by these repairs.
