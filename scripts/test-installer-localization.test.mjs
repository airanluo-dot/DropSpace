// Explicit opt-in only. Each named case counts toward the project-wide passive
// functional budget when executed; this file is not added to automatic suites.
import assert from 'node:assert/strict';
import test from 'node:test';
import { checkInstallerScriptOverrides, installerMessageTokens } from './check-localization.mjs';

test('main Messages entries cannot bypass reviewed language files', () => {
  assert.throws(() => checkInstallerScriptOverrides('\uFEFF [mEsSaGeS]\r\n; retained comment\r\nButtonNext=&Next\r\n'), /\[Messages\] overrides are forbidden.*reviewed installer\/localization/);
});

test('language-qualified main CustomMessages entries cannot create a second review path', () => {
  assert.throws(() => checkInstallerScriptOverrides('[CustomMessages]\nenglish.ExplorerContextTask=\n'), /\[CustomMessages\] overrides are forbidden.*reviewed installer\/localization/);
});

test('Messages preserves real arguments, unconditional newlines and percent literals', () => {
  // %%n compiles to percent + newline; %10 is %1 followed by literal 0.
  assert.deepEqual(installerMessageTokens('%1 %%1 %n %%n %%%n %10 %s 100%', 'Messages'), [
    'argument:1', 'argument:1', 'newline', 'newline', 'newline',
    'percent:escaped', 'percent:escaped', 'percent:literal', 'percent:literal', 'percent:literal',
  ].sort());
});

test('CustomMessages applies pair-aware newline expansion before single-digit arguments', () => {
  // %%n stays literal; %%%n compiles to %% + newline, then formats to % + newline.
  assert.deepEqual(installerMessageTokens('%1 %%1 %n %%n %%%n %10 %s 100%', 'CustomMessages'), [
    'argument:1', 'argument:1', 'newline', 'newline',
    'percent:escaped', 'percent:escaped', 'percent:escaped', 'percent:literal', 'percent:literal',
  ].sort());
});
