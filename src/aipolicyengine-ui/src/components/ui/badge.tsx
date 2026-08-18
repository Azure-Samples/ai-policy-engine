/* eslint-disable react-refresh/only-export-components */
import * as React from "react"
import { cn } from "../../lib/utils"

const badgeVariantClasses = {
  default: "border-transparent bg-primary text-primary-foreground",
  secondary: "border-transparent bg-secondary text-secondary-foreground",
  destructive: "border-transparent bg-destructive text-destructive-foreground",
  outline: "text-foreground",
  blue: "border-transparent bg-[#0078D4]/15 text-[#005A9E] dark:text-[#4CC2FF]",
  green: "border-transparent bg-[#107C10]/15 text-[#107C10] dark:text-[#5EC75E]",
  teal: "border-transparent bg-[#00B7C3]/15 text-[#007F85] dark:text-[#00B7C3]",
  amber: "border-transparent bg-[#FFB900]/15 text-[#986F0B] dark:text-[#FFB900]",
  red: "border-transparent bg-[#D13438]/15 text-[#D13438] dark:text-[#F1707A]",
  cyan: "border-transparent bg-[#00B7C3]/15 text-[#007F85] dark:text-[#00B7C3]",
} as const

type BadgeVariant = keyof typeof badgeVariantClasses

interface BadgeVariantOptions {
  variant?: BadgeVariant | null
  className?: string
}

const badgeVariants = ({ variant = "default", className }: BadgeVariantOptions = {}) =>
  cn(
    "inline-flex items-center rounded-full border px-2.5 py-0.5 text-xs font-semibold transition-colors focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2",
    badgeVariantClasses[variant ?? "default"],
    className,
  )

export interface BadgeProps
  extends React.HTMLAttributes<HTMLDivElement> {
  variant?: BadgeVariant | null
}

function Badge({ className, variant, ...props }: BadgeProps) {
  return <div className={badgeVariants({ variant, className })} {...props} />
}

export { Badge, badgeVariants }
