import { formatNumber } from "@/lib/sensors";

export function ValueRow({
  label,
  value,
  unit,
  digits = 2,
}: {
  label: string;
  value: number | null | undefined;
  unit?: string;
  digits?: number;
}) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b border-[color:var(--ink-line)] py-2 last:border-b-0">
      <span className="text-xs uppercase tracking-[0.14em] text-[color:var(--ink-muted)]">
        {label}
      </span>
      <span className="font-mono text-sm tabular-nums text-[color:var(--ink)]">
        {formatNumber(value, digits)}
        {unit ? <span className="ml-1 text-[color:var(--ink-muted)]">{unit}</span> : null}
      </span>
    </div>
  );
}
