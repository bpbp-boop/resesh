/* Theme toggle. The stored choice wins; with no stored choice the page
   follows prefers-color-scheme (handled in CSS, mirrored here for the label).
   The button always names the theme it will switch *to*. */
(function () {
  "use strict";

  var STORAGE_KEY = "resesh-theme";
  var root = document.documentElement;
  var buttons = document.querySelectorAll("[data-theme-toggle]");
  var systemLight = window.matchMedia ? window.matchMedia("(prefers-color-scheme: light)") : null;

  function current() {
    var chosen = root.getAttribute("data-theme");
    if (chosen === "light" || chosen === "dark") return chosen;
    return systemLight && systemLight.matches ? "light" : "dark";
  }

  function paintLabels() {
    var target = current() === "light" ? "dark" : "light";
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].textContent = target;
      buttons[i].setAttribute("aria-label", "switch to " + target + " theme");
    }
  }

  function toggle() {
    var next = current() === "light" ? "dark" : "light";
    root.setAttribute("data-theme", next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch (e) {
      /* private mode, blocked storage — the toggle still works for this page */
    }
    paintLabels();
  }

  for (var i = 0; i < buttons.length; i++) {
    buttons[i].addEventListener("click", toggle);
  }

  if (systemLight && systemLight.addEventListener) {
    systemLight.addEventListener("change", paintLabels);
  }

  paintLabels();
})();
