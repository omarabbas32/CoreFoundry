import { useId } from "react";

/**
 * The CoreFoundry mark: a drop of molten metal falling into a mold that's already filling, on a blue tile.
 * The foundry casts your schema. Its colors are fixed (brand), so it looks the same in light and dark mode.
 * The same drawing is the favicon (app/icon.svg); change both together.
 */
export function LogoMark({ size = 28, className }: { size?: number; className?: string }) {
  const id = useId();
  const tile = `${id}-tile`;
  const metal = `${id}-metal`;
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden className={className}>
      <defs>
        <linearGradient id={tile} x1="0" y1="0" x2="32" y2="32" gradientUnits="userSpaceOnUse">
          <stop stopColor="#388bfd" />
          <stop offset="1" stopColor="#1a56c4" />
        </linearGradient>
        <linearGradient id={metal} x1="16" y1="4" x2="16" y2="25" gradientUnits="userSpaceOnUse">
          <stop stopColor="#ffd166" />
          <stop offset="1" stopColor="#ff7b39" />
        </linearGradient>
      </defs>
      <rect width="32" height="32" rx="8" fill={`url(#${tile})`} />
      <path d="M16 4.5s-4.2 4.9-4.2 7.6a4.2 4.2 0 0 0 8.4 0c0-2.7-4.2-7.6-4.2-7.6z" fill={`url(#${metal})`} />
      <path d="M8.5 18.5V23a4 4 0 0 0 4 4h7a4 4 0 0 0 4-4v-4.5" fill="none" stroke="#fff" strokeWidth="2.6" strokeLinecap="round" />
      <path d="M11.1 20.6h9.8v2.3a1.9 1.9 0 0 1-1.9 1.9h-6a1.9 1.9 0 0 1-1.9-1.9z" fill={`url(#${metal})`} />
    </svg>
  );
}

/** The mark with the name; "Foundry" takes the accent color. */
export function Logo({ size = 28, className }: { size?: number; className?: string }) {
  return (
    <span className={`inline-flex items-center gap-2 font-semibold tracking-tight ${className ?? ""}`}>
      <LogoMark size={size} />
      <span>
        Core<span className="text-accent">Foundry</span>
      </span>
    </span>
  );
}
