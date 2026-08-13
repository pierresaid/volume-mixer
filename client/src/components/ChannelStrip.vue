<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'

const props = defineProps<{
  name: string
  volume: number
  muted: boolean
  peak?: number // 0..1 live level
  iconKey?: string | null
  master?: boolean
  hideable?: boolean
  canMoveUp?: boolean
}>()

const emit = defineEmits<{
  volume: [value: number]
  mute: []
  hide: []
  moveup: []
}>()

const letter = computed(() => (props.name.trim()[0] ?? '?').toUpperCase())

const setVars = computed(() => ({
  '--fill': `${props.volume}%`,
  '--frac': String(Math.max(props.volume, 1) / 100),
}))

const liveVars = computed(() => {
  const pct = Math.min(100, Math.round((props.peak ?? 0) * 100))
  return {
    '--fill': `${pct}%`,
    '--frac': String(Math.max(pct, 1) / 100),
  }
})

// haptic ticks when the finger crosses a detent (Android; iOS ignores vibrate)
const DETENTS = [0, 25, 50, 75, 100]
let lastValue = props.volume

function onInput(event: Event) {
  const value = Number((event.target as HTMLInputElement).value)
  if (DETENTS.some((d) => (lastValue < d && value >= d) || (lastValue > d && value <= d)))
    navigator.vibrate?.(8)
  lastValue = value
  emit('volume', value)
}

// fine stepper: tap the readout, then −/+ with hold-to-repeat
const stepperOpen = ref(false)
let repeatTimer: number | null = null

function nudge(delta: number) {
  emit('volume', Math.max(0, Math.min(100, props.volume + delta)))
}

function startRepeat(delta: number) {
  stopRepeat()
  nudge(delta)
  repeatTimer = window.setTimeout(function tick() {
    nudge(delta)
    repeatTimer = window.setTimeout(tick, 110)
  }, 380)
}

function stopRepeat() {
  if (repeatTimer !== null) {
    clearTimeout(repeatTimer)
    repeatTimer = null
  }
}

function onMuteClick() {
  navigator.vibrate?.(12)
  emit('mute')
}

function onMoveUp() {
  navigator.vibrate?.(8)
  emit('moveup')
}

onBeforeUnmount(stopRepeat)
</script>

<template>
  <section class="channel" :class="{ main: master, 'is-muted': muted }">
    <div class="ch-head">
      <div v-if="!master" class="chip">
        <img v-if="iconKey" :src="`/icons/${iconKey}`" alt="" loading="lazy" />
        <span v-else class="letter">{{ letter }}</span>
      </div>
      <div class="ch-name">{{ name }}</div>
      <button class="readout" :class="{ muted }" :aria-label="`Fine-adjust ${name} volume`" @click="stepperOpen = !stepperOpen">
        {{ muted ? 'MUTE' : volume }}
      </button>
      <button
        v-if="hideable"
        class="up-btn"
        :disabled="!canMoveUp"
        :aria-label="`Move ${name} up`"
        @click="onMoveUp"
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M6 14.5l6-6 6 6" />
        </svg>
      </button>
      <button
        v-if="hideable"
        class="hide-btn"
        :aria-label="`Hide ${name}`"
        @click="emit('hide')"
      >
        ×
      </button>
    </div>
    <div class="ch-controls">
      <button
        class="mute-btn"
        :class="{ on: muted }"
        :aria-label="muted ? `Unmute ${name}` : `Mute ${name}`"
        :aria-pressed="muted"
        @click="onMuteClick"
      >
        <svg v-if="!muted" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <path d="M4.5 9.2h3.4l4.6-3.7v13l-4.6-3.7H4.5z" fill="currentColor" stroke="none" />
          <path d="M16 9.7a3.6 3.6 0 0 1 0 4.6M18.6 7.4a7.2 7.2 0 0 1 0 9.2" />
        </svg>
        <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <path d="M4.5 9.2h3.4l4.6-3.7v13l-4.6-3.7H4.5z" fill="currentColor" stroke="none" />
          <path d="M16.2 9.8l4.6 4.6M20.8 9.8l-4.6 4.6" />
        </svg>
      </button>
      <div class="fader-slot">
        <div class="ledbar set" :style="setVars" aria-hidden="true"></div>
        <div class="ledbar live" :style="liveVars" aria-hidden="true"></div>
        <input
          class="fader"
          type="range"
          min="0"
          max="100"
          step="1"
          :value="volume"
          :aria-label="`${name} volume`"
          @input="onInput"
        />
      </div>
    </div>
    <div v-if="master" class="ruler" aria-hidden="true">
      <span>0</span><span>25</span><span>50</span><span>75</span><span>100</span>
    </div>

    <div v-if="stepperOpen" class="stepper-backdrop" @click="stepperOpen = false"></div>
    <div v-if="stepperOpen" class="stepper">
      <button
        class="step-btn"
        aria-label="Volume down"
        @pointerdown.prevent="startRepeat(-1)"
        @pointerup="stopRepeat"
        @pointercancel="stopRepeat"
        @pointerleave="stopRepeat"
      >−</button>
      <div class="step-val">{{ volume }}</div>
      <button
        class="step-btn"
        aria-label="Volume up"
        @pointerdown.prevent="startRepeat(1)"
        @pointerup="stopRepeat"
        @pointercancel="stopRepeat"
        @pointerleave="stopRepeat"
      >+</button>
    </div>
  </section>
</template>
