/* Fills the download rows and the version pill from the latest GitHub release.

   The markup ships with real values baked in, so the page is correct with no
   JS, with the API rate-limited (60 req/hr per IP, unauthenticated), or with
   the request blocked. This only ever replaces good data with fresher data —
   it never empties a row or shows a loading state. */
(function () {
  "use strict";

  var REPO = "bpbp-boop/resesh";
  var API = "https://api.github.com/repos/" + REPO + "/releases";
  var CACHE_KEY = "resesh-release";
  var CACHE_TTL = 6 * 60 * 60 * 1000;

  /* Which asset fills which row, matched on filename rather than position —
     the API does not guarantee asset order. "x64" cannot match "arm64". */
  var MATCHERS = {
    "x64-setup": function (n) { return /x64/.test(n) && /setup\.exe$/.test(n); },
    "arm64-setup": function (n) { return /arm64/.test(n) && /setup\.exe$/.test(n); },
    "x64-portable": function (n) { return /x64/.test(n) && /portable\.zip$/.test(n); }
  };

  function formatSize(bytes) {
    var mb = bytes / (1024 * 1024);
    return (mb < 10 ? mb.toFixed(1) : Math.round(mb)) + " mb";
  }

  function readCache() {
    try {
      var raw = localStorage.getItem(CACHE_KEY);
      if (!raw) return null;
      var hit = JSON.parse(raw);
      if (!hit || typeof hit.at !== "number") return null;
      if (Date.now() - hit.at > CACHE_TTL) return null;
      return hit.release;
    } catch (e) {
      return null;
    }
  }

  function writeCache(release) {
    try {
      localStorage.setItem(CACHE_KEY, JSON.stringify({ at: Date.now(), release: release }));
    } catch (e) {
      /* storage blocked or full — the fetch still worked, just not cached */
    }
  }

  /* Keep only the fields the page renders, so the cache entry stays small. */
  function condense(release) {
    return {
      tag: release.tag_name,
      url: release.html_url,
      assets: (release.assets || []).map(function (a) {
        return { name: a.name, size: a.size, url: a.browser_download_url };
      })
    };
  }

  function fetchRelease() {
    /* /releases/latest excludes prereleases and 404s when every release is
       one, which is a live possibility for a project shipping a beta. */
    return fetch(API + "/latest", { headers: { Accept: "application/vnd.github+json" } })
      .then(function (res) {
        if (res.ok) return res.json();
        if (res.status !== 404) throw new Error("github api " + res.status);
        return fetch(API + "?per_page=10", { headers: { Accept: "application/vnd.github+json" } })
          .then(function (r) {
            if (!r.ok) throw new Error("github api " + r.status);
            return r.json();
          })
          .then(function (list) {
            var first = (list || []).filter(function (r) { return !r.draft; })[0];
            if (!first) throw new Error("no published releases");
            return first;
          });
      })
      .then(condense);
  }

  function apply(release) {
    if (!release || !release.assets || !release.assets.length) return;

    var version = release.tag ? release.tag.replace(/^v/, "") : null;
    if (version) {
      Array.prototype.forEach.call(document.querySelectorAll("[data-release-version]"), function (el) {
        el.textContent = "v" + version;
      });
    }

    Array.prototype.forEach.call(document.querySelectorAll("[data-dl]"), function (row) {
      var match = MATCHERS[row.getAttribute("data-dl")];
      if (!match) return;

      var asset = release.assets.filter(function (a) { return match(a.name); })[0];
      var sub = row.querySelector("[data-dl-sub]");

      if (!asset) {
        /* This build is missing from the newest release. Point at the release
           page rather than leaving a stale link to an older asset. */
        if (release.url) row.href = release.url;
        if (sub) sub.textContent = "see all release files";
        return;
      }

      row.href = asset.url;
      if (sub) {
        sub.textContent = row.getAttribute("data-dl") === "x64-portable"
          ? "no install, runs from a usb stick · " + formatSize(asset.size)
          : asset.name + " · " + formatSize(asset.size);
      }
    });
  }

  var cached = readCache();
  if (cached) apply(cached);

  fetchRelease()
    .then(function (release) {
      writeCache(release);
      apply(release);
    })
    .catch(function () {
      /* Leave whatever is already on the page — baked-in values or cache. */
    });
})();
