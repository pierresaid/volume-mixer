import NoSleep from 'nosleep.js'
import { ref } from 'vue'

// Keep the phone screen on while mixing. The Wake Lock API needs HTTPS, so on a
// plain-HTTP LAN page this rides NoSleep's muted-video technique instead.
// Best effort by design: if the OS still sleeps the screen, reconnect-on-unlock
// keeps the mixer correct anyway.

const KEY = 'vm.awake'

export const keepAwake = ref(localStorage.getItem(KEY) !== 'off')

const noSleep = new NoSleep()
let engaged = false

async function engage() {
  if (!keepAwake.value || engaged) return
  try {
    await noSleep.enable()
    engaged = true
  } catch {
    // will retry on the next user gesture
  }
}

function release() {
  try {
    noSleep.disable()
  } catch {}
  engaged = false
}

// runs inside a click handler, so enable() has its required user gesture
export function toggleAwake() {
  keepAwake.value = !keepAwake.value
  localStorage.setItem(KEY, keepAwake.value ? 'on' : 'off')
  if (keepAwake.value) void engage()
  else release()
}

// video autoplay rules want a gesture at least once, so arm on the first touch
for (const event of ['click', 'touchend'] as const) {
  document.addEventListener(event, () => void engage(), { passive: true })
}

// backgrounding pauses the hidden video; re-arm when the user comes back
document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    engaged = false
  } else if (keepAwake.value) {
    void engage()
  }
})
