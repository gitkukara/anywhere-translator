/* Local-only translation renderer. Formula tokens are recognized before Markdown can consume TeX escapes. */
(() => {
  'use strict';
  const article = document.getElementById('translation');
  const host = window.chrome?.webview;
  let revision = 0;
  let lastText = '';
  const escapeHtml = text => text.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const escaped = (text, index) => {
    let count = 0;
    while (index > 0 && text[--index] === '\\') count++;
    return count % 2 === 1;
  };
  function closingIndex(source, delimiter, from) {
    let index = source.indexOf(delimiter, from);
    while (index >= 0) {
      if (!escaped(source, index)) return index;
      index = source.indexOf(delimiter, index + delimiter.length);
    }
    return -1;
  }
  function formula(source, block) {
    const leading = block ? /^(?: {0,3})/.exec(source)[0].length : 0;
    const input = source.slice(leading);
    let open, close, display = false;
    if (input.startsWith('$$')) { open = close = '$$'; display = true; }
    else if (input.startsWith('\\[')) { open = '\\['; close = '\\]'; display = true; }
    else if (!block && input.startsWith('\\(')) { open = '\\('; close = '\\)'; }
    else if (!block && input.startsWith('$') && !/^\$\s/.test(input)) { open = close = '$'; }
    else if (block) {
      const environment = /^\\begin\{(equation\*?|align\*?|gather\*?|multline\*?|displaymath)\}/.exec(input);
      if (environment) { open = environment[0]; close = '\\end{' + environment[1] + '}'; display = true; }
    }
    if (!open) return;
    const end = closingIndex(input, close, open.length);
    // Explicit delimiters and environments also protect unfinished streamed TeX from Markdown.
    if (end < 0) {
      if (open === '$' && !/^\$[A-Za-z\\{]/.test(input)) return;
      return { type: block ? 'mathBlock' : 'mathInline', raw: source, tex: input, display, pending: true };
    }
    const tex = input.slice(open.length, end);
    if (open === '$' && (!tex || /\s$/.test(tex) || tex.includes('\n') || input[end + 1] === '$')) return;
    let raw = source.slice(0, leading + end + close.length);
    if (block) {
      const rest = source.slice(raw.length);
      if (!/^(?:[ \t]*\n|[ \t]*$)/.test(rest)) return;
      raw += /^(?:[ \t]*\n|[ \t]*$)/.exec(rest)[0];
    }
    let expression = tex;
    if (/^\\begin\{align/.test(open)) expression = '\\begin{aligned}' + tex + '\\end{aligned}';
    if (/^\\begin\{(?:gather|multline)/.test(open)) expression = '\\begin{gathered}' + tex + '\\end{gathered}';
    return { type: block ? 'mathBlock' : 'mathInline', raw, tex: expression, display, pending: false };
  }
  function mathHtml(token) {
    const tag = token.display ? 'div' : 'span';
    if (token.pending) return `<${tag} class="math-pending">${escapeHtml(token.tex)}</${tag}>`;
    return `<${tag} class="${token.display ? 'math-display' : 'math-inline'}" data-tex="${escapeHtml(token.tex)}"></${tag}>`;
  }
  const parser = new marked.Marked({
    gfm: true,
    breaks: false,
    extensions: [
      {
        name: 'mathBlock', level: 'block',
        start(source) { return source.search(/(?:^|\n) {0,3}(?:\$\$|\\\[|\\begin\{(?:equation|align|gather|multline|displaymath))/); },
        tokenizer(source) { return formula(source, true); }, renderer: mathHtml,
      },
      {
        name: 'mathInline', level: 'inline',
        start(source) { return source.search(/\$|\\\(|\\\[/); },
        tokenizer(source) { return formula(source, false); }, renderer: mathHtml,
      },
    ],
    renderer: {
      html({ text }) { return escapeHtml(text); },
      image({ text }) { return escapeHtml(text || ''); },
      link({ tokens }) { return this.parser.parseInline(tokens); },
    },
  });
  function reportHeight() {
    host?.postMessage({ type: 'height', revision, height: Math.ceil(article.getBoundingClientRect().height) });
  }
  function render(message) {
    if (!message || message.type !== 'render' || typeof message.text !== 'string'
        || !Number.isSafeInteger(message.revision) || message.revision < revision) return;
    const scroll = document.scrollingElement || document.documentElement;
    const oldOffset = scroll.scrollTop;
    revision = message.revision;
    const palette = message.palette || {};
    for (const name of ['ink', 'surface', 'muted', 'divider', 'accent', 'selection'])
      if (/^#[0-9a-f]{6}$/i.test(palette[name])) document.documentElement.style.setProperty('--' + name, palette[name]);
    document.documentElement.style.colorScheme = message.dark ? 'dark' : 'light';
    try {
      article.classList.remove('plain-fallback');
      article.innerHTML = DOMPurify.sanitize(parser.parse(message.text), {
        ALLOWED_TAGS: ['p', 'br', 'strong', 'em', 'del', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'ul', 'ol', 'li', 'blockquote', 'pre', 'code', 'hr', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'span', 'div'],
        ALLOWED_ATTR: ['class', 'data-tex', 'start', 'align'],
      });
      for (const element of article.querySelectorAll('[data-tex]')) {
        const tex = element.getAttribute('data-tex');
        element.removeAttribute('data-tex');
        try {
          katex.render(tex, element, { displayMode: element.classList.contains('math-display'),
            throwOnError: true, trust: false, strict: 'ignore', maxSize: 20, maxExpand: 1000 });
        } catch {
          element.classList.add('math-error');
          element.textContent = tex;
          element.title = '公式暂时无法排版，已保留 LaTeX 原文';
        }
      }
      for (const table of article.querySelectorAll('table')) {
        const wrapper = document.createElement('div');
        wrapper.className = 'table-scroll';
        table.replaceWith(wrapper);
        wrapper.append(table);
      }
    } catch {
      article.classList.add('plain-fallback');
      article.textContent = message.text;
    }
    if (message.text === '' || !message.text.startsWith(lastText)) scroll.scrollTop = 0;
    else scroll.scrollTop = oldOffset;
    lastText = message.text;
    reportHeight();
  }
  window.translationRenderer = { render }; // Also used by offline renderer checks.
  host?.addEventListener('message', event => render(event.data));
  new ResizeObserver(reportHeight).observe(article);
  document.fonts?.ready.then(reportHeight);
  document.addEventListener('keydown', event => {
    let action;
    if (event.key === 'Escape') action = 'dismiss';
    if (event.ctrlKey && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 'r') action = 'retry';
    if (event.ctrlKey && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 'c' && !window.getSelection()?.toString()) action = 'copy';
    if (action) { event.preventDefault(); host?.postMessage({ type: 'action', action }); }
  });
  document.addEventListener('click', event => { if (event.target.closest('a')) event.preventDefault(); });
  host?.postMessage({ type: 'ready' });
})();
