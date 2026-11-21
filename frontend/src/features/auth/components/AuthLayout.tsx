import type { ReactNode } from 'react'
import { Logo } from '../../../components/Logo'

// ##### Brand panel #####

function BrandPanel() {
  return (
    <div className="hidden lg:flex lg:w-[45%] flex-col justify-between bg-gradient-to-br from-blue-700 to-sky-700 p-10 text-white">
      <div className="flex items-center gap-2.5">
        <Logo size={32} className="text-white" />
        <span className="text-base font-semibold tracking-tight">ChatNET</span>
      </div>

      <div>
        <h1 className="text-3xl font-bold leading-snug">
          Say hi.<br />Keep it between you.
        </h1>
        <p className="mt-3 max-w-xs text-sm leading-relaxed text-white">
          Chats, calls and files with your people, end-to-end encrypted. No work talk required.
        </p>
      </div>

      <p className="text-xs text-white">© 2026 ChatNET</p>
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

      <div className="flex-1 flex flex-col items-center justify-center p-8 bg-page">
        {/* From a tablet up the form is a card over the page colour; on a phone it stays flat, to keep the width. */}
        <div className="w-full max-w-sm animate-enter sm:max-w-md sm:rounded-2xl sm:bg-surface sm:p-8 sm:shadow-card">
          {/* Logo shown only on mobile where BrandPanel is hidden */}
          <div className="mb-8 flex items-center gap-2 lg:hidden">
            <Logo tile size={32} />
            <span className="font-semibold text-fg">ChatNET</span>
          </div>

          <div className="mb-7">
            <h2 className="text-2xl font-semibold tracking-tight text-fg">{title}</h2>
            <p className="mt-1 text-sm text-fg-muted">{subtitle}</p>
          </div>

          {children}
        </div>
      </div>
    </div>
  )
}
