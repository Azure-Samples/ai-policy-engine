import { describe, it, expect } from 'vitest'
import { cn } from './utils'
import { badgeVariants } from '../components/ui/badge'
import { buttonVariants } from '../components/ui/button'

describe('cn', () => {
  it('merges strings and filters falsy values', () => {
    expect(cn('a', false, null, undefined, 'b')).toBe('a b')
  })

  it('resolves conflicting tailwind classes (last wins)', () => {
    expect(cn('px-2', 'px-4')).toBe('px-4')
  })

  it('empty call returns empty string', () => {
    expect(cn()).toBe('')
  })
})

describe('badgeVariants', () => {
  it('returns default variant classes when called with no args', () => {
    const cls = badgeVariants()
    expect(cls).toContain('bg-primary')
    expect(cls).toContain('text-primary-foreground')
  })

  it('returns named variant classes', () => {
    expect(badgeVariants({ variant: 'blue' })).toContain('bg-[#0078D4]/15')
  })

  it('falls back to default when variant is null', () => {
    expect(badgeVariants({ variant: null })).toContain('bg-primary')
  })

  it('appends className override and merges conflicts', () => {
    const cls = badgeVariants({ variant: 'default', className: 'px-6' })
    expect(cls).toContain('px-6')
  })
})

describe('buttonVariants', () => {
  it('returns base classes and default variant when called with no args', () => {
    const cls = buttonVariants()
    expect(cls).toContain('bg-primary')
    expect(cls).toContain('h-10')
  })

  it('returns correct size classes', () => {
    expect(buttonVariants({ size: 'sm' })).toContain('h-9')
    expect(buttonVariants({ size: 'lg' })).toContain('h-11')
    expect(buttonVariants({ size: 'icon' })).toContain('w-10')
  })

  it('falls back to defaults when variant and size are null', () => {
    const cls = buttonVariants({ variant: null, size: null })
    expect(cls).toContain('bg-primary')
    expect(cls).toContain('h-10')
  })

  it('applies className override', () => {
    const cls = buttonVariants({ variant: 'ghost', className: 'w-full' })
    expect(cls).toContain('hover:bg-accent')
    expect(cls).toContain('w-full')
  })
})
