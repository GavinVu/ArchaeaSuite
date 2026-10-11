interface Props {
  name: string;
  icon: string | null;
  size?: number;
}

/** Two-letter monogram used when an app has no icon: "ArchaeaPhoto" -> "Ph". */
function monogram(name: string): string {
  const rest = name.replace(/^archaea\s*/i, "").trim() || name;
  return rest.slice(0, 2).replace(/^./, (c) => c.toUpperCase());
}

export function AppIcon({ name, icon, size = 56 }: Props) {
  return (
    <div className="app-icon" style={{ width: size, height: size }} aria-hidden="true">
      {icon ? (
        <img src={icon} alt="" draggable={false} />
      ) : (
        <div className="app-icon-fallback" style={{ fontSize: size * 0.42 }}>
          {monogram(name)}
        </div>
      )}
    </div>
  );
}
