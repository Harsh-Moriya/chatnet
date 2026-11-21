import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { Alert } from './Alert'

const html = (props: Parameters<typeof Alert>[0]) => renderToStaticMarkup(createElement(Alert, props))

describe('Alert', () => {
  it('is an error box by default, with its words inside', () => {
    const markup = html({ children: 'Could not reach the server.' })

    expect(markup).toContain('Could not reach the server.')
    expect(markup).toContain('bg-danger-soft')
    expect(markup).toContain('text-danger-fg')
  })

  it('takes its colours from its kind', () => {
    expect(html({ kind: 'warning', children: 'x' })).toContain('bg-warn-soft')
    expect(html({ kind: 'info', children: 'x' })).toContain('bg-accent-soft')
  })

  it('announces itself only when the caller says so', () => {
    expect(html({ children: 'x' })).not.toContain('role=')
    expect(html({ role: 'alert', children: 'x' })).toContain('role="alert"')
    expect(html({ kind: 'warning', role: 'note', children: 'x' })).toContain('role="note"')
  })

  it('draws the action at the end and centres the row on it', () => {
    const markup = html({ children: 'x', action: createElement('button', null, 'Try again') })

    expect(markup).toMatch(/items-center[^>]*>.*Try again/s)
    expect(markup).not.toContain('items-start')
  })

  it('is smaller text in the small size', () => {
    expect(html({ size: 'sm', children: 'x' })).toContain('text-xs')
    expect(html({ children: 'x' })).toContain('text-sm')
  })
})
