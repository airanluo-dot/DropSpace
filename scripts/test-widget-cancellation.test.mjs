import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
const source = fs.readFileSync(process.env.WIDGET_SOURCE || new URL('../src/DropSpace.App/Views/Settings/WidgetEditorView.cs', import.meta.url), 'utf8');
for (const event of ['PointerCanceledEvent', 'PointerCaptureLostEvent']) {
  test(`${event} clears drag click suppression`, () => {
    const start = source.indexOf(`button.AddHandler(${event}`);
    assert.ok(start >= 0);
    const next = source.indexOf('button.AddHandler(', start + 1);
    const handler = source.slice(start, next < 0 ? undefined : next);
    assert.match(handler, /_suppressClicks\.Remove\(id\)/);
  });
}
