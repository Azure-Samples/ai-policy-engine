import { twMerge } from "tailwind-merge"

export function cn(...inputs: (string | false | null | undefined)[]) {
  return twMerge(inputs.filter(Boolean).join(" "))
}
