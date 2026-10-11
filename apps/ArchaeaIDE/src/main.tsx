import React from "react";
import ReactDOM from "react-dom/client";
import "@fontsource-variable/archivo/wdth.css";
import "@fontsource/ibm-plex-mono/400.css";
import "@fontsource/ibm-plex-mono/500.css";
import "@fontsource/ibm-plex-mono/600.css";
import "@xterm/xterm/css/xterm.css";
// The suite's design tokens and shared components come from the launcher.
import "../../../launcher/src/styles.css";
import "./ide.css";
import { App } from "./App";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);

// Development builds expose the modules for scripted testing (launcher/scripts/cdp.mjs).
if (import.meta.env.DEV) {
  void Promise.all([
    import("./store"), import("./editor"), import("./terminal"), import("./workspace"), import("./commands"),
    import("./debug"), import("./git"), import("./overlay"), import("./extensions"), import("./api"),
  ]).then(([store, editor, terminal, workspace, commands, debug, git, overlay, extensions, api]) =>
    Object.assign(window, { ide: { store, editor, terminal, workspace, commands, debug, git, overlay, extensions, api } })
  );
}
