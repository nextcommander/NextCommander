// The user guides as pages of the site. English comes straight from docs/guide/*.md, so the site always has the
// guides of the repository; Turkish from web/content/guides/tr/*.md. A translation keeps the headings of the
// English guide in the same order: each Turkish heading gets the anchor of the English one at its place, so links
// between guides (tools.md#file-index) work in both languages.

import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { Marked } from "marked";
import { repoUrl } from "./config.mjs";

// the reading order of the guides (also the order of the guide list)
export const guideNames = [
  "getting-started", "keyboard-shortcuts", "tools", "archives", "connections", "configuration",
  "themes", "icon-packs", "languages", "scripts", "plugins", "settings-folder",
];

let repo = "";
export function setRepositoryRoot(path) { repo = path; }

function sourceOf(lang, name) {
  const english = join(repo, "docs/guide", `${name}.md`);
  if (lang === "en") return { text: readFileSync(english, "utf8"), translated: true };
  const translation = join(repo, "web/content/guides", lang, `${name}.md`);
  return existsSync(translation)
    ? { text: readFileSync(translation, "utf8"), translated: true }
    : { text: readFileSync(english, "utf8"), translated: false }; // shown in English with a note
}

// GitHub's anchors: lower case, punctuation dropped, spaces to hyphens, a number after repeats
function slugger() {
  const seen = new Map();
  return (text) => {
    const base = text.toLowerCase().trim().replace(/[^\p{L}\p{M}\p{N}\p{Pc} -]/gu, "").replace(/ /g, "-");
    const count = seen.get(base) || 0;
    seen.set(base, count + 1);
    return count ? `${base}-${count}` : base;
  };
}

const plain = (tokens) => tokens.map((t) => t.tokens ? plain(t.tokens) : (t.text ?? t.raw ?? "")).join("");

// the headings of a Markdown text, outside code blocks
function headingsOf(text) {
  const headings = [];
  new Marked().walkTokens(new Marked().lexer(text), (token) => {
    if (token.type === "heading") headings.push({ depth: token.depth, text: plain(token.tokens) });
  });
  return headings;
}

// The "Back to the README" line and the guide's own list of its sections go: the page has its own navigation.
function trim(text) {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  const out = [];
  let skippingContents = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\[[^\]]+\]\(\.\.\/\.\.\/README\.md\)\s*$/.test(line)) continue;
    // a list right at the top whose items only point into the page itself
    if (!skippingContents && /^\s*[-*] \[[^\]]+\]\(#[^)]+\)\s*$/.test(line) && !out.some((l) => l.trim() && !l.startsWith("#"))) {
      skippingContents = true;
    }
    if (skippingContents) {
      if (/^\s*[-*] \[[^\]]+\]\(#[^)]+\)\s*$/.test(line)) continue;
      if (line.trim() === "") continue;
      skippingContents = false;
    }
    out.push(line);
  }
  return out.join("\n");
}

function rewriteHref(href, lang) {
  if (!href || /^(https?:|mailto:)/.test(href) || href.startsWith("#")) return href;
  const [path, hash] = href.split("#");
  const anchor = hash ? "#" + hash : "";
  let m;
  if ((m = path.match(/^([a-z0-9-]+)\.md$/))) return `/${lang}/guide/${m[1]}/${anchor}`;
  if (/^\.\.\/\.\.\/README\.md$/.test(path)) return `/${lang}/${anchor}`;
  if ((m = path.match(/^\.\.\/screenshots\/(.+)$/))) return `/screenshots/${m[1]}`;
  if ((m = path.match(/^\.\.\/\.\.\/(.+)$/))) return `${repoUrl}/tree/main/${m[1]}${anchor}`; // source code
  if ((m = path.match(/^\.\.\/([a-z0-9-]+\.md)$/))) return `${repoUrl}/blob/main/docs/${m[1]}${anchor}`;
  return href;
}

export function renderGuide(lang, name) {
  const { text, translated } = sourceOf(lang, name);
  const english = headingsOf(trim(sourceOf("en", name).text));
  const slug = slugger();
  const ids = english.map((h) => slug(h.text));
  const markdown = trim(text);
  const own = headingsOf(markdown);
  if (own.length !== english.length || own.some((h, i) => h.depth !== english[i].depth)) {
    throw new Error(`Guide ${lang}/${name}.md: its headings do not follow the English guide (${own.length} / ${english.length}); translate heading by heading.`);
  }

  let title = "";
  const toc = [];
  let index = 0;
  const marked = new Marked({
    gfm: true,
    walkTokens(token) {
      if (token.type === "link" || token.type === "image") token.href = rewriteHref(token.href, lang);
    },
    renderer: {
      heading({ tokens, depth }) {
        const id = ids[index++];
        const inner = this.parser.parseInline(tokens);
        if (depth === 1) { title = plain(tokens); return ""; } // the page shows it in its own header
        if (depth <= 3) toc.push({ depth, id, text: plain(tokens) });
        return `<h${depth} id="${id}"><a class="anchor" href="#${id}" aria-hidden="true" tabindex="-1">#</a>${inner}</h${depth}>\n`;
      },
      image({ href, title: imageTitle, text: alt }) {
        return `<img src="${href}" alt="${alt.replace(/"/g, "&quot;")}" loading="lazy" decoding="async"${imageTitle ? ` title="${imageTitle}"` : ""}>`;
      },
      code({ text: codeText, lang: codeLang }) {
        const escaped = codeText.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        const label = codeLang ? `<span class="code-lang">${codeLang}</span>` : "";
        return `<div class="codeblock">${label}<pre><code>${escaped}</code></pre></div>\n`;
      },
    },
  });
  let html = marked.parse(markdown);
  html = html.replace(/<table>/g, '<div class="table-wrap"><table class="table">').replace(/<\/table>/g, "</table></div>");
  // a link to the guide's own page goes to its anchor only
  html = html.replace(new RegExp(`href="/${lang}/guide/${name}/#`, "g"), 'href="#');
  const lead = (markdown.split("\n").find((line) => line.trim() && !line.startsWith("#")) || "").replace(/[*_`[\]]|\([^)]*\)/g, "");
  return { title, html, toc, translated, description: lead.slice(0, 160) };
}
