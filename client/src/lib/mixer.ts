import {
  HubConnectionBuilder,
  HubConnectionState,
  type HubConnection,
} from '@microsoft/signalr'
import { reactive, ref } from 'vue'
import type {
  AppChannel,
  AudioDevice,
  ConnectionStatus,
  MasterState,
  MediaInfo,
  MixerSnapshot,
  PeakFrame,
} from './types'

// ---- pairing token: arrives once via the QR URL fragment, then lives in localStorage ----

const TOKEN_KEY = 'vm.token'

function resolveToken(): string {
  const match = window.location.hash.match(/[#&]t=([^&]+)/)
  if (match) {
    localStorage.setItem(TOKEN_KEY, match[1])
    return match[1]
  }
  return localStorage.getItem(TOKEN_KEY) ?? ''
}

const token = resolveToken()

// ---- reactive state ----

export const status = ref<ConnectionStatus>('connecting')
export const state = reactive({
  machineName: '',
  master: { volume: 0, muted: false } as MasterState,
  micMuted: null as boolean | null,
  micName: null as string | null,
  devices: [] as AudioDevice[],
  media: null as MediaInfo | null,
  apps: [] as AppChannel[],
})

// ---- live meters: server sends raw peaks at 10 Hz; a local ticker eases them
// down so the LEDs fall naturally even after the stream goes quiet ----

export const peaks = reactive({
  master: 0,
  mic: 0,
  apps: {} as Record<string, number>,
})

let lastPeakFrame: PeakFrame = { master: 0, mic: null, apps: {} }

function ease(shown: number, target: number): number {
  const next = target >= shown ? target : Math.max(target, shown * 0.7)
  return next < 0.004 ? 0 : next
}

window.setInterval(() => {
  peaks.master = ease(peaks.master, lastPeakFrame.master)
  peaks.mic = ease(peaks.mic, lastPeakFrame.mic ?? 0)
  for (const app of state.apps)
    peaks.apps[app.key] = ease(peaks.apps[app.key] ?? 0, lastPeakFrame.apps[app.key] ?? 0)
}, 100)

// ---- hidden + pinned channels (per phone, in localStorage) ----

const HIDDEN_KEY = 'vm.hidden'
export const hiddenKeys = ref<Set<string>>(new Set(JSON.parse(localStorage.getItem(HIDDEN_KEY) ?? '[]')))

export function setHidden(key: string, hidden: boolean) {
  const next = new Set(hiddenKeys.value)
  if (hidden) next.add(key)
  else next.delete(key)
  hiddenKeys.value = next
  localStorage.setItem(HIDDEN_KEY, JSON.stringify([...next]))
}

// custom channel order: apps you've moved keep their place; the rest sort naturally.
// (migrates the old vm.pins list, which used the same "these keys go first" idea)
const ORDER_KEY = 'vm.order'
const order = ref<string[]>(
  JSON.parse(localStorage.getItem(ORDER_KEY) ?? 'null') ?? JSON.parse(localStorage.getItem('vm.pins') ?? '[]'),
)

export function orderApps(apps: AppChannel[]): AppChannel[] {
  const index = new Map(order.value.map((k, i) => [k, i]))
  return [...apps].sort((a, b) => {
    const pa = index.get(a.key) ?? Infinity
    const pb = index.get(b.key) ?? Infinity
    if (pa !== pb) return pa - pb
    return Number(a.isSystem) - Number(b.isSystem) || a.name.localeCompare(b.name)
  })
}

/** Swap the channel with the one above it (within the currently visible list). */
export function moveUp(key: string, visibleKeys: string[]) {
  const i = visibleKeys.indexOf(key)
  if (i <= 0) return
  const next = [...visibleKeys]
  ;[next[i - 1], next[i]] = [next[i], next[i - 1]]
  // snapshot the whole visible sequence, keep positions of currently-closed apps after it
  order.value = [...next, ...order.value.filter((k) => !next.includes(k))]
  localStorage.setItem(ORDER_KEY, JSON.stringify(order.value))
}

// ---- local-input hold: while a finger is on a fader, inbound echoes for that one
// channel are ignored so the slider never fights the user's thumb ----

const HOLD_MS = 600
const holds = new Map<string, number>() // channel key ('' = master) -> last local input timestamp

function hold(key: string) {
  holds.set(key, Date.now())
}

function isHeld(key: string): boolean {
  const at = holds.get(key)
  return at !== undefined && Date.now() - at < HOLD_MS
}

// ---- connection ----

const connection: HubConnection = new HubConnectionBuilder()
  .withUrl('/volumehub', { accessTokenFactory: () => token })
  .withAutomaticReconnect({
    // never give up; the stock policy stops after 4 attempts
    nextRetryDelayInMilliseconds: (ctx) => Math.min(250 * 2 ** ctx.previousRetryCount, 5000),
  })
  .build()

// server pings every 5s (ClientTimeoutInterval 15s), mirror that on our side
connection.keepAliveIntervalInMilliseconds = 5000
connection.serverTimeoutInMilliseconds = 15000

connection.on('snapshot', (snapshot: MixerSnapshot) => {
  state.machineName = snapshot.machineName
  state.micMuted = snapshot.micMuted ?? null
  state.micName = snapshot.micName ?? null
  state.devices = snapshot.devices ?? []
  applyMedia(snapshot.media ?? null)
  if (!isHeld('')) state.master = snapshot.master
  const localByKey = new Map(state.apps.map((a) => [a.key, a]))
  state.apps = snapshot.apps.map((app) => {
    const local = localByKey.get(app.key)
    return local && isHeld(app.key) ? { ...app, volume: local.volume } : app
  })
  status.value = 'live'
})

connection.on('appUpserted', (app: AppChannel) => {
  const index = state.apps.findIndex((a) => a.key === app.key)
  if (isHeld(app.key) && index >= 0) {
    // keep the locally-dragged volume, accept everything else
    state.apps[index] = { ...app, volume: state.apps[index].volume }
    return
  }
  if (index >= 0) state.apps[index] = app
  else state.apps = [...state.apps, app]
})

connection.on('appRemoved', (key: string) => {
  state.apps = state.apps.filter((a) => a.key !== key)
})

connection.on('masterChanged', (master: MasterState) => {
  if (!isHeld('')) state.master = master
})

connection.on('micChanged', (muted: boolean) => {
  state.micMuted = muted
})

connection.on('devicesChanged', (devices: AudioDevice[]) => {
  state.devices = devices
})

connection.on('mediaChanged', (media: MediaInfo | null) => {
  applyMedia(media)
})

connection.on('peaks', (frame: PeakFrame) => {
  lastPeakFrame = frame
})

connection.onreconnecting(() => {
  status.value = 'reconnecting'
})
// on successful reconnect the server pushes a fresh snapshot, which sets status back to 'live'
connection.onclose(() => {
  if (status.value !== 'unpaired') status.value = 'offline'
})

async function ensureConnected() {
  if (connection.state !== HubConnectionState.Disconnected) return
  if (status.value !== 'connecting') status.value = 'reconnecting'
  try {
    await connection.start()
  } catch (error: unknown) {
    const message = error instanceof Error ? error.message : String(error)
    if (message.includes('401')) {
      status.value = 'unpaired'
      return
    }
    status.value = 'offline'
    // background timers are throttled on phones; lifecycle events below are the
    // real recovery path; this timer only covers the screen-stays-on case
    window.setTimeout(ensureConnected, 3000)
  }
}

// the moment the user comes back (unlock, tab switch, network back), reconnect NOW
for (const event of ['visibilitychange', 'pageshow', 'focus', 'online'] as const) {
  window.addEventListener(event, () => {
    if (!document.hidden && status.value !== 'unpaired') void ensureConnected()
  })
}

export function start() {
  void ensureConnected()
}

export function retryPairing() {
  status.value = 'connecting'
  void ensureConnected()
}

// ---- outgoing commands: apply locally right away, throttle the wire to ~1/75ms ----

const THROTTLE_MS = 75
const throttles = new Map<string, { timer: number | null; pending: number | null }>()

function sendThrottled(key: string, volume: number, send: (volume: number) => void) {
  let slot = throttles.get(key)
  if (!slot) {
    slot = { timer: null, pending: null }
    throttles.set(key, slot)
  }
  if (slot.timer === null) {
    send(volume)
    slot.timer = window.setTimeout(function flush() {
      const s = throttles.get(key)
      if (!s) return
      if (s.pending !== null) {
        const value = s.pending
        s.pending = null
        send(value)
        s.timer = window.setTimeout(flush, THROTTLE_MS)
      } else {
        s.timer = null
      }
    }, THROTTLE_MS)
  } else {
    slot.pending = volume
  }
}

function invoke(method: string, ...args: unknown[]) {
  if (connection.state === HubConnectionState.Connected) {
    void connection.send(method, ...args).catch(() => {})
  }
}

export function setAppVolume(key: string, volume: number) {
  hold(key)
  const app = state.apps.find((a) => a.key === key)
  if (app) app.volume = volume
  sendThrottled(key, volume, (v) => invoke('SetAppVolume', key, v))
}

export function toggleAppMute(key: string) {
  const app = state.apps.find((a) => a.key === key)
  if (!app) return
  app.muted = !app.muted
  hold(key)
  invoke('SetAppMuted', key, app.muted)
}

export function setMasterVolume(volume: number) {
  hold('')
  state.master.volume = volume
  sendThrottled('', volume, (v) => invoke('SetMasterVolume', v))
}

export function toggleMasterMute() {
  state.master.muted = !state.master.muted
  hold('')
  invoke('SetMasterMuted', state.master.muted)
}

export function toggleMic() {
  if (state.micMuted === null) return
  state.micMuted = !state.micMuted
  invoke('SetMicMuted', state.micMuted)
}

export function selectDevice(id: string) {
  if (state.devices.find((d) => d.id === id)?.isDefault) return
  state.devices = state.devices.map((d) => ({ ...d, isDefault: d.id === id })) // optimistic
  invoke('SetDefaultDevice', id)
}

export function mediaPlayPause() {
  if (state.media) state.media = { ...state.media, playing: !state.media.playing } // optimistic
  invoke('MediaPlayPause')
}

export function mediaNext() {
  invoke('MediaNext')
}

export function mediaPrevious() {
  invoke('MediaPrevious')
}

// ---- media timeline: the server sends position ANCHORS; we extrapolate locally ----

export const mediaPositionMs = ref(0)
let mediaAnchorMs = 0
let mediaAnchorAt = Date.now()
let seekHoldUntil = 0 // ignore stale position anchors briefly after we seek

function applyMedia(media: MediaInfo | null) {
  const sameTrack = media && state.media && media.title === state.media.title
  const holding = sameTrack && Date.now() < seekHoldUntil
  state.media = media
  if (!media) {
    mediaPositionMs.value = 0
    mediaAnchorMs = 0
    return
  }
  if (holding) return // keep our optimistic post-seek anchor
  mediaAnchorMs = media.positionMs ?? 0
  mediaAnchorAt = Date.now()
  mediaPositionMs.value = mediaAnchorMs
}

window.setInterval(() => {
  const media = state.media
  if (!media || media.durationMs <= 0) return
  const position = media.playing
    ? mediaAnchorMs + (Date.now() - mediaAnchorAt) * (media.rate || 1)
    : mediaAnchorMs
  mediaPositionMs.value = Math.min(Math.max(position, 0), media.durationMs)
}, 250)

export function mediaSeek(positionMs: number) {
  seekHoldUntil = Date.now() + 2500
  mediaAnchorMs = positionMs
  mediaAnchorAt = Date.now()
  mediaPositionMs.value = positionMs
  invoke('MediaSeek', Math.round(positionMs))
}

// dev-only hook so the UI can be exercised with fake media during development
if (import.meta.env.DEV) {
  ;(window as unknown as Record<string, unknown>).__mixer = { state, peaks, setMedia: applyMedia }
}
