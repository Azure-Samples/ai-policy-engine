import { type ReactNode } from "react"
import { Sun, Moon, LogOut, Activity } from "lucide-react"
import { useMsal } from "@azure/msal-react"
import { useTheme } from "../context/ThemeProvider"
import { Button } from "./ui/button"
import { DropdownMenu, DropdownMenuItem } from "./ui/dropdown-menu"
import type { BillingMode } from "../types"

interface LayoutProps {
  children: ReactNode
  activeTab: string
  onTabChange: (tab: string) => void
  billingMode?: BillingMode
}

export function Layout({ children, activeTab, onTabChange, billingMode = 'token' }: LayoutProps) {
  const { resolvedTheme, setTheme, theme } = useTheme()
  const { instance, accounts } = useMsal()
  const account = accounts[0]

  const navItems = [
    { id: "dashboard", label: "Dashboard" },
    { id: "clients", label: "Clients" },
    { id: "plans", label: "Plans" },
    { id: "pricing", label: "Pricing" },
    { id: "routing", label: "Routing" },
    // Adaptive: show Request Billing only when at least one plan uses multiplier billing
    ...(billingMode !== 'token' ? [{ id: "requests", label: "Request Billing" }] : []),
    { id: "export", label: "Export" },
  ]

  return (
    <div className="min-h-screen bg-background">
      {/* Header */}
      <header className="sticky top-0 z-40 border-b bg-background/80 backdrop-blur-md">
        <div className="flex h-16 items-center justify-between px-6">
          <div className="flex items-center gap-4">
            <Activity className="h-6 w-6 text-[#0078D4]" />
            <h1 className="text-xl font-bold tracking-tight">Chargeback Dashboard</h1>
          </div>

          {/* Nav */}
          <nav className="hidden md:flex items-center gap-1">
            {navItems.map((item) => (
              <button
                key={item.id}
                onClick={() => onTabChange(item.id)}
                className={`px-4 py-2 rounded-md text-sm font-medium transition-colors cursor-pointer ${
                  activeTab === item.id
                    ? "bg-primary text-primary-foreground"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
                }`}
              >
                {item.label}
              </button>
            ))}
          </nav>

          {/* Right side */}
          <div className="flex items-center gap-3">
            {/* Theme toggle */}
            <Button
              variant="ghost"
              size="icon"
              onClick={() => setTheme(resolvedTheme === "dark" ? "light" : "dark")}
              title={`Current: ${theme}. Click to toggle.`}
            >
              {resolvedTheme === "dark" ? <Sun className="h-5 w-5" /> : <Moon className="h-5 w-5" />}
            </Button>

            {/* User menu */}
            {account && (
              <DropdownMenu
                trigger={
                  <div className="flex items-center gap-2 cursor-pointer rounded-md px-3 py-1.5 hover:bg-accent transition-colors">
                    <div className="h-8 w-8 rounded-full bg-[#0078D4] flex items-center justify-center text-white text-sm font-semibold">
                      {account.name?.charAt(0).toUpperCase() ?? "U"}
                    </div>
                    <span className="hidden lg:inline text-sm font-medium">{account.name}</span>
                  </div>
                }
              >
                <div className="px-2 py-1.5 text-sm text-muted-foreground border-b mb-1">
                  {account.username}
                </div>
                <DropdownMenuItem
                  onClick={() => instance.logoutPopup()}
                  className="text-destructive"
                >
                  <LogOut className="h-4 w-4 mr-2" />
                  Sign out
                </DropdownMenuItem>
              </DropdownMenu>
            )}
          </div>
        </div>

        {/* Mobile nav */}
        <div className="md:hidden flex border-t px-4 py-2 gap-1 overflow-x-auto">
          {navItems.map((item) => (
            <button
              key={item.id}
              onClick={() => onTabChange(item.id)}
              className={`px-3 py-1.5 rounded-md text-sm font-medium whitespace-nowrap transition-colors cursor-pointer ${
                activeTab === item.id
                  ? "bg-primary text-primary-foreground"
                  : "text-muted-foreground hover:bg-accent"
              }`}
            >
              {item.label}
            </button>
          ))}
        </div>
      </header>

      {/* Content */}
      <main className="mx-auto max-w-screen-2xl p-6">{children}</main>
    </div>
  )
}
