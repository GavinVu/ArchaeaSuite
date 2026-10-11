export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || !Number.isFinite(bytes)) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return unit === 0 ? `${bytes} B` : `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
}

export function formatDate(input: string | number | null | undefined): string {
  if (input == null || input === "") return "—";
  // A bare YYYY-MM-DD is a calendar date; parse it as local so it doesn't
  // slip a day in timezones behind UTC.
  const ymd = typeof input === "string" ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(input) : null;
  const date = ymd
    ? new Date(+ymd[1], +ymd[2] - 1, +ymd[3])
    : typeof input === "number"
      ? new Date(input * 1000)
      : new Date(input);
  if (Number.isNaN(date.getTime())) return String(input);
  return date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

export function formatRelative(unixSeconds: number | null): string {
  if (!unixSeconds) return "never";
  const diff = Math.max(0, Date.now() / 1000 - unixSeconds);
  if (diff < 60) return "just now";
  if (diff < 3600) return `${Math.floor(diff / 60)} min ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} h ago`;
  return formatDate(unixSeconds);
}

/** Semver comparison: negative when a < b. Pre-releases sort before releases. */
export function compareVersions(a: string, b: string): number {
  const parse = (v: string) => {
    const [core, pre] = v.split("+")[0].split("-", 2);
    const nums = core.split(".").map((n) => parseInt(n, 10) || 0);
    return { nums, pre: pre ?? null };
  };
  const pa = parse(a);
  const pb = parse(b);
  for (let i = 0; i < 3; i++) {
    const d = (pa.nums[i] ?? 0) - (pb.nums[i] ?? 0);
    if (d !== 0) return d;
  }
  if (pa.pre === pb.pre) return 0;
  if (pa.pre === null) return 1;
  if (pb.pre === null) return -1;
  return pa.pre.localeCompare(pb.pre, undefined, { numeric: true });
}

export function percent(done: number, total: number): number {
  if (!total) return 0;
  return Math.min(100, Math.max(0, Math.round((done / total) * 100)));
}
