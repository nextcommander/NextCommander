// The page templates. Plain template strings: each function takes the texts of one language and returns HTML.

import { languages, pages, siteUrl, repoUrl, releasesUrl, defaultLanguage } from "./config.mjs";
import { icon } from "./icons.mjs";
import { guideNames, renderGuide } from "./guides.mjs";

// screenshot sizes (set by build.mjs) so pictures keep their place while they load
let shotSizes = {};
export function setShotSizes(sizes) { shotSizes = sizes; }

const esc = (text) => String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
const fill = (pattern, ...values) => values.reduce((text, value, i) => text.replace(`{${i}}`, value), pattern);
const href = (lang, page) => `/${lang}/${page ? page + "/" : ""}`;

// a guide of the site, with an optional anchor: "tools#file-index"
let currentLang = defaultLanguage;
function guideUrl(target) {
  const [name, anchor] = target.split("#");
  return `/${currentLang}/guide/${name}/${anchor ? "#" + anchor : ""}`;
}

function shot(name, alt, { cls = "", eager = false, id = "" } = {}) {
  const size = shotSizes[name];
  const dims = size ? ` width="${size[0]}" height="${size[1]}"` : "";
  return `<img src="/screenshots/${name}" alt="${esc(alt)}"${dims}${cls ? ` class="${cls}"` : ""}${id ? ` id="${id}"` : ""} ${eager ? 'fetchpriority="high"' : 'loading="lazy"'} decoding="async">`;
}

function windowFrame(content, title = "NextCommander") {
  return `<figure class="window"><div class="window-bar" aria-hidden="true"><i></i><i></i><i></i><span>${esc(title)}</span></div>${content}</figure>`;
}

function sectionHead(eyebrow, title, lead, { center = false, h = "h2" } = {}) {
  return `<header class="section-head${center ? " center" : ""}">
    <p class="eyebrow">${esc(eyebrow)}</p>
    <${h}>${esc(title)}</${h}>
    ${lead ? `<p class="lead">${lead}</p>` : ""}
  </header>`;
}

// ---------------------------------------------------------------- layout

function layout({ lang, page, t, assets, body, jsonLd = "", meta = t.meta[page], path = page }) {
  const url = siteUrl + href(lang, path);
  const alternates = languages.map((other) => `<link rel="alternate" hreflang="${other}" href="${siteUrl + href(other, path)}">`).join("\n  ");
  return `<!doctype html>
<html lang="${lang}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>${esc(meta.title)}</title>
  <meta name="description" content="${esc(meta.description)}">
  <link rel="canonical" href="${url}">
  ${alternates}
  <link rel="alternate" hreflang="x-default" href="${siteUrl + href(defaultLanguage, path)}">
  <meta property="og:type" content="website">
  <meta property="og:site_name" content="NextCommander">
  <meta property="og:title" content="${esc(meta.title)}">
  <meta property="og:description" content="${esc(meta.description)}">
  <meta property="og:url" content="${url}">
  <meta property="og:image" content="${siteUrl}/screenshots/main-dark.png">
  <meta property="og:locale" content="${t.locale}">
  <meta name="twitter:card" content="summary_large_image">
  <meta name="theme-color" content="#f7f8fc" media="(prefers-color-scheme: light)">
  <meta name="theme-color" content="#0b1020" media="(prefers-color-scheme: dark)">
  <link rel="icon" href="/favicon.ico" sizes="any">
  <link rel="icon" href="/icon.svg" type="image/svg+xml">
  <link rel="apple-touch-icon" href="/apple-touch-icon.png">
  <link rel="manifest" href="/site.webmanifest">
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&family=JetBrains+Mono:wght@500&display=swap">
  <link rel="stylesheet" href="${assets["style.css"]}">
  <script>
    // the theme chosen before (else the system's), set before the page is drawn
    document.documentElement.classList.add("js");
    try { var theme = localStorage.getItem("nc-theme"); if (theme) document.documentElement.dataset.theme = theme; } catch (e) {}
  </script>
  <script src="${assets["app.js"]}" defer></script>${jsonLd}
</head>
<body data-page="${(page || "home").replace("/", "-")}">
  <a class="skip" href="#main">${esc(t.nav.skip)}</a>
  <div class="glow" aria-hidden="true"></div>
  ${header({ lang, page, t, path })}
  <main id="main">
${body}
  </main>
  ${footer({ lang, t })}
</body>
</html>
`;
}

function header({ lang, page, t, path }) {
  const links = pages.map((p) =>
    `<a href="${href(lang, p)}"${p === page ? ' aria-current="page"' : ""}>${esc(t.nav[p])}</a>`).join("");
  const switcher = languages.map((other) =>
    `<a href="${href(other, path)}" hreflang="${other}" lang="${other}" data-lang="${other}"${other === lang ? ' aria-current="true"' : ""}>${other.toUpperCase()}</a>`).join("");
  return `<header class="top">
    <div class="wrap top-inner">
      <a class="brand" href="${href(lang, "")}"><img src="/icon-32.png" width="28" height="28" alt="">NextCommander</a>
      <nav class="nav" id="nav" aria-label="${esc(t.nav.menu)}">${links}</nav>
      <div class="top-actions">
        <div class="lang" role="group" aria-label="${esc(t.nav.language)}">${switcher}</div>
        <button class="icon-button" id="theme" type="button" aria-label="${esc(t.nav.theme)}" title="${esc(t.nav.theme)}">${icon("sun", "sun")}${icon("moon", "moon")}</button>
        <a class="icon-button hide-small" href="${repoUrl}" aria-label="${esc(t.nav.github)}" title="${esc(t.nav.github)}">${icon("github")}</a>
        <a class="button primary small hide-small" href="${href(lang, "download")}">${esc(t.common.download)}</a>
        <button class="icon-button menu-button" id="menu" type="button" aria-controls="nav" aria-expanded="false" aria-label="${esc(t.nav.menu)}">${icon("menu")}</button>
      </div>
    </div>
  </header>`;
}

function footer({ lang, t }) {
  const product = pages.filter(Boolean).map((p) => `<li><a href="${href(lang, p)}">${esc(t.nav[p])}</a></li>`).join("");
  const guides = t.footer.guideLinks.map(([name, label]) => `<li><a href="${guideUrl(name)}">${esc(label)}</a></li>`).join("") +
    `<li><a href="${href(lang, "guide")}">${esc(t.guide.allGuides)}</a></li>`;
  return `<footer class="foot">
    <div class="wrap foot-grid">
      <div>
        <a class="brand" href="${href(lang, "")}"><img src="/icon-32.png" width="28" height="28" alt="">NextCommander</a>
        <p class="muted">${esc(t.footer.tagline)}</p>
      </div>
      <nav aria-label="${esc(t.footer.product)}"><h2>${esc(t.footer.product)}</h2><ul>${product}</ul></nav>
      <nav aria-label="${esc(t.footer.guides)}"><h2>${esc(t.footer.guides)}</h2><ul>${guides}</ul></nav>
      <nav aria-label="${esc(t.footer.project)}"><h2>${esc(t.footer.project)}</h2><ul>
        <li><a href="${repoUrl}">${esc(t.footer.source)}</a></li>
        <li><a href="${repoUrl}/releases">${esc(t.footer.releases)}</a></li>
        <li><a href="${repoUrl}/issues">${esc(t.footer.issues)}</a></li>
      </ul></nav>
    </div>
    <div class="wrap foot-bottom muted">© ${new Date().getFullYear()} NextCommander</div>
  </footer>`;
}

// ---------------------------------------------------------------- pages

const renderers = { "": home, features, connections, plugins, guide: guideIndex, download, faq };

export function renderPage({ lang, page, t, assets }) {
  currentLang = lang;
  const jsonLd = page === "" ? `\n  <script type="application/ld+json">${JSON.stringify({
    "@context": "https://schema.org", "@type": "SoftwareApplication", name: "NextCommander",
    applicationCategory: "UtilitiesApplication", operatingSystem: "Windows, Linux, macOS",
    description: t.meta[""].description, url: siteUrl + href(lang, ""), image: `${siteUrl}/screenshots/main-dark.png`,
    downloadUrl: releasesUrl, inLanguage: languages,
  })}</script>` : "";
  return layout({ lang, page, t, assets, body: renderers[page]({ lang, t }), jsonLd });
}

function home({ lang, t }) {
  const h = t.home;
  const fkeys = h.fkeys.map(([key, label]) => `<li><kbd>${key}</kbd>${esc(label)}</li>`).join("");
  const stats = h.stats.map(([n, label]) => `<div class="stat"><strong>${esc(n)}</strong><span>${esc(label)}</span></div>`).join("");
  const why = h.why.items.map((item) => `<article class="card reveal">
        <div class="card-icon">${icon(item.icon)}</div>
        <h3>${esc(item.title)}</h3>
        <p>${esc(item.body)}</p>
      </article>`).join("\n      ");
  const tourRows = h.tour.items.map((item, i) => `<button type="button" class="row" role="option" id="tour-${i}" aria-selected="${i === 0}"
          data-shot="${item.shot}" data-name="${esc(item.name)}" data-body="${esc(item.body)}"><span>${esc(item.name)}</span>${item.key ? `<kbd>${esc(item.key)}</kbd>` : `<small>${esc(item.where)}</small>`}</button>`).join("\n        ");
  const first = h.tour.items[0];
  const chips = h.connections.chips.map((chip) => `<li>${esc(chip)}</li>`).join("");
  const looks = h.looks.options.map(([label, file], i) =>
    `<button type="button" role="tab" aria-selected="${i === 0}" data-shot="${file}">${esc(label)}</button>`).join("");
  return `
    <section class="hero wrap">
      <p class="pill reveal">${esc(h.pill)}</p>
      <h1 class="reveal">${esc(h.titleA)}<br><span class="gradient">${esc(h.titleB)}</span></h1>
      <p class="lead reveal">${esc(h.lead)}</p>
      <div class="actions reveal">
        <a class="button primary" id="hero-download" href="/${lang}/download/" data-pattern="${esc(t.common.downloadFor)}">${icon("download")}${esc(t.common.download)}</a>
        <a class="button" href="${guideUrl("getting-started")}">${esc(t.common.getStarted)}</a>
      </div>
      <div class="hero-shot reveal">
        ${windowFrame(shot("main-dark.png", fill(t.common.screenshotOf, "NextCommander"), { cls: "dark-only", eager: true }) + shot("main-light.png", fill(t.common.screenshotOf, "NextCommander"), { cls: "light-only", eager: true }))}
        <ul class="fkeys" aria-label="F3 … F8">${fkeys}</ul>
      </div>
    </section>

    <section class="wrap stats reveal">${stats}</section>

    <section class="wrap block">
      ${sectionHead(h.why.eyebrow, h.why.title, esc(h.why.lead))}
      <div class="grid four">
      ${why}
      </div>
      <p class="more reveal"><a class="link-arrow" href="${href(lang, "features")}">${esc(t.common.allFeatures)} ${icon("arrow")}</a></p>
    </section>

    <section class="wrap block" id="tour">
      ${sectionHead(h.tour.eyebrow, h.tour.title, h.tour.lead)}
      <div class="panes reveal" data-tour>
        <div class="pane list-pane">
          <div class="pane-head"><span>${esc(h.tour.listTitle)}</span><span>${esc(h.tour.keyTitle)}</span></div>
          <div class="rows" role="listbox" tabindex="0" aria-label="${esc(h.tour.title)}" aria-activedescendant="tour-0">
        ${tourRows}
          </div>
        </div>
        <div class="pane preview-pane">
          <div class="preview-img">${shot(first.shot, first.name, { id: "tour-img" })}</div>
          <div class="preview-text"><h3 id="tour-title">${esc(first.name)}</h3><p id="tour-body">${esc(first.body)}</p></div>
        </div>
      </div>
    </section>

    <section class="wrap block">
      ${sectionHead(h.connections.eyebrow, h.connections.title, h.connections.lead)}
      <ul class="chips reveal">${chips}</ul>
      <p class="more reveal"><a class="link-arrow" href="${href(lang, "connections")}">${esc(t.common.seeConnections)} ${icon("arrow")}</a></p>
    </section>

    <section class="wrap block" id="looks">
      ${sectionHead(h.looks.eyebrow, h.looks.title, esc(h.looks.lead))}
      <div class="segmented reveal" role="tablist">${looks}</div>
      <div class="reveal">${windowFrame(shot(h.looks.options[0][1], h.looks.options[0][0], { id: "look-img" }))}</div>
    </section>

    <section class="wrap block">
      <div class="cta reveal">
        <h2>${esc(h.cta.title)}</h2>
        <p>${esc(h.cta.lead)}</p>
        <div class="actions">
          <a class="button primary" href="${href(lang, "download")}">${icon("download")}${esc(t.common.download)}</a>
          <a class="button ghost" href="${href(lang, "plugins")}">${esc(t.common.seePlugins)}</a>
        </div>
      </div>
    </section>`;
}

function pageHero(head) {
  return `<section class="wrap page-hero">${sectionHead(head.eyebrow, head.title, esc(head.lead), { h: "h1" })}</section>`;
}

function features({ t }) {
  const f = t.features;
  const toc = f.sections.map((s) => `<li><a href="#${s.id}">${icon(s.icon)}${esc(s.title)}</a></li>`).join("");
  const sections = f.sections.map((s) => `
      <section class="feature reveal" id="${s.id}">
        <div class="feature-head">
          <div class="card-icon">${icon(s.icon)}</div>
          <div><h2>${esc(s.title)}</h2><p class="lead">${esc(s.lead)}</p></div>
        </div>
        <div class="feature-shot">${windowFrame(shot(s.shot, s.title))}</div>
        <ul class="checks">${s.points.map((p) => `<li>${icon("check")}<span>${p}</span></li>`).join("")}</ul>
        <a class="link-arrow" href="${guideUrl(s.guide)}">${esc(t.common.readGuide)} ${icon("arrow")}</a>
      </section>`).join("");
  return `
    ${pageHero(f)}
    <div class="wrap with-toc">
      <nav class="toc" aria-label="${esc(t.common.onThisPage)}"><p>${esc(t.common.onThisPage)}</p><ul>${toc}</ul></nav>
      <div class="features">${sections}
      </div>
    </div>`;
}

function connections({ t }) {
  const c = t.connections;
  const groups = c.groups.map((g) => `<article class="card group reveal">
        <div class="card-head"><div class="card-icon">${icon(g.icon)}</div><h2>${esc(g.title)}</h2></div>
        <dl>${g.items.map(([name, detail]) => `<dt>${esc(name)}</dt><dd>${esc(detail)}</dd>`).join("")}</dl>
      </article>`).join("\n      ");
  const extras = c.extras.items.map((e) => `<article class="card reveal">
        <div class="card-icon">${icon(e.icon)}</div><h3>${esc(e.title)}</h3><p>${esc(e.body)}</p>
      </article>`).join("\n      ");
  return `
    ${pageHero(c)}
    <section class="wrap">
      <div class="grid three">
      ${groups}
      </div>
    </section>
    <section class="wrap block">
      <div class="reveal">${windowFrame(shot(c.shot, c.title))}</div>
    </section>
    <section class="wrap block">
      ${sectionHead("", c.extras.title, "")}
      <div class="grid four">
      ${extras}
      </div>
      <p class="more reveal"><a class="link-arrow" href="${guideUrl("connections")}">${esc(t.common.readGuide)} ${icon("arrow")}</a></p>
    </section>`;
}

function plugins({ t }) {
  const p = t.plugins;
  const cards = p.items.map((item) => `<article class="card plugin reveal">
        <div class="card-head"><div class="card-icon">${icon(item.icon)}</div><h2>${esc(item.name)}</h2></div>
        <p>${esc(item.body)}</p>
        <p class="badge${item.needs ? "" : " ok"}">${item.needs ? `${esc(p.needsLabel)} <code>${esc(item.needs)}</code>` : esc(p.builtIn)}</p>
      </article>`).join("\n      ");
  const own = p.own.points.map(([name, body]) => `<div class="own-item"><h3>${esc(name)}</h3><p>${esc(body)}</p></div>`).join("");
  return `
    ${pageHero(p)}
    <section class="wrap">
      <div class="grid three">
      ${cards}
      </div>
    </section>
    <section class="wrap block">
      <div class="panel-box reveal">
        ${sectionHead("C# · .NET", p.own.title, esc(p.own.lead))}
        <div class="own-grid">${own}</div>
        <p class="more"><a class="link-arrow" href="${guideUrl("plugins")}">${esc(t.common.readGuide)} ${icon("arrow")}</a></p>
      </div>
    </section>`;
}

function codeBox(command, t) {
  return `<div class="code"><code>${esc(command)}</code><button type="button" class="copy" data-copied="${esc(t.common.copied)}">${esc(t.common.copy)}</button></div>`;
}

function download({ t }) {
  const d = t.download;
  const platforms = d.platforms.map((p) => `<article class="card platform reveal" data-os="${p.os}">
        <div class="card-head"><div class="card-icon">${icon(p.os)}</div><div><h2>${esc(p.name)}</h2><p class="muted">${esc(p.archs)}</p></div></div>
        <p class="yours-label">${esc(d.yours)}</p>
        <ul class="files">${p.files.map(([file, note]) => `<li><code>${esc(file)}</code><span>${esc(note)}</span></li>`).join("")}</ul>
        ${p.command ? codeBox(p.command, t) : ""}
        ${p.note ? `<p class="note">${esc(p.note)}</p>` : ""}
        <a class="button primary" href="${releasesUrl}">${icon("download")}${esc(fill(d.get, p.name))}</a>
      </article>`).join("\n      ");
  const helpers = d.helpers.items.map(([name, use]) => `<tr><th scope="row">${esc(name)}</th><td>${esc(use)}</td></tr>`).join("");
  const after = d.after.items.map(([title, body]) => `<div class="own-item"><h3>${esc(title)}</h3><p>${esc(body)}</p></div>`).join("");
  return `
    ${pageHero(d)}
    <section class="wrap">
      <div class="grid three platforms">
      ${platforms}
      </div>
      <p class="more reveal"><a class="link-arrow" href="${releasesUrl}">${esc(t.common.latestRelease)} ${icon("arrow")}</a></p>
    </section>
    <section class="wrap block two-col">
      <div class="reveal">
        ${sectionHead("", d.helpers.title, esc(d.helpers.lead))}
        <div class="table-wrap"><table class="table"><tbody>${helpers}</tbody></table></div>
      </div>
      <div class="reveal">
        ${sectionHead("", d.verify.title, esc(d.verify.body))}
        <p class="label">Windows (PowerShell)</p>
        ${codeBox(d.verify.windows, t)}
        <p class="label">Linux · macOS</p>
        ${codeBox(d.verify.unix, t)}
      </div>
    </section>
    <section class="wrap block">
      <div class="panel-box reveal">
        ${sectionHead("", d.after.title, "")}
        <div class="own-grid three">${after}</div>
      </div>
    </section>`;
}

function faq({ t }) {
  const f = t.faq;
  const groups = f.groups.map((g) => `<section class="faq-group reveal">
        <h2>${esc(g.title)}</h2>
        ${g.items.map(([q, a]) => `<details><summary>${esc(q)}${icon("plus")}</summary><p>${esc(a)}</p></details>`).join("\n        ")}
      </section>`).join("\n      ");
  return `
    ${pageHero(f)}
    <div class="wrap faq">
      ${groups}
    </div>`;
}

// ---------------------------------------------------------------- guides

function guideIndex({ lang, t }) {
  const g = t.guide;
  const groups = g.groups.map(([title, names]) => `<section class="guide-group reveal">
        <h2>${esc(title)}</h2>
        <div class="grid three">${names.map((name) => {
          const [itemTitle, description] = g.items[name];
          return `<a class="card guide-card" href="${guideUrl(name)}" data-search="${esc((itemTitle + " " + description).toLowerCase())}">
            <h3>${esc(itemTitle)} ${icon("arrow")}</h3><p>${esc(description)}</p></a>`;
        }).join("")}</div>
      </section>`).join("\n      ");
  return `
    ${pageHero(g)}
    <div class="wrap">
      <label class="search reveal">${icon("search")}<input type="search" id="guide-filter" placeholder="${esc(g.filter)}" aria-label="${esc(g.filter)}"></label>
      <p class="muted no-match" id="guide-none" hidden>${esc(g.noMatch)}</p>
      ${groups}
    </div>`;
}

// One guide: the guide list on the left, the guide in the middle, its sections on the right.
export function renderGuidePage({ lang, t, assets, name }) {
  currentLang = lang;
  const g = t.guide;
  const guide = renderGuide(lang, name);
  const position = guideNames.indexOf(name);
  const previous = guideNames[position - 1];
  const next = guideNames[position + 1];
  const nav = g.groups.map(([title, names]) => `<p>${esc(title)}</p><ul>${names.map((other) =>
    `<li><a href="${guideUrl(other)}"${other === name ? ' aria-current="page"' : ""}>${esc(g.items[other][0])}</a></li>`).join("")}</ul>`).join("");
  const toc = guide.toc.map((h) => `<li class="d${h.depth}"><a href="#${h.id}">${esc(h.text)}</a></li>`).join("");
  const pager = `<nav class="pager" aria-label="${esc(g.allGuides)}">
        ${previous ? `<a class="card" href="${guideUrl(previous)}" rel="prev"><small>${esc(g.previous)}</small><strong>${esc(g.items[previous][0])}</strong></a>` : "<span></span>"}
        ${next ? `<a class="card next" href="${guideUrl(next)}" rel="next"><small>${esc(g.next)}</small><strong>${esc(g.items[next][0])}</strong></a>` : "<span></span>"}
      </nav>`;
  const body = `
    <div class="wrap guide-layout">
      <nav class="guide-nav" aria-label="${esc(g.allGuides)}">
        <a class="back" href="${href(lang, "guide")}">${esc(g.allGuides)}</a>
        ${nav}
      </nav>
      <article class="prose">
        <p class="eyebrow">${esc(g.eyebrow)}</p>
        <h1>${esc(guide.title)}</h1>
        ${guide.translated ? "" : `<p class="note">${esc(g.untranslated)}</p>`}
        ${toc ? `<details class="mobile-toc"><summary>${esc(g.onThisPage)}${icon("plus")}</summary><ul>${toc}</ul></details>` : ""}
        ${guide.html}
        ${pager}
      </article>
      <aside class="guide-toc">${toc ? `<p>${esc(g.onThisPage)}</p><ul>${toc}</ul>` : ""}</aside>
    </div>`;
  const meta = { title: `${guide.title} — NextCommander`, description: g.items[name][1] };
  return layout({ lang, page: "guide", t, assets, body, meta, path: `guide/${name}` });
}

// ---------------------------------------------------------------- root and 404

// The root page sends the visitor to their language: the one chosen before, else the browser's, else English.
export function renderRoot({ texts, assets }) {
  const choices = languages.map((lang) => `<a class="button" href="/${lang}/" hreflang="${lang}" lang="${lang}">${esc(texts[lang].name)}</a>`).join("");
  return `<!doctype html>
<html lang="${defaultLanguage}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>NextCommander</title>
  <meta name="description" content="${esc(texts[defaultLanguage].meta[""].description)}">
  <link rel="canonical" href="${siteUrl}/${defaultLanguage}/">
  ${languages.map((lang) => `<link rel="alternate" hreflang="${lang}" href="${siteUrl}/${lang}/">`).join("\n  ")}
  <link rel="alternate" hreflang="x-default" href="${siteUrl}/">
  <link rel="icon" href="/favicon.ico" sizes="any">
  <script>
    (function () {
      var known = ${JSON.stringify(languages)}, chosen = null;
      try { chosen = localStorage.getItem("nc-lang"); } catch (e) {}
      if (known.indexOf(chosen) < 0) {
        var wanted = (navigator.languages || [navigator.language || ""]).map(function (l) { return String(l).slice(0, 2).toLowerCase(); });
        chosen = wanted.filter(function (l) { return known.indexOf(l) >= 0; })[0] || ${JSON.stringify(defaultLanguage)};
      }
      location.replace("/" + chosen + "/" + location.search + location.hash);
    })();
  </script>
  <link rel="stylesheet" href="${assets["style.css"]}">
</head>
<body>
  <main class="wrap center-page">
    <img src="/icon-256.png" width="96" height="96" alt="">
    <h1>NextCommander</h1>
    <p class="muted">${languages.map((lang) => esc(texts[lang].root.choose)).join(" · ")}</p>
    <div class="actions">${choices}</div>
  </main>
</body>
</html>
`;
}

export function renderNotFound({ texts, assets }) {
  const parts = languages.map((lang) => {
    const n = texts[lang].notFound;
    return `<section lang="${lang}"><h1>${esc(n.title)}</h1><p class="muted">${esc(n.body)}</p><a class="button primary" href="/${lang}/">${esc(n.back)}</a></section>`;
  }).join("");
  return `<!doctype html>
<html lang="${defaultLanguage}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>404 — NextCommander</title>
  <meta name="robots" content="noindex">
  <link rel="icon" href="/favicon.ico" sizes="any">
  <link rel="stylesheet" href="${assets["style.css"]}">
  <script>try { var t = localStorage.getItem("nc-theme"); if (t) document.documentElement.dataset.theme = t; } catch (e) {}</script>
</head>
<body>
  <div class="glow" aria-hidden="true"></div>
  <main class="wrap center-page">
    <img src="/icon-256.png" width="96" height="96" alt="">
    <p class="big-code gradient">404</p>
    <div class="not-found">${parts}</div>
  </main>
</body>
</html>
`;
}
