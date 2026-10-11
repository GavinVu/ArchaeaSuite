// Runs before first paint. Mirrors the theme saved in settings (kept in
// localStorage by the app) so the splash screen already has the right colours.
(function () {
  var theme = "system";
  try {
    theme = localStorage.getItem("archaea.theme") || "system";
  } catch (e) {}
  if (theme !== "dark" && theme !== "light") {
    theme = window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  }
  document.documentElement.setAttribute("data-theme", theme);
})();
