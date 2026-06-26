---
name: "sticky-positioning-accessibility"
description: "Correct sticky positioning with header alignment and accessible search/filter controls"
domain: "frontend, react, accessibility, layout"
confidence: "high"
source: "earned — from Access Profiles layout review"
---

## Context
Use this skill when implementing sticky headers, filters, or sidebars that need to align with a page header and be accessible to screen readers.

## Patterns

### 1. Sticky Offset Alignment
**Always match sticky `top-*` to exact header height.**

```tsx
// Header
<header className="sticky top-0 h-16">

// Sticky element
<div className="xl:sticky xl:top-16 xl:h-[calc(100vh-4rem)]">
```

**Rule:** If header is `h-16` (4rem), use `top-16` and `h-[calc(100vh-4rem)]` for viewport-filling sidebars.

**Why:** Mismatched offsets (e.g., `top-[5.5rem]` when header is `h-16`) create visual gaps. Using the same Tailwind unit keeps them synchronized.

### 2. Search Input Accessibility
**Icon-only inputs must have `aria-label` even when placeholder exists.**

```tsx
<div className="relative">
  <Search className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2" />
  <Input
    placeholder="Search…"
    aria-label="Search access profiles"  // Required for screen readers
    className="pl-9"
  />
</div>
```

**Why:** Placeholders are not accessible labels — screen readers need `aria-label`.

### 3. Filter Button Group Accessibility
**Toggle button groups need semantic roles and pressed state.**

```tsx
<div role="group" aria-label="Override filter">
  {options.map((option) => (
    <button
      key={option.value}
      type="button"
      aria-pressed={activeFilter === option.value}  // Required for toggle state
      onClick={() => setActiveFilter(option.value)}
    >
      {option.label}
    </button>
  ))}
</div>
```

**Why:** Screen readers need `role="group"` to announce the group context and `aria-pressed` to announce toggle state.

## Examples

### Access Profiles Page (src/aipolicyengine-ui/src/pages/AccessProfiles.tsx)
- Sticky client list: `xl:sticky xl:top-16 xl:h-[calc(100vh-4rem)]`
- Header: `h-16` (Layout.tsx)

### Profile Grid (src/aipolicyengine-ui/src/components/accessProfiles/ProfileGrid.tsx)
- Sticky search bar: `sticky top-16`
- Search input: `aria-label="Search access profiles"`
- Filter group: `role="group" aria-label="Override filter"` with `aria-pressed` on buttons

## Anti-Patterns
- Using arbitrary pixel offsets like `top-[88px]` instead of matching header class.
- Omitting `aria-label` on icon-only search inputs.
- Using plain `<div>` for button groups without `role="group"`.
- Toggle buttons without `aria-pressed` state.

## WCAG Compliance
- **1.3.1 Info and Relationships** — Button groups have semantic roles.
- **4.1.2 Name, Role, Value** — Inputs have accessible names, buttons announce pressed state.
