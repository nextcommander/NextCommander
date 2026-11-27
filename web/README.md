# NextCommander product site

The product site of NextCommander in English and Turkish, hosted apart from the GitHub Pages page in
[`site/`](../site). A static site: plain HTML, CSS and a little JavaScript, built by a Node script. The user guides
are pages of the site, made from the Markdown guides of the repository.

## Build and look at it

Needs Node.js 20 or later and one package, [marked](https://marked.js.org) (Markdown of the guides), pinned in
`package-lock.json`.

```sh
cd web
npm ci                    # once
node build.mjs            # writes dist/
node build.mjs --check    # also checks every internal link, anchor and picture
node serve.mjs            # http://localhost:8080 (PORT=... for another port)
```

`npm run build`, `npm run check`, `npm run serve` and `npm start` (build + serve) do the same.

Settings at build time (environment variables):

| Variable | Default | What it is |
|---|---|---|
| `SITE_URL` | `https://nextcommander.example.com` | The address the site is served from. Used for canonical and hreflang links, the sitemap, robots.txt and the social preview picture. **Set it for every real build.** |
| `REPO_URL` | `https://github.com/nextcommander/NextCommander` | Where the source, releases, issues and guides are. |

## What is built

```
dist/
  index.html          sends the visitor to /en/ or /tr/: the language picked before, else the browser's, else English
  en/  tr/            one folder per language: index.html, features/, connections/, plugins/, guide/, download/, faq/
  en/guide/<name>/    one page per guide of docs/guide (tr/guide/<name>/ the Turkish one)
  assets/             style.<hash>.css, app.<hash>.js (the hash changes with the content: cache them for long)
  screenshots/        copied from docs/screenshots, so the site always shows the program as it is
  404.html  sitemap.xml  robots.txt  site.webmanifest  favicon.ico  icon.svg  icon-*.png  apple-touch-icon.png
```

Pages link with absolute paths (`/en/features/`), so the site must be served at the root of its domain or subdomain.

## Where things are

| File | Contents |
|---|---|
| `src/i18n/en.mjs`, `src/i18n/tr.mjs` | Every text of the site. Both files have the same keys and the same list lengths; the build stops when they differ. |
| `src/pages.mjs` | The layout (head, header, footer), the pages, the guide list and guide pages, the root page and the 404 page. |
| `src/guides.mjs` | The guides: Markdown to HTML, links between guides and to screenshots made into the site's addresses, anchors, the guide order. |
| `content/guides/tr/*.md` | The Turkish guides. |
| `src/icons.mjs` | The line icons. |
| `src/config.mjs` | The languages, the pages and their order, `SITE_URL` / `REPO_URL`. |
| `src/assets/style.css` | The design: light and dark (the system's, or chosen with the button), narrow screens. |
| `src/assets/app.js` | Theme button, menu on narrow screens, the tour, the looks, the visitor's system on the download page, copy buttons. |

**The guides:** the English pages come straight from `docs/guide/*.md`; change a guide there. The Turkish file in
`content/guides/tr` follows it heading by heading: the same headings, levels and order (the build stops otherwise),
because each Turkish heading takes the anchor of the English heading at its place, so `tools.md#file-index` works in
both languages. A guide without a Turkish file is shown in English with a note. A new guide: its name in
`guideNames` (`src/guides.mjs`) and in `guide.groups` / `guide.items` of both language files.

**A new text:** add it to both language files at the same place. **A new page:** add its name to `pages` in
`config.mjs`, its texts (`meta`, `nav`) to both languages and a render function to `renderers` in `pages.mjs`.
**A new language:** a third file in `src/i18n`, its code in `languages` and an import in `build.mjs`.

## Hosting

Any static host works; give it the `dist` folder and the 404 page.

- **Container:** from the repository root,
  `docker build -f web/Dockerfile --build-arg SITE_URL=https://www.example.com -t nextcommander-web .`
  then `docker run -p 8080:8080 nextcommander-web`. nginx (unprivileged, port 8080) with `nginx.conf`: long caching
  for `/assets/`, security headers, the 404 page.
- **Netlify / Cloudflare Pages / Vercel / Azure Static Web Apps:** build command `cd web && npm ci && node build.mjs`, output folder
  `web/dist`, environment variable `SITE_URL`. Most of them serve `404.html` on their own.
- **Your own server:** copy `dist/` and use `nginx.conf` (change `root`).

The site loads the Inter and JetBrains Mono fonts from Google Fonts; nothing else comes from other hosts, and there
is no tracking.
