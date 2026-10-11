// Opening folders, file operations from the explorer, and project templates.
import { open as openDialog, save as saveDialog } from "@tauri-apps/plugin-dialog";
import { api, basename, dirname, errorText, join, samePath } from "./api";
import { closeAllTabs, closeTab, openFile, openSpecial, pathRenamed, syncWithDisk } from "./editor";
import { ask, pick, prompt } from "./overlay";
import { fail, get, saveSession, set, toast, touchFs } from "./store";

export async function openFolder(path: string, files: string[] = [], active: string | null = null) {
  if (!(await api.stat(path)).dir) {
    toast("error", "Folder not found", path);
    set((s) => ({ recent: s.recent.filter((r) => !samePath(r, path)) }));
    return;
  }
  if (get().root && !samePath(get().root!, path) && !(await closeAllTabs())) return;
  set((s) => ({ root: path, recent: [path, ...s.recent.filter((r) => !samePath(r, path))].slice(0, 12), sideOpen: true }));
  touchFs();
  for (const f of files) if ((await api.stat(f)).exists) await openFile(f);
  if (active && files.some((f) => samePath(f, active))) await openFile(active);
  saveSession();
}

export async function pickFolder() {
  const path = await openDialog({ directory: true, title: "Open Folder" });
  if (typeof path === "string") await openFolder(path);
}

export async function pickFile() {
  const path = await openDialog({ title: "Open File", defaultPath: get().root ?? undefined });
  if (typeof path === "string") await openFile(path);
}

export async function closeFolder() {
  if (!(await closeAllTabs())) return;
  set({ root: null });
  openSpecial("welcome");
  saveSession();
}

export async function newUntitledFile() {
  const path = await saveDialog({ title: "New File", defaultPath: get().root ?? undefined });
  if (!path) return;
  await api.writeText(path, "").catch(fail("Could not create the file"));
  touchFs();
  await openFile(path);
}

const validName = (name: string) => /^[^\\/:*?"<>|]+$/.test(name) && !/^\.+$/.test(name);

export async function createIn(dir: string, kind: "file" | "folder") {
  const name = (await prompt(kind === "file" ? "New file name (a/b.txt creates folders)" : "New folder name"))?.trim();
  if (!name) return;
  if (!name.split(/[\\/]/).every(validName)) return toast("error", "That name contains characters Windows does not allow.");
  const path = join(dir, name);
  try {
    if (kind === "file") {
      await api.createFile(path);
      await openFile(path);
    } else {
      await api.createDir(path);
    }
  } catch (e) {
    toast("error", `Could not create ${name}`, errorText(e));
  }
  touchFs();
}

export async function renamePath(path: string) {
  const name = (await prompt("New name", basename(path)))?.trim();
  if (!name || name === basename(path)) return;
  if (!validName(name)) return toast("error", "That name contains characters Windows does not allow.");
  const to = join(dirname(path), name);
  try {
    await api.rename(path, to);
    pathRenamed(path, to);
  } catch (e) {
    toast("error", "Could not rename", errorText(e));
  }
  touchFs();
}

export async function deletePath(path: string, isDir: boolean) {
  const answer = await ask(
    `Delete ${basename(path)}?`,
    `${isDir ? "The folder and everything in it" : "The file"} will be moved to the Recycle Bin.`,
    [{ label: "Move to Recycle Bin", value: "yes", kind: "danger" }, { label: "Cancel", value: "no" }]
  );
  if (answer !== "yes") return;
  try {
    await api.trash(path);
    for (const g of get().groups)
      for (const t of g.tabs)
        if (t.path && (samePath(t.path, path) || t.path.toLowerCase().startsWith(path.toLowerCase() + "\\")))
          await closeTab(g.id, t.id, true);
  } catch (e) {
    toast("error", "Could not delete", errorText(e));
  }
  touchFs();
}

/** Called when the window regains focus: other programs may have changed files. */
export async function refreshFromDisk() {
  await syncWithDisk();
  touchFs();
}

// -------------------------------------------------------------- templates

interface Template { name: string; detail: string; main: string; files: (name: string) => Record<string, string> }

const gitignore = (lines: string) => lines.trim().split(/\s+/).join("\n") + "\n";
const safe = (name: string) => name.replace(/[^A-Za-z0-9_]/g, "_").replace(/^(\d)/, "_$1");

export const templates: Template[] = [
  {
    name: "Python script",
    detail: "main.py with a main() function",
    main: "main.py",
    files: (n) => ({
      "main.py": `def main() -> None:\n    name = "${n}"\n    print(f"Hello from {name}!")\n\n\nif __name__ == "__main__":\n    main()\n`,
      "README.md": `# ${n}\n\nRun with \`python main.py\`.\n`,
      ".gitignore": gitignore("__pycache__/ .venv/ *.pyc"),
    }),
  },
  {
    name: "Node.js app",
    detail: "package.json and index.js (ES modules)",
    main: "index.js",
    files: (n) => ({
      "package.json": JSON.stringify({ name: n.toLowerCase().replace(/[^a-z0-9-]/g, "-"), version: "0.1.0", private: true, type: "module", scripts: { start: "node index.js" } }, null, 2) + "\n",
      "index.js": `const name = "${n}";\nconsole.log(\`Hello from \${name}!\`);\n`,
      ".gitignore": gitignore("node_modules/"),
    }),
  },
  {
    name: "TypeScript (Node.js)",
    detail: "index.ts, run directly by Node.js 23.6 or newer",
    main: "index.ts",
    files: (n) => ({
      "package.json": JSON.stringify({ name: n.toLowerCase().replace(/[^a-z0-9-]/g, "-"), version: "0.1.0", private: true, type: "module", scripts: { start: "node index.ts" } }, null, 2) + "\n",
      "index.ts": `function greet(name: string): string {\n  return \`Hello from \${name}!\`;\n}\n\nconsole.log(greet("${n}"));\n`,
      "tsconfig.json": JSON.stringify({ compilerOptions: { target: "ES2022", module: "NodeNext", strict: true, noEmit: true } }, null, 2) + "\n",
      ".gitignore": gitignore("node_modules/"),
    }),
  },
  {
    name: "Web page",
    detail: "index.html, style.css and script.js",
    main: "index.html",
    files: (n) => ({
      "index.html": `<!doctype html>\n<html lang="en">\n  <head>\n    <meta charset="utf-8" />\n    <meta name="viewport" content="width=device-width, initial-scale=1" />\n    <title>${n}</title>\n    <link rel="stylesheet" href="style.css" />\n  </head>\n  <body>\n    <h1>${n}</h1>\n    <button id="hello">Say hello</button>\n    <p id="out"></p>\n    <script src="script.js"></script>\n  </body>\n</html>\n`,
      "style.css": `body {\n  font-family: system-ui, sans-serif;\n  margin: 3rem;\n}\n`,
      "script.js": `document.getElementById("hello").addEventListener("click", () => {\n  document.getElementById("out").textContent = "Hello!";\n});\n`,
    }),
  },
  {
    name: "C program",
    detail: "main.c (needs gcc or clang)",
    main: "main.c",
    files: (n) => ({
      "main.c": `#include <stdio.h>\n\nint main(void) {\n    printf("Hello from ${n}!\\n");\n    return 0;\n}\n`,
      ".gitignore": gitignore("*.exe *.o"),
    }),
  },
  {
    name: "C++ program",
    detail: "main.cpp (needs g++ or clang++)",
    main: "main.cpp",
    files: (n) => ({
      "main.cpp": `#include <iostream>\n\nint main() {\n    std::cout << "Hello from ${n}!" << std::endl;\n    return 0;\n}\n`,
      ".gitignore": gitignore("*.exe *.o"),
    }),
  },
  {
    name: "C# console app",
    detail: "A .NET project with Program.cs",
    main: "Program.cs",
    files: (n) => ({
      [`${safe(n)}.csproj`]: `<Project Sdk="Microsoft.NET.Sdk">\n\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net9.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n\n</Project>\n`,
      "Program.cs": `var name = "${n}";\nConsole.WriteLine($"Hello from {name}!");\n`,
      ".gitignore": gitignore("bin/ obj/"),
    }),
  },
  {
    name: "Java program",
    detail: "Main.java (needs a JDK)",
    main: "Main.java",
    files: (n) => ({
      "Main.java": `public class Main {\n    public static void main(String[] args) {\n        System.out.println("Hello from ${n}!");\n    }\n}\n`,
      ".gitignore": gitignore("*.class"),
    }),
  },
  {
    name: "Rust program",
    detail: "A Cargo package",
    main: "src\\main.rs",
    files: (n) => ({
      "Cargo.toml": `[package]\nname = "${safe(n).toLowerCase()}"\nversion = "0.1.0"\nedition = "2021"\n\n[dependencies]\n`,
      "src\\main.rs": `fn main() {\n    println!("Hello from ${n}!");\n}\n`,
      ".gitignore": gitignore("target/"),
    }),
  },
  {
    name: "Go program",
    detail: "go.mod and main.go",
    main: "main.go",
    files: (n) => ({
      "go.mod": `module ${safe(n).toLowerCase()}\n\ngo 1.22\n`,
      "main.go": `package main\n\nimport "fmt"\n\nfunc main() {\n\tfmt.Println("Hello from ${n}!")\n}\n`,
    }),
  },
  { name: "Empty folder", detail: "Just a README", main: "README.md", files: (n) => ({ "README.md": `# ${n}\n` }) },
];

export async function newProject() {
  const template = await pick(templates.map((t) => ({ label: t.name, detail: t.detail, value: t })), "New project: choose a template");
  if (!template) return;
  const name = (await prompt("Project name", "my-project"))?.trim();
  if (!name) return;
  if (!validName(name)) return toast("error", "That name contains characters Windows does not allow.");
  const parent = await openDialog({ directory: true, title: `Create "${name}" inside…` });
  if (typeof parent === "string") await createProject(template, name, parent);
}

/** Writes a template's files into parent\name and opens the result. */
export async function createProject(template: Template, name: string, parent: string) {
  const dir = join(parent, name);
  if ((await api.stat(dir)).exists) return toast("error", "That folder already exists.", dir);
  try {
    for (const [rel, text] of Object.entries(template.files(name))) await api.writeText(join(dir, rel), text);
  } catch (e) {
    return toast("error", "Could not create the project", errorText(e));
  }
  await openFolder(dir);
  await openFile(join(dir, template.main));
  toast("ok", `Created ${name}`, dir);
}
