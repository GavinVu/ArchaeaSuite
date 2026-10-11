import { useEffect, useRef } from "react";
import { AppDrawer } from "./components/AppDrawer";
import { Sidebar, TitleBar, Toasts, UninstallDialog } from "./components/Chrome";
import { AllApps } from "./pages/AllApps";
import { Home } from "./pages/Home";
import { Installed } from "./pages/Installed";
import { Settings } from "./pages/Settings";
import { Updates } from "./pages/Updates";
import { init, useEntries, useStore } from "./store";

function dismissSplash() {
  const splash = document.getElementById("splash");
  if (!splash) return;
  splash.classList.add("is-leaving");
  window.setTimeout(() => splash.remove(), 220);
}

export function App() {
  const ready = useStore((s) => s.ready);
  const page = useStore((s) => s.page);
  const selectedId = useStore((s) => s.selectedId);
  const entries = useEntries();
  const mainRef = useRef<HTMLElement>(null);

  useEffect(() => {
    void init();
  }, []);

  useEffect(() => {
    if (ready) dismissSplash();
  }, [ready]);

  useEffect(() => {
    mainRef.current?.scrollTo({ top: 0 });
  }, [page]);

  // A launcher is not a web page: no browser context menu outside text fields.
  useEffect(() => {
    const onContext = (e: MouseEvent) => {
      const target = e.target as HTMLElement;
      if (!target.closest("input, textarea, .selectable")) e.preventDefault();
    };
    window.addEventListener("contextmenu", onContext);
    return () => window.removeEventListener("contextmenu", onContext);
  }, []);

  const selected = selectedId ? entries.find((e) => e.id === selectedId) : undefined;

  return (
    <div className="shell">
      <TitleBar />
      <Sidebar />
      <main className="main" ref={mainRef}>
        {ready && page === "home" && <Home />}
        {ready && page === "apps" && <AllApps />}
        {ready && page === "installed" && <Installed />}
        {ready && page === "updates" && <Updates />}
        {ready && page === "settings" && <Settings />}
      </main>
      {selected && <AppDrawer entry={selected} />}
      <UninstallDialog />
      <Toasts />
    </div>
  );
}
