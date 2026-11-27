// A small static server for dist/, to look at the built site: `node serve.mjs` (PORT, default 8080).
// Production hosting is any static host; see README.md.

import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { dirname, extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "dist");
const port = Number(process.env.PORT || 8080);
const types = {
  ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon", ".xml": "application/xml",
  ".txt": "text/plain; charset=utf-8", ".webmanifest": "application/manifest+json",
};

createServer(async (request, response) => {
  const path = normalize(decodeURIComponent(new URL(request.url, "http://x").pathname)).replace(/^(\.\.[\\/])+/, "");
  let file = join(root, path);
  try {
    if ((await stat(file)).isDirectory()) {
      if (!request.url.split("?")[0].endsWith("/")) {
        response.writeHead(301, { Location: request.url.replace(/(\?|$)/, "/$1") });
        return response.end();
      }
      file = join(file, "index.html");
    }
    const body = await readFile(file);
    response.writeHead(200, { "Content-Type": types[extname(file)] || "application/octet-stream" });
    response.end(body);
  } catch {
    response.writeHead(404, { "Content-Type": types[".html"] });
    response.end(await readFile(join(root, "404.html")).catch(() => "Not found"));
  }
}).listen(port, () => console.log(`NextCommander site on http://localhost:${port}/`));
