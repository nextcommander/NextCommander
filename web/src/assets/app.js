// The site's behaviour. Every part checks that its elements are on the page; without JavaScript the pages still work.
(function () {
  "use strict";
  const root = document.documentElement;
  const remember = (key, value) => { try { localStorage.setItem(key, value); } catch (e) { /* private window */ } };

  // light / dark: the button switches and remembers it
  const themeButton = document.getElementById("theme");
  if (themeButton) {
    themeButton.addEventListener("click", () => {
      const dark = root.dataset.theme ? root.dataset.theme === "dark" : matchMedia("(prefers-color-scheme: dark)").matches;
      root.dataset.theme = dark ? "light" : "dark";
      remember("nc-theme", root.dataset.theme);
    });
  }

  // the menu on narrow screens
  const menu = document.getElementById("menu");
  const nav = document.getElementById("nav");
  if (menu && nav) {
    menu.addEventListener("click", () => {
      const open = nav.classList.toggle("open");
      menu.setAttribute("aria-expanded", String(open));
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && nav.classList.contains("open")) { nav.classList.remove("open"); menu.setAttribute("aria-expanded", "false"); menu.focus(); }
    });
  }

  // a language picked by hand wins over the browser's next time the root page is opened
  document.querySelectorAll(".lang a[data-lang]").forEach((link) => {
    link.addEventListener("click", () => remember("nc-lang", link.dataset.lang));
  });

  // the tour: a file list with a cursor, the line under it shown on the right
  const tour = document.querySelector("[data-tour]");
  if (tour) {
    const list = tour.querySelector("[role=listbox]");
    const rows = Array.from(list.querySelectorAll(".row"));
    const img = document.getElementById("tour-img");
    const title = document.getElementById("tour-title");
    const body = document.getElementById("tour-body");
    let current = 0;
    rows.forEach((row) => { new Image().src = "/screenshots/" + row.dataset.shot; }); // ready before the cursor gets there

    const select = (i) => {
      current = Math.max(0, Math.min(rows.length - 1, i)); // stops at the ends, like the file list
      const row = rows[current];
      rows.forEach((r) => r.setAttribute("aria-selected", String(r === row)));
      list.setAttribute("aria-activedescendant", row.id);
      img.src = "/screenshots/" + row.dataset.shot;
      img.removeAttribute("width");
      img.removeAttribute("height");
      img.alt = row.dataset.name;
      title.textContent = row.dataset.name;
      body.textContent = row.dataset.body;
      if (row.offsetTop < list.scrollTop || row.offsetTop + row.offsetHeight > list.scrollTop + list.clientHeight) {
        row.scrollIntoView({ block: "nearest" });
      }
    };
    rows.forEach((row, i) => row.addEventListener("click", () => { select(i); list.focus({ preventScroll: true }); }));
    list.addEventListener("keydown", (e) => {
      const moves = { ArrowDown: 1, ArrowUp: -1, Home: -current, End: rows.length - 1 - current, PageDown: 5, PageUp: -5 };
      if (e.key in moves) { e.preventDefault(); select(current + moves[e.key]); }
    });
  }

  // the looks: one screenshot per choice
  const lookImg = document.getElementById("look-img");
  if (lookImg) {
    const buttons = Array.from(document.querySelectorAll(".segmented button"));
    buttons.forEach((button) => {
      new Image().src = "/screenshots/" + button.dataset.shot;
      button.addEventListener("click", () => {
        buttons.forEach((b) => b.setAttribute("aria-selected", String(b === button)));
        lookImg.src = "/screenshots/" + button.dataset.shot;
        lookImg.alt = button.textContent;
      });
    });
  }

  // the download of the visitor's system stands out
  const agent = navigator.userAgent;
  const os = /Windows/i.test(agent) ? "windows" : /Mac/i.test(agent) && !/iPhone|iPad/i.test(agent) ? "mac" : /Linux|X11/i.test(agent) && !/Android/i.test(agent) ? "linux" : null;
  const names = { windows: "Windows", mac: "macOS", linux: "Linux" };
  if (os) {
    const card = document.querySelector(`.platform[data-os="${os}"]`);
    if (card) card.classList.add("yours");
    const heroButton = document.getElementById("hero-download");
    if (heroButton && heroButton.dataset.pattern) {
      heroButton.lastChild.textContent = heroButton.dataset.pattern.replace("{0}", names[os]);
    }
  }

  // copy buttons next to commands
  document.querySelectorAll(".code .copy").forEach((button) => {
    const label = button.textContent;
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(button.previousElementSibling.textContent);
        button.textContent = button.dataset.copied;
        setTimeout(() => { button.textContent = label; }, 1600);
      } catch (e) { /* no clipboard: the text can still be selected */ }
    });
  });

  // the guide list: typing filters the cards
  const filter = document.getElementById("guide-filter");
  if (filter) {
    const cards = Array.from(document.querySelectorAll(".guide-card"));
    filter.addEventListener("input", () => {
      const words = filter.value.toLocaleLowerCase().split(/\s+/).filter(Boolean);
      let shown = 0;
      cards.forEach((card) => {
        const match = words.every((w) => card.dataset.search.includes(w));
        card.hidden = !match;
        if (match) shown++;
      });
      document.querySelectorAll(".guide-group").forEach((group) => { group.hidden = !group.querySelector(".guide-card:not([hidden])"); });
      document.getElementById("guide-none").hidden = shown > 0;
    });
  }

  // the features page and the guides: the part in view is marked in the list beside it
  const tocLinks = Array.from(document.querySelectorAll(".toc a, .guide-toc a"));
  if (tocLinks.length && "IntersectionObserver" in window) {
    const byId = new Map(tocLinks.map((a) => [a.getAttribute("href").slice(1), a]));
    const spy = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        tocLinks.forEach((a) => a.classList.remove("active"));
        const link = byId.get(entry.target.id);
        if (link) link.classList.add("active");
      });
    }, { rootMargin: "-40% 0px -55% 0px" });
    byId.forEach((_, id) => { const section = document.getElementById(id); if (section) spy.observe(section); });
  }

  // the parts appear while scrolling
  const hidden = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window) {
    const seen = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) { entry.target.classList.add("in"); seen.unobserve(entry.target); }
      });
    }, { rootMargin: "0px 0px -6% 0px" });
    hidden.forEach((element) => seen.observe(element));
  } else {
    hidden.forEach((element) => element.classList.add("in"));
  }
})();
