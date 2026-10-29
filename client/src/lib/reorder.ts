// Long-press an app's name, then drag it to a new place. Only the name row is a handle, so the
// faders and buttons work exactly as before, and a normal swipe still scrolls: the press has to
// hold still for LONG_PRESS_MS before anything moves. Items are the container's children that
// carry data-key; works for the one-column list and the two-column grid alike.

const LONG_PRESS_MS = 400
const MOVE_TOLERANCE = 8 // px a finger may wander before the press counts as a scroll
const EDGE = 64 // px from the top/bottom of the screen where a drag scrolls the page

interface Slot {
  el: HTMLElement
  key: string
  left: number // page coordinates, measured when the item lifts
  right: number
  top: number
  bottom: number
}

// Once an item is lifted the finger must not scroll the page. This has to be a non-passive
// listener registered ahead of time; it does nothing unless a drag is on.
let dragging = false
document.addEventListener('touchmove', (event) => {
  if (dragging) event.preventDefault()
}, { passive: false })

export function useLongPressReorder(onDrop: (fromKey: string, toKey: string) => void) {
  let timer = 0
  let scrollFrame = 0
  let pointerId = -1
  let startX = 0
  let startY = 0
  let startScrollY = 0
  let lastX = 0
  let lastY = 0
  let container: HTMLElement | null = null
  let item: HTMLElement | null = null
  let slots: Slot[] = []
  let target: Slot | null = null
  let lifted = false

  function onPointerDown(event: PointerEvent) {
    if (event.button !== 0 || item) return
    const el = event.target as HTMLElement
    if (!el.closest('.ch-head') || el.closest('button')) return
    const row = el.closest<HTMLElement>('[data-key]')
    if (!row || row.parentElement !== event.currentTarget) return

    container = event.currentTarget as HTMLElement
    item = row
    pointerId = event.pointerId
    startX = lastX = event.clientX
    startY = lastY = event.clientY
    startScrollY = window.scrollY
    timer = window.setTimeout(lift, LONG_PRESS_MS)
    window.addEventListener('pointermove', onPointerMove)
    window.addEventListener('pointerup', onPointerEnd)
    window.addEventListener('pointercancel', onPointerEnd)
  }

  function lift() {
    if (!item || !container) return
    lifted = dragging = true
    navigator.vibrate?.(12)
    slots = [...container.children]
      .filter((child): child is HTMLElement => child instanceof HTMLElement && !!child.dataset.key)
      .map((el) => {
        const r = el.getBoundingClientRect()
        return {
          el,
          key: el.dataset.key!,
          left: r.left + window.scrollX,
          right: r.right + window.scrollX,
          top: r.top + window.scrollY,
          bottom: r.bottom + window.scrollY,
        }
      })
    item.classList.add('lifted')
    document.documentElement.classList.add('reordering')
    scrollFrame = requestAnimationFrame(autoScroll)
  }

  function onPointerMove(event: PointerEvent) {
    if (event.pointerId !== pointerId) return
    lastX = event.clientX
    lastY = event.clientY
    if (!lifted) {
      if (Math.hypot(lastX - startX, lastY - startY) > MOVE_TOLERANCE) reset() // it's a scroll
      return
    }
    follow()
  }

  // the item tracks the finger; the slot under the finger shows where it will land
  function follow() {
    if (!item) return
    const dx = lastX - startX
    const dy = lastY - startY + (window.scrollY - startScrollY)
    item.style.transform = `translate(${dx}px, ${dy}px)`

    const x = lastX + window.scrollX
    const y = lastY + window.scrollY
    const hit = slots.find((s) => s.el !== item && x >= s.left && x <= s.right && y >= s.top && y <= s.bottom) ?? null
    if (hit?.el === target?.el) return
    target?.el.classList.remove('drop-before', 'drop-after')
    target = hit
    if (hit) {
      const from = slots.findIndex((s) => s.el === item)
      hit.el.classList.add(slots.indexOf(hit) > from ? 'drop-after' : 'drop-before')
      navigator.vibrate?.(4)
    }
  }

  function autoScroll() {
    if (!lifted) return
    const step = lastY < EDGE ? -10 : lastY > window.innerHeight - EDGE ? 10 : 0
    if (step) {
      window.scrollBy(0, step)
      follow()
    }
    scrollFrame = requestAnimationFrame(autoScroll)
  }

  function onPointerEnd(event: PointerEvent) {
    if (event.pointerId !== pointerId) return
    const from = item?.dataset.key
    const to = target?.key
    const drop = lifted && event.type === 'pointerup' && from && to
    reset()
    if (drop) onDrop(from, to)
  }

  function reset() {
    window.clearTimeout(timer)
    cancelAnimationFrame(scrollFrame)
    window.removeEventListener('pointermove', onPointerMove)
    window.removeEventListener('pointerup', onPointerEnd)
    window.removeEventListener('pointercancel', onPointerEnd)
    if (item) {
      item.classList.remove('lifted')
      item.style.transform = ''
    }
    target?.el.classList.remove('drop-before', 'drop-after')
    document.documentElement.classList.remove('reordering')
    container = item = target = null
    slots = []
    lifted = dragging = false
    pointerId = -1
  }

  /** A long press on text would otherwise open the browser's selection or context menu. */
  function onContextMenu(event: Event) {
    if ((event.target as HTMLElement).closest('.ch-head')) event.preventDefault()
  }

  return { onPointerDown, onContextMenu }
}
