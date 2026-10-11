import { copyFileSync, mkdirSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('.', import.meta.url));
const output = path.resolve(root, '../../src/TranslatorAnywhere/Assets/TranslationRenderer/vendor');
mkdirSync(path.join(output, 'fonts'), { recursive: true });
for (const [source, destination] of [
  ['marked/lib/marked.umd.js', 'marked.js'],
  ['marked/LICENSE', 'marked-LICENSE'],
  ['dompurify/dist/purify.min.js', 'purify.js'],
  ['dompurify/LICENSE', 'dompurify-LICENSE'],
  ['katex/dist/katex.min.js', 'katex.js'],
  ['katex/dist/katex.min.css', 'katex.css'],
  ['katex/LICENSE', 'katex-LICENSE'],
]) copyFileSync(path.join(root, 'node_modules', source), path.join(output, destination));
for (const file of readdirSync(path.join(root, 'node_modules/katex/dist/fonts')))
  copyFileSync(path.join(root, 'node_modules/katex/dist/fonts', file), path.join(output, 'fonts', file));
console.log('Vendored Marked 18.1.0, DOMPurify 3.4.16, KaTeX 0.19.0 and math fonts.');
