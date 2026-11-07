import type { ReactNode } from 'react'
import { MessagesSquare } from 'lucide-react'

// ##### Brand panel #####

function BrandPanel() {
  return (
    <div className="hidden lg:flex lg:w-[45%] flex-col justify-between bg-gradient-to-br from-blue-700 to-sky-500 p-10 text-white">
      <div className="flex items-center gap-2.5">
        <MessagesSquare className="h-6 w-6" />
        <span className="text-base font-semibold tracking-tight">ChatNET</span>
      </div>

      <div>
        <h1 className="text-3xl font-bold leading-snug">
          Real-time conversations<br />for your team.
        </h1>
        <p className="mt-3 max-w-xs text-sm leading-relaxed text-blue-100">
          Simple, fast, and reliable messaging. Built for how teams actually work.
        </p>
      </div>

      <p className="text-xs text-blue-200/60">© 2026 ChatNET</p>
    </div>
  )
}

// ##### Layout #####

interface Props {
  title: string
  subtitle: string
  children: ReactNode
}

// The split-panel layout every page for a person who is not signed in uses: the brand panel on the left, hidden on a phone, and the
// form on the right.
export function AuthLayout({ title, subtitle, children }: Props) {
  return (
    <div className="min-h-screen flex">
      <BrandPanel />

      <div className="flex-1 flex flex-col items-center justify-center p-8 bg-slate-50">
        <div className="w-full max-w-sm">
          {/* Logo shown only on mobile where BrandPanel is hidden */}
          <div className="mb-8 flex items-center gap-2 lg:hidden">
            <MessagesSquare className="h-6 w-6 text-blue-600" />
            <span className="font-semibold text-slate-900">ChatNET</span>
          </div>

          <div className="mb-7">
            <h2 className="text-2xl font-semibold text-slate-900">{title}</h2>
            <p className="mt-1 text-sm text-slate-500">{subtitle}</p>
          </div>

          {children}
        </div>
      </div>
    </div>
  )
}
