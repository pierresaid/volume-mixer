<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import pkg from '../package.json'
import ChannelStrip from './components/ChannelStrip.vue'
import { keepAwake, toggleAwake } from './lib/awake'
import { shortDeviceName, shortDeviceNames } from './lib/devices'
import {
  hiddenKeys,
  isIdle,
  mediaNext,
  mediaPlayPause,
  mediaPositionMs,
  mediaPrevious,
  mediaSeek,
  mediaSelect,
  micMode,
  orderApps,
  peaks,
  retryPairing,
  selectDevice,
  setAppVolume,
  setHidden,
  setMasterVolume,
  setMicMode,
  setOrder,
  start,
  state,
  status,
  toggleAppMute,
  toggleMasterMute,
  toggleMic,
} from './lib/mixer'
import { useLongPressReorder } from './lib/reorder'

const REPO_URL = 'https://github.com/pierresaid/volume-mixer'
const host = window.location.host

const showHidden = ref(false)
const showIdle = ref(false)
const showAbout = ref(false)
const confirmForget = ref(false)

const visibleApps = computed(() => orderApps(state.apps.filter((a) => !hiddenKeys.value.has(a.key))))
// apps whose audio has been off for a while fold into "Idle", still one tap away
const activeApps = computed(() => visibleApps.value.filter((a) => !isIdle(a)))
const idleApps = computed(() => visibleApps.value.filter((a) => isIdle(a)))
const hiddenApps = computed(() => state.apps.filter((a) => hiddenKeys.value.has(a.key)))
const deviceLabels = computed(() => shortDeviceNames(state.devices.map((d) => d.name)))
const micAvailable = computed(() => state.micMuted !== null)
const micInUse = computed(() => state.micUsers.length > 0)
const micVisible = computed(
  () => micAvailable.value && (micMode.value === 'shown' || (micMode.value === 'auto' && micInUse.value)),
)
const micHidden = computed(() => micAvailable.value && !micVisible.value)
const hiddenCount = computed(() => hiddenApps.value.length + (micHidden.value ? 1 : 0))

const micVars = computed(() => {
  const pct = state.micMuted ? 0 : Math.min(100, Math.round(peaks.mic * 100))
  return { '--fill': `${pct}%`, '--frac': String(Math.max(pct, 1) / 100) }
})

// "Headset (WH-1000XM6)" -> "WH-1000XM6"
const micDisplayName = computed(() => (state.micName ? shortDeviceName(state.micName) : ''))

const ledClass = computed(() =>
  status.value === 'live' ? '' : status.value === 'reconnecting' || status.value === 'connecting' ? 'warn' : 'off',
)

const machineLabel = computed(() => {
  switch (status.value) {
    case 'live': return state.machineName || 'live'
    case 'connecting': return 'connecting'
    case 'reconnecting': return state.machineName || 'reconnecting'
    case 'offline': return 'offline'
    case 'unpaired': return 'not paired'
  }
})

// × always clears the row right away; unpinning it keeps the pop-up during calls when that does
function hideMic() {
  setMicMode(micMode.value === 'shown' && !micInUse.value ? 'auto' : 'hidden')
}

// ---- reorder: long-press an app's name and drag it (within its list). The order is per phone. ----

const TIP_KEY = 'vm.tip.reorder'
const reorderTipDone = ref(localStorage.getItem(TIP_KEY) === 'done')

function dismissReorderTip() {
  reorderTipDone.value = true
  localStorage.setItem(TIP_KEY, 'done')
}

const reorder = useLongPressReorder((from, to) => {
  const list = [activeApps.value, idleApps.value].find(
    (apps) => apps.some((a) => a.key === from) && apps.some((a) => a.key === to),
  )
  if (!list) return
  const keys = list.map((a) => a.key)
  keys.splice(keys.indexOf(from), 1)
  keys.splice(list.findIndex((a) => a.key === to), 0, from) // lands after the target moving down, before it moving up
  const active = list === activeApps.value ? keys : activeApps.value.map((a) => a.key)
  const idle = list === idleApps.value ? keys : idleApps.value.map((a) => a.key)
  setOrder([...active, ...idle])
  dismissReorderTip()
})

// ---- seek bar: always scrubbable, with a smaller cap than the volume faders.
// Dragging previews the target time; the seek is sent only on release. ----

const scrubbing = ref(false)
const scrubMs = ref(0)

const shownPositionMs = computed(() => (scrubbing.value ? scrubMs.value : mediaPositionMs.value))

function onScrubInput(event: Event) {
  scrubbing.value = true
  scrubMs.value = Number((event.target as HTMLInputElement).value)
}

function onScrubRelease(event: Event) {
  mediaSeek(Number((event.target as HTMLInputElement).value))
  scrubbing.value = false
}

function fmt(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`
}

function openAbout() {
  confirmForget.value = false
  showAbout.value = true
}

function forgetPc() {
  if (!confirmForget.value) {
    confirmForget.value = true
    window.setTimeout(() => (confirmForget.value = false), 3500)
    return
  }
  for (const key of Object.keys(localStorage)) {
    if (key.startsWith('vm.')) localStorage.removeItem(key)
  }
  window.location.href = window.location.origin
}

onMounted(start)
</script>

<template>
  <header class="masthead">
    <div class="machine">
      <span class="led" :class="ledClass"></span>
      <span class="name">{{ machineLabel }}</span>
    </div>
    <button class="wordmark" aria-haspopup="dialog" @click="openAbout">Volume Mixer</button>
  </header>

  <div v-if="status === 'reconnecting'" class="reconnect-strip">
    reconnecting, controls resume in a moment
  </div>

  <template v-if="status === 'unpaired'">
    <div class="notice">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
        <circle cx="8" cy="15" r="4" />
        <path d="M10.9 12.1 20 3M15 8l3 3" />
      </svg>
      <h2>This phone isn't paired</h2>
      <p>
        On the PC, right-click the Volume Mixer tray icon, choose
        <strong>Show QR code</strong>, and scan it again with your camera.
      </p>
      <button class="action" @click="retryPairing">Try again</button>
    </div>
  </template>

  <template v-else-if="status === 'offline'">
    <div class="notice">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
        <path d="M12 19.5v.01M8.5 16a5 5 0 0 1 7 0M5.5 12.5a10 10 0 0 1 4.1-2.4M14.5 10.1a10 10 0 0 1 4 2.4M2.5 9a15 15 0 0 1 19 0" />
        <path d="M4 4l16 16" />
      </svg>
      <h2>Can't reach {{ state.machineName || 'the PC' }}</h2>
      <p>
        Make sure the PC is on and this phone is on the same Wi-Fi.
        If the PC's address changed, re-scan the QR code from the tray icon.
      </p>
      <button class="action" @click="retryPairing">Retry now</button>
    </div>
  </template>

  <template v-else-if="status === 'connecting'">
    <div class="notice">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
        <path d="M6 18V10M12 18V4M18 18v-6" />
        <path d="M4 21h16" />
      </svg>
      <h2>Connecting…</h2>
      <p>Looking for the Volume Mixer server on your network.</p>
    </div>
  </template>

  <template v-else>
    <main class="console">
      <div v-if="state.media" class="channel transport">
        <div class="art">
          <img v-if="state.media.artKey" :src="`/media/art/${state.media.artKey}`" alt="" />
          <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
            <circle cx="8" cy="18" r="3" />
            <path d="M11 18V5l9-2v13" />
            <circle cx="17" cy="16" r="3" />
          </svg>
        </div>
        <div class="track-meta">
          <div class="track-title">{{ state.media.title }}</div>
          <div class="track-artist">{{ state.media.artist }}</div>
        </div>
        <div class="transport-keys">
          <button class="mute-btn sm momentary" aria-label="Previous track" @click="mediaPrevious">
            <svg viewBox="0 0 24 24" fill="currentColor"><path d="M6 5h2v14H6zM20 5.5v13L9.5 12z" /></svg>
          </button>
          <button class="mute-btn sm momentary" :aria-label="state.media.playing ? 'Pause' : 'Play'" @click="mediaPlayPause">
            <svg v-if="state.media.playing" viewBox="0 0 24 24" fill="currentColor"><path d="M7 5h3.4v14H7zM13.6 5H17v14h-3.4z" /></svg>
            <svg v-else viewBox="0 0 24 24" fill="currentColor"><path d="M8 4.8l11.2 7.2L8 19.2z" /></svg>
          </button>
          <button class="mute-btn sm momentary" aria-label="Next track" @click="mediaNext">
            <svg viewBox="0 0 24 24" fill="currentColor"><path d="M16 5h2v14h-2zM4 5.5v13L14.5 12z" /></svg>
          </button>
        </div>

        <div v-if="state.media.canSeek && state.media.durationMs > 0" class="timeline">
          <div class="scrub">
            <span class="time" :class="{ live: scrubbing }">{{ fmt(shownPositionMs) }}</span>
            <div class="fader-slot scrub-slot">
              <div
                class="timefill"
                :style="{ width: `calc(${Math.min(100, (shownPositionMs / state.media.durationMs) * 100)}% - 8px)` }"
                aria-hidden="true"
              ></div>
              <input
                class="fader"
                type="range"
                min="0"
                :max="state.media.durationMs"
                step="1000"
                :value="shownPositionMs"
                aria-label="Seek"
                @pointerdown="scrubbing = true"
                @input="onScrubInput"
                @change="onScrubRelease"
              />
            </div>
            <span class="time total">{{ fmt(state.media.durationMs) }}</span>
          </div>
        </div>

        <div v-if="state.mediaSessions.length > 1" class="media-chips" role="group" aria-label="Media player">
          <button
            v-for="session in state.mediaSessions"
            :key="session.id"
            class="out-chip"
            :class="{ active: session.current }"
            :aria-pressed="session.current"
            @click="mediaSelect(session.id)"
          >
            {{ session.name }}
          </button>
        </div>
      </div>

      <ChannelStrip
        master
        name="Main out"
        :volume="state.master.volume"
        :muted="state.master.muted"
        :peak="peaks.master"
        @volume="setMasterVolume"
        @mute="toggleMasterMute"
      />

      <div v-if="state.devices.length > 1" class="channel outrow">
        <div class="out-label">Output</div>
        <div class="out-chips">
          <button
            v-for="(device, i) in state.devices"
            :key="device.id"
            class="out-chip"
            :class="{ active: device.isDefault }"
            :title="device.name"
            @click="selectDevice(device.id)"
          >
            {{ deviceLabels[i] }}
          </button>
        </div>
      </div>

      <div class="bank" @pointerdown="reorder.onPointerDown" @contextmenu="reorder.onContextMenu">
        <ChannelStrip
          v-for="app in activeApps"
          :key="app.key"
          :data-key="app.key"
          :name="app.name"
          :volume="app.volume"
          :muted="app.muted"
          :peak="peaks.apps[app.key] ?? 0"
          :icon-key="app.iconKey"
          hideable
          @volume="(v) => setAppVolume(app.key, v)"
          @mute="toggleAppMute(app.key)"
          @hide="setHidden(app.key, true)"
        />
      </div>

      <div v-if="activeApps.length === 0" class="channel empty">
        Nothing is playing right now.<br />
        Start some audio on {{ state.machineName || 'the PC' }} and it appears here by itself.
      </div>

      <button
        v-if="idleApps.length"
        class="channel hidden-head"
        :aria-expanded="showIdle"
        @click="showIdle = !showIdle"
      >
        <span class="hh-label">Idle</span>
        <span class="hh-count">{{ idleApps.length }}</span>
        <span class="hh-note">no sound for a while</span>
        <svg class="hh-chev" :class="{ open: showIdle }" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M6 10l6 6 6-6" />
        </svg>
      </button>
      <div
        v-if="showIdle && idleApps.length"
        class="bank idle-bank"
        @pointerdown="reorder.onPointerDown"
        @contextmenu="reorder.onContextMenu"
      >
        <ChannelStrip
          v-for="app in idleApps"
          :key="app.key"
          :data-key="app.key"
          :name="app.name"
          :volume="app.volume"
          :muted="app.muted"
          :peak="peaks.apps[app.key] ?? 0"
          :icon-key="app.iconKey"
          hideable
          @volume="(v) => setAppVolume(app.key, v)"
          @mute="toggleAppMute(app.key)"
          @hide="setHidden(app.key, true)"
        />
      </div>

      <section v-if="micVisible" class="channel microw" :class="{ 'is-muted': state.micMuted }">
        <div class="ch-head">
          <div class="mic-title">
            <span class="mic-label">Mic</span>
            <span v-if="micInUse" class="mic-users" :title="state.micName ?? ''">
              <span class="onair" aria-hidden="true"></span>
              <span class="mic-users-names">{{ state.micUsers.join(', ') }}</span>
            </span>
            <span v-else-if="micDisplayName" class="mic-name" :title="state.micName ?? ''">{{ micDisplayName }}</span>
          </div>
          <div v-if="state.micMuted" class="readout muted">MUTED</div>
          <button class="hide-btn" aria-label="Hide microphone" @click="hideMic">×</button>
        </div>
        <div class="ch-controls">
          <button
            class="mute-btn"
            :class="{ on: state.micMuted }"
            :aria-label="state.micMuted ? 'Unmute microphone' : 'Mute microphone'"
            :aria-pressed="state.micMuted"
            @click="toggleMic"
          >
            <svg v-if="!state.micMuted" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <rect x="9" y="3" width="6" height="11" rx="3" />
              <path d="M6 11a6 6 0 0 0 12 0M12 17v3.5" />
            </svg>
            <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <rect x="9" y="3" width="6" height="11" rx="3" />
              <path d="M6 11a6 6 0 0 0 12 0M12 17v3.5M4 3l16 18" />
            </svg>
          </button>
          <!-- no mic volume to set: the dim ladder is just the unlit LEDs, the bright one is the level -->
          <div class="fader-slot">
            <div class="ledbar set" style="--fill: 100%; --frac: 1" aria-hidden="true"></div>
            <div class="ledbar live" :style="micVars" aria-hidden="true"></div>
          </div>
        </div>
      </section>

      <button
        v-if="hiddenCount"
        class="channel hidden-head"
        :aria-expanded="showHidden"
        @click="showHidden = !showHidden"
      >
        <span class="hh-label">Hidden</span>
        <span class="hh-count">{{ hiddenCount }}</span>
        <svg class="hh-chev" :class="{ open: showHidden }" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M6 10l6 6 6-6" />
        </svg>
      </button>
      <template v-if="showHidden">
        <button
          v-for="app in hiddenApps"
          :key="app.key"
          class="channel hidden-row"
          :aria-label="`Show ${app.name} again`"
          @click="setHidden(app.key, false)"
        >
          <span class="chip">
            <img v-if="app.iconKey" :src="`/icons/${app.iconKey}`" alt="" loading="lazy" />
            <span v-else class="letter">{{ (app.name.trim()[0] ?? '?').toUpperCase() }}</span>
          </span>
          <span class="hidden-name">{{ app.name }}</span>
          <span class="hidden-restore" aria-hidden="true">＋</span>
        </button>
        <button
          v-if="micHidden"
          class="channel hidden-row"
          aria-label="Always show the microphone"
          @click="setMicMode('shown')"
        >
          <span class="chip">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
              <rect x="9" y="3" width="6" height="11" rx="3" />
              <path d="M6 11a6 6 0 0 0 12 0M12 17v3.5" />
            </svg>
          </span>
          <span class="hidden-name">
            Mic <span class="hidden-note">{{ micMode === 'auto' ? 'shows up during calls' : 'hidden, even during calls' }}</span>
          </span>
          <span class="hidden-restore" aria-hidden="true">＋</span>
        </button>
      </template>
    </main>

    <footer class="deck-foot">
      <p v-if="!reorderTipDone && visibleApps.length > 1" class="foot-tip">
        Tip: long-press an app's name, then drag it to reorder.
        <button class="tip-close" aria-label="Dismiss tip" @click="dismissReorderTip">×</button>
      </p>
      <button class="foot-btn" :aria-pressed="keepAwake" @click="toggleAwake">
        <span class="dot" :class="{ on: keepAwake }"></span>
        keep screen on
      </button>
    </footer>
  </template>

  <div v-if="showAbout" class="modal-backdrop" @click="showAbout = false"></div>
  <div v-if="showAbout" class="modal" role="dialog" aria-label="About Volume Mixer">
    <div class="modal-head">
      <span class="modal-title">Volume Mixer</span>
      <span class="modal-version">v{{ pkg.version }}</span>
      <button class="hide-btn" aria-label="Close" @click="showAbout = false">×</button>
    </div>
    <div class="modal-sub">
      Connected to <strong>{{ state.machineName || '?' }}</strong> at <span class="mono">{{ host }}</span>
    </div>
    <a class="modal-link" :href="REPO_URL" target="_blank" rel="noreferrer">
      <svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 2A10 10 0 0 0 2 12c0 4.42 2.87 8.17 6.84 9.5c.5.08.66-.23.66-.5v-1.69c-2.77.6-3.36-1.34-3.36-1.34c-.46-1.16-1.11-1.47-1.11-1.47c-.91-.62.07-.6.07-.6c1 .07 1.53 1.03 1.53 1.03c.87 1.52 2.34 1.07 2.91.83c.09-.65.35-1.09.63-1.34c-2.22-.25-4.55-1.11-4.55-4.92c0-1.11.38-2 1.03-2.71c-.1-.25-.45-1.29.1-2.64c0 0 .84-.27 2.75 1.02c.79-.22 1.65-.33 2.5-.33c.85 0 1.71.11 2.5.33c1.91-1.29 2.75-1.02 2.75-1.02c.55 1.35.2 2.39.1 2.64c.65.71 1.03 1.6 1.03 2.71c0 3.82-2.34 4.66-4.57 4.91c.36.31.69.92.69 1.85V21c0 .27.16.59.67.5C19.14 20.16 22 16.42 22 12A10 10 0 0 0 12 2Z" /></svg>
      Project on GitHub <span class="ext" aria-hidden="true">↗</span>
    </a>
    <a class="modal-link" :href="`${REPO_URL}/issues/new`" target="_blank" rel="noreferrer">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="13" r="6" /><path d="M12 10v3.5M12 16v.01M9 4l1.5 2M15 4l-1.5 2" /></svg>
      Report a problem <span class="ext" aria-hidden="true">↗</span>
    </a>
    <button class="modal-link danger" @click="forgetPc">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M6.5 7l.9 12a1 1 0 0 0 1 .9h7.2a1 1 0 0 0 1-.9l.9-12" /></svg>
      {{ confirmForget ? 'Tap again to confirm' : 'Forget this PC' }}
    </button>
  </div>
</template>
