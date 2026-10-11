const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const tooling = createRequire(path.resolve(__dirname, '../../scripts/renderer/package.json'));
const { JSDOM } = tooling('jsdom');
const assets = path.resolve(__dirname, '../../src/TranslatorAnywhere/Assets/TranslationRenderer');
function fixture() {
  const dom = new JSDOM('<!doctype html><article id="translation"></article>', { runScripts: 'outside-only' });
  const { window } = dom;
  window.ResizeObserver = class { observe() {} };
  const messages = [];
  window.chrome = { webview: { addEventListener() {}, postMessage(message) { messages.push(message); } } };
  for (const file of ['vendor/marked.js', 'vendor/purify.js', 'vendor/katex.js', 'renderer.js'])
    window.eval(fs.readFileSync(path.join(assets, file), 'utf8'));
  let revision = 0;
  const article = window.document.getElementById('translation');
  return { window, article, messages, render(text, palette = {}) {
    window.translationRenderer.render({ type: 'render', revision: ++revision, text, palette });
  } };
}
test('paper prose, Markdown and TeX retain structure together', () => {
  const f = fixture();
  f.render(String.raw`## 实验结果

**损失函数**为 \(\mathcal{L}_{i}=\frac{a_i}{b_i}\)，参考文献 [12]。

\[
\sum_{i=1}^{n} x_i^2 = \int_0^1 f(x)\,dx \tag{3}
\]

- 第一项
- 第二项

| 方法 | 误差 |
| --- | --- |
| A | $x_i^2$ |`);
  assert.equal(f.article.querySelector('h2').textContent, '实验结果');
  assert.equal(f.article.querySelector('strong').textContent, '损失函数');
  assert.equal(f.article.querySelectorAll('.katex').length, 3);
  assert.equal(f.article.querySelectorAll('math').length, 3);
  assert.match(f.article.querySelector('annotation').textContent, /a_i/);
  assert.equal(f.article.querySelectorAll('li').length, 2);
  assert.ok(f.article.querySelector('.table-scroll table'));
  assert.equal(f.article.querySelectorAll('.math-error').length, 0);
});
test('dollar delimiters, escaped dollars and prices are distinguished', () => {
  const f = fixture();
  f.render(String.raw`行内 $E=mc^2$ 和 \(x_{ij}\)。价格 \$5 和 \$10；价格 $5 and $10。

$$\frac{1}{2}$$`);
  assert.equal(f.article.querySelectorAll('.katex').length, 3);
  assert.match(f.article.textContent, /价格 \$5 和 \$10/);
  assert.match(f.article.textContent, /\$5 and \$10/);
});
test('display environments and matrices render without losing alignment', () => {
  const f = fixture();
  f.render(String.raw`\begin{align}
a &= b+c \\
d &= \begin{pmatrix}1 & 2 \\ 3 & 4\end{pmatrix}
\end{align}

\begin{equation}\alpha^2 + \beta^2 = 1\end{equation}`);
  assert.equal(f.article.querySelectorAll('.math-display .katex').length, 2);
  assert.equal(f.article.querySelectorAll('.math-error').length, 0);
});
test('streamed incomplete formulas keep literal source then render on closure', () => {
  const f = fixture();
  f.render(String.raw`正文 \(\frac{a_i`);
  assert.equal(f.article.querySelector('.math-pending').textContent, String.raw`\(\frac{a_i`);
  assert.equal(f.article.querySelectorAll('.katex').length, 0);
  f.render(String.raw`正文 \(\frac{a_i}{b_i}\)。`);
  assert.equal(f.article.querySelectorAll('.katex').length, 1);
  assert.equal(f.article.querySelectorAll('.math-pending').length, 0);
  f.render(String.raw`正文 $x_i+\frac{a`);
  assert.equal(f.article.querySelector('.math-pending').textContent, String.raw`$x_i+\frac{a`);
  f.render(String.raw`正文 $x_i+\frac{a}{b}$`);
  assert.equal(f.article.querySelectorAll('.katex').length, 1);
  f.render('');
  assert.equal(f.article.textContent, '');
});
test('code examples are literal and unsupported math falls back locally', () => {
  const f = fixture();
  f.render('`$x_i$`\n\n```latex\n\\[x^2\\]\n```\n\n\\(\\unknowncommand{x}\\)\n\n后续译文');
  assert.equal(f.article.querySelectorAll('code').length, 2);
  assert.equal(f.article.querySelectorAll('.katex').length, 0);
  assert.equal(f.article.querySelector('.math-error').textContent, String.raw`\unknowncommand{x}`);
  assert.match(f.article.textContent, /后续译文/);
});
test('model HTML, links, images and trusted TeX cannot run scripts or load external resources', () => {
  const f = fixture();
  f.render(String.raw`<script>window.compromised = true</script>
<img src="https://example.test/tracker" onerror="window.compromised=true">

[link](javascript:alert%281%29) ![image](https://example.test/tracker)

\(\includegraphics{https://example.test/tracker}\)
\(\href{javascript:alert(1)}{click}\)`);
  assert.equal(f.window.compromised, undefined);
  assert.equal(f.article.querySelectorAll('script,img,a,iframe,[onclick],[onerror]').length, 0);
  assert.match(f.article.textContent, /<script>/);
  assert.match(f.article.textContent, /image/);
});
test('theme updates are bounded and height messages identify their revision', () => {
  const f = fixture();
  f.render('译文', { ink: '#ddeeff', surface: 'url(https://example.test)' });
  assert.equal(f.window.document.documentElement.style.getPropertyValue('--ink'), '#ddeeff');
  assert.equal(f.window.document.documentElement.style.getPropertyValue('--surface'), '');
  assert.ok(f.messages.some(m => m.type === 'height' && m.revision === 1));
  f.window.translationRenderer.render({ type: 'render', revision: 0, text: 'stale' });
  assert.equal(f.article.textContent.trim(), '译文');
});
test('keyboard actions preserve browser selection copying', () => {
  const f = fixture();
  f.render('可选中的译文');
  const key = (name, ctrlKey = false) => {
    const event = new f.window.KeyboardEvent('keydown', { key: name, ctrlKey, bubbles: true, cancelable: true });
    f.window.document.dispatchEvent(event);
    return event.defaultPrevented;
  };
  assert.equal(key('c', true), true);
  assert.equal(f.messages.at(-1).action, 'copy');
  const range = f.window.document.createRange();
  range.selectNodeContents(f.article.querySelector('p'));
  f.window.getSelection().addRange(range);
  assert.equal(key('c', true), false);
  assert.equal(key('r', true), true);
  assert.equal(f.messages.at(-1).action, 'retry');
  assert.equal(key('Escape'), true);
  assert.equal(f.messages.at(-1).action, 'dismiss');
});
