"use client";

import type { OrientationData } from "@/lib/sensors";

export function OrientationViz({ data }: { data: OrientationData | null }) {
  const beta = data?.beta ?? 0;
  const gamma = data?.gamma ?? 0;
  const alpha = data?.alpha ?? 0;

  return (
    <div className="relative mx-auto aspect-square w-full max-w-[180px]">
      <div className="absolute inset-0 rounded-full border border-[color:var(--ink-line)] bg-[radial-gradient(circle_at_30%_20%,rgba(34,197,164,0.18),transparent_55%),linear-gradient(160deg,#eef6f4,#d7e8e3)]" />
      <div className="absolute inset-[18%] rounded-full border border-dashed border-[color:var(--teal)]/40" />
      <div
        className="absolute inset-[28%] rounded-[1.1rem] border border-[color:var(--ink)]/20 bg-[color:var(--panel)] shadow-[0_10px_30px_rgba(15,48,43,0.18)] transition-transform duration-150 ease-out"
        style={{
          transform: `rotateZ(${alpha * 0.15}deg) rotateX(${Math.max(-50, Math.min(50, -beta))}deg) rotateY(${Math.max(-50, Math.min(50, gamma))}deg)`,
          transformStyle: "preserve-3d",
        }}
      >
        <div className="absolute inset-x-4 top-3 h-1.5 rounded-full bg-[color:var(--teal)]" />
        <div className="absolute inset-0 grid place-items-center font-mono text-[10px] uppercase tracking-[0.2em] text-[color:var(--ink-muted)]">
          device
        </div>
      </div>
    </div>
  );
}
