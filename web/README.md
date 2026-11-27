# NextCommander product site

The product site of NextCommander in English and Turkish, hosted apart from the GitHub Pages page in
[`site/`](../site). A static site: plain HTML, CSS and a little JavaScript, built by a Node script without packages.

## Build and look at it

Needs Node.js 20 or later; nothing to install (`npm install` is not needed).

```sh
cd web
node build.mjs            # writes dist/
node build.mjs --check    # also checks every internal link and picture
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
  en/  tr/            one folder per language: index.html, features/, connections/, plugins/, download/, faq/
  assets/             style.<hash>.css, app.<hash>.js (the hash changes with the content: cache them for long)
  screenshots/        copied from docs/screenshots, so the site always shows the program as it is
  404.html  sitemap.xml  robots.txt  site.webmanifest  favicon.ico  icon.svg  icon-*.png  apple-touch-icon.png
```

Pages link with absolute paths (`/en/features/`), so the site must be served at the root of its domain or subdomain.

## Where things are

| File | Contents |
|---|---|
| `src/i18n/en.mjs`, `src/i18n/tr.mjs` | Every text of the site. Both files have the same keys and the same list lengths; the build stops when they differ. |
| `src/pages.mjs` | The layout (head, header, footer) and the six pages, the root page and the 404 page. |
| `src/icons.mjs` | The line icons. |
| `src/config.mjs` | The languages, the pages and their order, `SITE_URL` / `REPO_URL`. |
| `src/assets/style.css` | The design: light and dark (the system's, or chosen with the button), narrow screens. |
| `src/assets/app.js` | Theme button, menu on narrow screens, the tour, the looks, the visitor's system on the download page, copy buttons. |

**A new text:** add it to both language files at the same place. **A new page:** add its name to `pages` in
`config.mjs`, its texts (`meta`, `nav`) to both languages and a render function to `renderers` in `pages.mjs`.
**A new language:** a third file in `src/i18n`, its code in `languages` and an import in `build.mjs`.

## Hosting

Any static host works; give it the `dist` folder and the 404 page.

- **Container:** from the repository root,
  `docker build -f web/Dockerfile --build-arg SITE_URL=https://www.example.com -t nextcommander-web .`
  then `docker run -p 8080:8080 nextcommander-web`. nginx (unprivileged, port 8080) with `nginx.conf`: long caching
  for `/assets/`, security headers, the 404 page.
- **Netlify / Cloudflare Pages / Vercel / Azure Static Web Apps:** build command `node web/build.mjs`, output folder
  `web/dist`, environment variable `SITE_URL`. Most of them serve `404.html` on their own.
- **Your own server:** copy `dist/` and use `nginx.conf` (change `root`).

The site loads the Inter and JetBrains Mono fonts from Google Fonts; nothing else comes from other hosts, and there
is no tracking.
