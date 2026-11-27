// Site-wide settings. SITE_URL and REPO_URL can be given at build time (see README.md).

export const siteUrl = (process.env.SITE_URL || "https://nextcommander.example.com").replace(/\/+$/, "");
export const repoUrl = (process.env.REPO_URL || "https://github.com/nextcommander/NextCommander").replace(/\/+$/, "");
export const releasesUrl = repoUrl + "/releases/latest";
export const guideUrl = (name) => `${repoUrl}/blob/main/docs/guide/${name}.md`;

export const languages = ["en", "tr"];
export const defaultLanguage = "en";

// The pages, in menu order. "" is the home page of a language.
export const pages = ["", "features", "connections", "plugins", "download", "faq"];
