# Translation renderer assets

This local page is hosted by WebView2CompositionControl inside the WPF translation popup.
It uses Marked 18.1.0, DOMPurify 3.4.16 and KaTeX 0.19.0. The libraries and fonts are
vendored so displaying a translation never requires a CDN or an external page.
Upstream licenses are retained alongside the vendored files.

To reproduce the vendor assets or run renderer checks, from the repository root:

```powershell
Set-Location scripts/renderer
npm ci --ignore-scripts
npm run vendor
npm test
```

Math is tokenized before Markdown processing so TeX escapes and underscores survive.
Supported delimiters: `\(...\)`, `\[...\]`, `$...$`, `$$...$$`. Standalone equation,
align, gather and multline environments are accepted, including starred variants.
Code spans and fenced code blocks remain literal. Invalid/unsupported math keeps its
TeX source. Raw model HTML is escaped; remote images and active links are not loaded.
