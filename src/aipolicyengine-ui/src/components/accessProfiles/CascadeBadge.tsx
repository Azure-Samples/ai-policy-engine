import { GitBranchPlus, Layers3 } from "lucide-react"
import { Badge } from "../ui/badge"
import { Button } from "../ui/button"
import { cn } from "../../lib/utils"
import type { EffectiveAccessPreview } from "./types"

interface CascadeBadgeProps {
  effective: EffectiveAccessPreview | null
  bulkQueued?: boolean
  onOverride: () => void
  onQueueBulk?: () => void
}

const SOURCE_BADGE_VARIANTS = {
  api: "teal",
  global: "cyan",
  client: "amber",
} as const

export function CascadeBadge({ effective, bulkQueued = false, onOverride, onQueueBulk }: CascadeBadgeProps) {
  if (!effective || effective.source === "direct") {
    return null
  }

  const sourceVariant = SOURCE_BADGE_VARIANTS[effective.source]

  return (
    <div className="rounded-xl border border-dashed border-slate-300/70 bg-slate-500/5 p-3 dark:border-slate-700/70 dark:bg-slate-500/10">
      <div className="flex items-start gap-3">
        <div className="rounded-lg bg-slate-900/90 p-2 text-white dark:bg-slate-100 dark:text-slate-900">
          <Layers3 className="h-4 w-4" />
        </div>
        <div className="min-w-0 flex-1 space-y-2">
          <div className="flex items-center gap-2">
            <Badge className="flex-shrink-0" variant={sourceVariant}>{effective.sourceLabel}</Badge>
            <span className="min-w-0 flex-1 truncate text-xs uppercase tracking-[0.18em] text-muted-foreground">Cascade preview</span>
          </div>
          <p className="text-sm font-medium text-foreground">{effective.sourceDescription}</p>
          <p className="text-xs text-muted-foreground">This scope inherits until you create a direct override here.</p>
          <div className="flex flex-wrap gap-2">
            <Button type="button" size="sm" onClick={onOverride} className="gap-2">
              <GitBranchPlus className="h-3.5 w-3.5" />
              Override here
            </Button>
            {onQueueBulk && (
              <Button
                type="button"
                size="sm"
                variant={bulkQueued ? "secondary" : "outline"}
                onClick={onQueueBulk}
                className={cn("gap-2", bulkQueued && "border-primary/40")}
              >
                {bulkQueued ? "Queued for bulk" : "Queue for bulk"}
              </Button>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
