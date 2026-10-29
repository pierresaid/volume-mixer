// Runs before anything else reads localStorage (main.ts imports it first).
//
// The pairing token arrives in the URL fragment (#t=...), which never reaches the server, and
// stays there on purpose: a bookmark or home-screen icon then carries it. That matters on
// iPhones, where a home-screen web app gets its own storage and can't see what Safari stored.
//
// Each address (IP or name) has its own storage too, so moving to the PC's .local name hands
// this phone's layout over in the fragment as well (#t=...&p=...).

const TOKEN_KEY = 'vm.token'
const PREF_KEYS = ['vm.hidden', 'vm.order', 'vm.mic', 'vm.awake', 'vm.tip.reorder']

const params = new URLSearchParams(window.location.hash.slice(1))

// base64url of UTF-8 JSON (app keys come from exe names, which can be non-ASCII)
function pack(value: unknown): string {
  const bytes = new TextEncoder().encode(JSON.stringify(value))
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

function unpack(packed: string): unknown {
  const binary = atob(packed.replace(/-/g, '+').replace(/_/g, '/'))
  return JSON.parse(new TextDecoder().decode(Uint8Array.from(binary, (c) => c.charCodeAt(0))))
}

export const token = params.get('t') ?? localStorage.getItem(TOKEN_KEY) ?? ''
if (params.get('t')) localStorage.setItem(TOKEN_KEY, token)

const handedPrefs = params.get('p')
if (handedPrefs) {
  try {
    const prefs = unpack(handedPrefs) as Record<string, string>
    for (const key of PREF_KEYS) {
      // what this address already knows wins over what was handed over
      if (typeof prefs[key] === 'string' && localStorage.getItem(key) === null) localStorage.setItem(key, prefs[key])
    }
  } catch {
    // a mangled link: just skip the layout
  }
}

if (token && (params.get('t') !== token || handedPrefs)) {
  history.replaceState(null, '', `${window.location.pathname}${window.location.search}#t=${token}`)
}

/** This page's address on another origin, carrying the token and this phone's layout. */
export function handoffUrl(origin: string): string {
  const prefs: Record<string, string> = {}
  for (const key of PREF_KEYS) {
    const value = localStorage.getItem(key)
    if (value !== null) prefs[key] = value
  }
  return `${origin}/#t=${token}&p=${pack(prefs)}`
}
