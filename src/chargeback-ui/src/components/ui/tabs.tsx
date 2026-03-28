import * as React from "react"
import { cn } from "../../lib/utils"

interface TabsProps {
  value: string
  onValueChange: (value: string) => void
  children: React.ReactNode
  className?: string
}

function Tabs({ value, onValueChange, children, className }: TabsProps) {
  return (
    <div className={className} data-active-tab={value}>
      {React.Children.map(children, (child) => {
        if (React.isValidElement<TabsListProps | TabsContentProps>(child)) {
          return React.cloneElement(child, { _activeTab: value, _onTabChange: onValueChange } as Partial<TabsListProps & TabsContentProps>)
        }
        return child
      })}
    </div>
  )
}

interface TabsListProps extends React.HTMLAttributes<HTMLDivElement> {
  _activeTab?: string
  _onTabChange?: (value: string) => void
}

function TabsList({ className, children, _activeTab, _onTabChange, ...props }: TabsListProps) {
  return (
    <div
      className={cn(
        "inline-flex h-10 items-center justify-center rounded-md bg-muted p-1 text-muted-foreground",
        className
      )}
      {...props}
    >
      {React.Children.map(children, (child) => {
        if (React.isValidElement<TabsTriggerProps>(child)) {
          return React.cloneElement(child, { _activeTab, _onTabChange } as Partial<TabsTriggerProps>)
        }
        return child
      })}
    </div>
  )
}

interface TabsTriggerProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  value: string
  _activeTab?: string
  _onTabChange?: (value: string) => void
}

function TabsTrigger({ className, value, _activeTab, _onTabChange, children, ...props }: TabsTriggerProps) {
  const isActive = _activeTab === value
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center whitespace-nowrap rounded-sm px-3 py-1.5 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50 cursor-pointer",
        isActive && "bg-background text-foreground shadow-sm",
        className
      )}
      onClick={() => _onTabChange?.(value)}
      {...props}
    >
      {children}
    </button>
  )
}

interface TabsContentProps extends React.HTMLAttributes<HTMLDivElement> {
  value: string
  _activeTab?: string
  _onTabChange?: (value: string) => void
}

function TabsContent({ className, value, _activeTab, _onTabChange: _, children, ...props }: TabsContentProps) {
  if (_activeTab !== value) return null
  return (
    <div className={cn("mt-2 ring-offset-background focus-visible:outline-none", className)} {...props}>
      {children}
    </div>
  )
}

export { Tabs, TabsList, TabsTrigger, TabsContent }
