"use client";

import type { ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import type { PermissionState } from "@/lib/sensors";

const statusLabel: Record<PermissionState, string> = {
  unknown: "idle",
  prompt: "prompt",
  granted: "live",
  denied: "denied",
  unsupported: "n/a",
};

export function SensorPanel({
  title,
  description,
  permission,
  enabled,
  onEnabledChange,
  onEnable,
  error,
  children,
  actionLabel = "Allow access",
}: {
  title: string;
  description: string;
  permission: PermissionState;
  enabled: boolean;
  onEnabledChange: (next: boolean) => void;
  onEnable: () => void | Promise<void>;
  error?: string | null;
  children: ReactNode;
  actionLabel?: string;
}) {
  const needsPermission =
    permission === "unknown" || permission === "prompt" || permission === "denied";

  return (
    <section className="sensor-panel flex flex-col gap-4 rounded-2xl border border-[color:var(--ink-line)] bg-[color:var(--panel)]/80 p-5 backdrop-blur-sm">
      <div className="flex items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-[family-name:var(--font-display)] text-xl tracking-tight text-[color:var(--ink)]">
              {title}
            </h2>
            <Badge
              variant="outline"
              className="rounded-md border-[color:var(--ink-line)] font-mono text-[10px] uppercase tracking-[0.16em]"
            >
              {statusLabel[permission]}
            </Badge>
          </div>
          <p className="max-w-prose text-sm leading-relaxed text-[color:var(--ink-muted)]">
            {description}
          </p>
        </div>
        <Switch
          checked={enabled}
          onCheckedChange={onEnabledChange}
          aria-label={`Toggle ${title}`}
        />
      </div>

      {needsPermission && enabled ? (
        <Button
          type="button"
          onClick={() => void onEnable()}
          className="w-fit rounded-xl bg-[color:var(--teal)] text-[color:var(--teal-ink)] hover:bg-[color:var(--teal-deep)]"
        >
          {actionLabel}
        </Button>
      ) : null}

      {error ? (
        <p className="rounded-xl border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
          {error}
        </p>
      ) : null}

      <div className={enabled ? "opacity-100" : "opacity-45"}>{children}</div>
    </section>
  );
}
