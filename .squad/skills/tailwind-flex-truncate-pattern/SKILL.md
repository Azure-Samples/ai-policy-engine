---
name: "tailwind-flex-truncate-pattern"
description: "Proper flex truncation for title+badge rows in Tailwind CSS"
domain: "ui-layout"
confidence: "high"
source: "earned"
---

## Context
When building UI components with text labels alongside badges, pills, or action buttons in a flex row, text often overflows or prevents proper truncation. This is a common issue in tree views, list items, card headers, and modal forms.

## Patterns

### Title + Badge Row
```tsx
<div className="flex items-center gap-2">
  <span className="min-w-0 flex-1 truncate font-medium">{title}</span>
  <Badge className="flex-shrink-0">Label</Badge>
</div>
```

**Key classes:**
- `min-w-0` — allows flex item to shrink below intrinsic content width (critical for truncation)
- `flex-1` — title takes remaining space after badge
- `truncate` — adds `text-overflow: ellipsis; overflow: hidden; white-space: nowrap`
- `flex-shrink-0` — badge never shrinks, always shows full text

### Form Label + Multiple Badges
```tsx
<div className="flex items-center gap-2">
  <label className="min-w-0 flex-1 truncate text-sm font-medium">{paramName}</label>
  {isRequired && <Badge className="flex-shrink-0" variant="red">Required</Badge>}
  <Badge className="flex-shrink-0" variant="outline">{type}</Badge>
</div>
```

### Nested Flex Container
When the flex row is inside another flex container (e.g., a button or clickable area), the parent also needs `min-w-0`:
```tsx
<button className="flex min-w-0 flex-1 flex-col items-start">
  <div className="flex w-full items-center gap-2">
    <span className="min-w-0 flex-1 truncate">{name}</span>
    <Badge className="flex-shrink-0">Status</Badge>
  </div>
</button>
```

## Examples

### Before (broken)
```tsx
<div className="flex items-center gap-2">
  <span className="truncate font-medium">{api.displayName}</span>
  <Badge>Current</Badge>
</div>
```
Result: Badge pushes text, but text doesn't truncate properly — overflows container.

### After (fixed)
```tsx
<div className="flex items-center gap-2">
  <span className="min-w-0 flex-1 truncate font-medium">{api.displayName}</span>
  <Badge className="flex-shrink-0">Current</Badge>
</div>
```
Result: Text truncates with ellipsis, badge stays visible.

## Anti-Patterns

❌ **Missing `min-w-0`** — without it, flexbox respects intrinsic width and won't truncate
```tsx
<span className="flex-1 truncate">{text}</span>  // Won't truncate!
```

❌ **Badge without `flex-shrink-0`** — badge may shrink and hide text on narrow containers
```tsx
<Badge>{label}</Badge>  // May shrink awkwardly
```

❌ **Using `whitespace-nowrap` without container constraints** — causes horizontal overflow
```tsx
<div className="whitespace-nowrap">{longText}</div>  // Overflows parent
```

❌ **Setting `overflow-hidden` on wrong element** — must be on the element with `truncate`, not a distant parent
