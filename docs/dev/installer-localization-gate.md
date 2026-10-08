# Installer localization gate

The installer keeps its standard wizard translations from the existing SHA-256 and
signature-pinned Inno Setup **7.0.2** package (`scripts/Install-InnoSetup.ps1`).
Repository-owned resources remain in `installer/localization/<locale>.isl`, with the
existing App review fingerprints. Do not edit framework resources to pass a check.

`scripts/check-localization.mjs` rejects non-comment content in the main
`installer/DropSpace.iss` `[Messages]` and `[CustomMessages]` sections, including
language-qualified entries and empty-value overrides. Move overrides to the ten
reviewed language files. Main-script entries are read after language files and can
overwrite their final values, so they cannot be an untracked second review path.
The same guard applies to extraction and explicit review confirmation.

Percent tokens follow the pinned compiler, rather than a generic printf pattern:

| Source | `[Messages]` compiler result | `[CustomMessages]` compiler result |
| --- | --- | --- |
| `%n` | CR/LF | CR/LF |
| `%%n` | literal percent + CR/LF | unchanged `%%n` |
| `%%%n` | `%%` + CR/LF | `%%` + CR/LF |

After compilation, `FmtMessage` substitutes one-digit `%1` through `%9` arguments
and reduces `%%` to a literal percent. For example, `%10` is argument 1 followed by
literal `0`, and `%s` is not an Inno argument. Calling `CustomMessage` directly
returns the compiled value without this formatting step. The integrity gate
conservatively preserves parameter identities, newline counts, escaped percent
counts and bare percent counts. It uses the same section-aware tokens for resource
checking and review confirmation; escaped parameters cannot be confirmed as real
parameters. Existing translations and installer behavior are unchanged.

The primary sources for these rules are the pinned
[compiler's `EnumMessagesProc` and `EnumCustomMessagesProc`](https://github.com/jrsoftware/issrc/blob/is-7_0_2/Projects/Src/Compiler.SetupCompiler.pas#L6297)
and [runtime `FmtMessage`](https://github.com/jrsoftware/issrc/blob/is-7_0_2/Projects/Src/SetupLdrAndSetup.Messages.pas#L59).
The [Messages](https://jrsoftware.org/ishelp/topic_messagessection.htm) and
[CustomMessages](https://jrsoftware.org/ishelp/topic_custommessagessection.htm)
documentation describes final override priority and language qualification.

`node scripts/check-localization.mjs --scope app` is the production integrity gate.
The focused `node --test scripts/test-installer-localization.test.mjs` command is
explicit opt-in and contains four named functional cases. It is not added to
automatic workflows. Every actual execution, failure or retry belongs in the one
project-wide passive ledger; do not use the command as an uncounted static scan.
