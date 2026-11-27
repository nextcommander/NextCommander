// Builds the site into dist/: one folder per language, one index.html per page, the assets, the screenshots,
// a language-choosing root page, sitemap.xml, robots.txt and 404.html. No dependencies: `node build.mjs`.
// `node build.mjs --check` builds and then checks every internal link and picture.

import { cp, mkdir, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { languages, pages, siteUrl, defaultLanguage } from "./src/config.mjs";
import { renderPage, renderRoot, renderNotFound, setShotSizes } from "./src/pages.mjs";
import en from "./src/i18n/en.mjs";
import tr from "./src/i18n/tr.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, "..");
const out = join(here, "dist");
const texts = { en, tr };

async function main() {
  checkTexts(en, tr);
  await rm(out, { recursive: true, force: true });
  await mkdir(out, { recursive: true });

  // assets carry a short hash of their content so they can be cached for long
  const assets = {};
  for (const name of await readdir(join(here, "src/assets"))) {
    const content = await readFile(join(here, "src/assets", name));
    const hash = createHash("sha256").update(content).digest("hex").slice(0, 10);
    const dot = name.lastIndexOf(".");
    const hashed = `${name.slice(0, dot)}.${hash}${name.slice(dot)}`;
    await mkdir(join(out, "assets"), { recursive: true });
    await writeFile(join(out, "assets", hashed), content);
    assets[name] = "/assets/" + hashed;
  }

  // screenshots and icons come from the repository itself, so the site always shows the program as it is
  await cp(join(repo, "docs/screenshots"), join(out, "screenshots"), { recursive: true });
  const sizes = {};
  for (const name of await readdir(join(out, "screenshots"))) {
    if (!name.endsWith(".png")) continue;
    const head = await readFile(join(out, "screenshots", name));
    sizes[name] = [head.readUInt32BE(16), head.readUInt32BE(20)]; // the PNG header's width and height
  }
  setShotSizes(sizes);
  for (const size of [32, 180, 256, 512]) {
    const source = join(repo, "build/icons/png", `${size === 180 ? 256 : size}.png`);
    await cp(source, join(out, size === 180 ? "apple-touch-icon.png" : `icon-${size}.png`));
  }
  await cp(join(repo, "build/icons/nextcommander.svg"), join(out, "icon.svg"));
  await cp(join(repo, "build/icons/nextcommander.ico"), join(out, "favicon.ico"));

  for (const lang of languages) {
    for (const page of pages) {
      const html = renderPage({ lang, page, t: texts[lang], texts, assets });
      const folder = join(out, lang, page);
      await mkdir(folder, { recursive: true });
      await writeFile(join(folder, "index.html"), html);
    }
  }
  await writeFile(join(out, "index.html"), renderRoot({ texts, assets }));
  await writeFile(join(out, "404.html"), renderNotFound({ texts, assets }));
  await writeFile(join(out, "robots.txt"), `User-agent: *\nAllow: /\n\nSitemap: ${siteUrl}/sitemap.xml\n`);
  await writeFile(join(out, "sitemap.xml"), sitemap());
  await writeFile(join(out, "site.webmanifest"), JSON.stringify({
    name: "NextCommander", short_name: "NextCommander", start_url: `/${defaultLanguage}/`, display: "browser",
    background_color: "#0b1020", theme_color: "#4f46e5",
    icons: [{ src: "/icon-256.png", sizes: "256x256", type: "image/png" }, { src: "/icon-512.png", sizes: "512x512", type: "image/png" }],
  }, null, 2));

  const files = await count(out);
  console.log(`Built ${languages.length * pages.length} pages (${languages.join(", ")}), ${files} files in ${relative(process.cwd(), out) || "."} for ${siteUrl}`);
  if (process.argv.includes("--check")) await checkLinks();
}

function sitemap() {
  const today = new Date().toISOString().slice(0, 10);
  const entries = pages.map((page) => languages.map((lang) => {
    const alternates = languages.map((other) =>
      `    <xhtml:link rel="alternate" hreflang="${other}" href="${siteUrl}/${other}/${page ? page + "/" : ""}"/>`).join("\n");
    return `  <url>\n    <loc>${siteUrl}/${lang}/${page ? page + "/" : ""}</loc>\n    <lastmod>${today}</lastmod>\n${alternates}\n` +
      `    <xhtml:link rel="alternate" hreflang="x-default" href="${siteUrl}/${defaultLanguage}/${page ? page + "/" : ""}"/>\n  </url>`;
  }).join("\n")).join("\n");
  return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">\n${entries}\n</urlset>\n`;
}

// Both languages must have the same texts: a key missing in one would show as "undefined" on its pages.
function checkTexts(a, b, path = "") {
  if (Array.isArray(a) !== Array.isArray(b)) throw new Error(`Text shapes differ at ${path}`);
  if (typeof a !== "object" || a === null) return;
  if (Array.isArray(a) && a.length !== b.length) throw new Error(`Lists differ in length at ${path} (${a.length} / ${b.length})`);
  for (const key of new Set([...Object.keys(a), ...Object.keys(b)])) {
    if (!(key in a) || !(key in b)) throw new Error(`Text "${path}${key}" is missing in ${key in a ? "tr" : "en"}`);
    checkTexts(a[key], b[key], `${path}${key}.`);
  }
}

async function count(folder) {
  let n = 0;
  for (const entry of await readdir(folder, { withFileTypes: true })) {
    n += entry.isDirectory() ? await count(join(folder, entry.name)) : 1;
  }
  return n;
}

// Every href / src starting with "/" must point at a file of the output.
async function checkLinks() {
  const problems = [];
  async function walk(folder) {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
      const path = join(folder, entry.name);
      if (entry.isDirectory()) { await walk(path); continue; }
      if (!entry.name.endsWith(".html")) continue;
      const html = await readFile(path, "utf8");
      for (const [, url] of html.matchAll(/(?:href|src|srcset)="(\/[^"#?]*)/g)) {
        let target = join(out, decodeURI(url));
        if (url.endsWith("/")) target = join(target, "index.html");
        if (!existsSync(target) || (await stat(target)).isDirectory()) problems.push(`${relative(out, path)}: ${url}`);
      }
      for (const [, id] of html.matchAll(/href="#([^"]+)"/g)) {
        if (!html.includes(`id="${id}"`)) problems.push(`${relative(out, path)}: #${id}`);
      }
    }
  }
  await walk(out);
  if (problems.length) {
    console.error("Broken links:\n  " + [...new Set(problems)].join("\n  "));
    process.exit(1);
  }
  console.log("Links checked: all internal links and pictures are there.");
}

main().catch((error) => { console.error(error); process.exit(1); });
