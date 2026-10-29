// Windows names endpoints "<endpoint> (<device>)": "Speakers (Realtek(R) Audio)",
// "Headphones (WH-1000XM6)", or a name the user gave it: "Desk speakers (Realtek(R) Audio)".
// Show whichever half says more: the device when the endpoint name is a stock one.

const STOCK_ENDPOINT =
  /^(speakers?|headphones?|headset( earphone| microphone)?|earphones?|microphone( array)?|line( in| out)?|output|input)$/i

export function shortDeviceName(full: string): string {
  // the last top-level "(...)" is the device; it may hold one nested pair, as in "Realtek(R) Audio"
  const match = full.match(/^(.*?)\s*\(((?:[^()]|\([^()]*\))*)\)\s*$/)
  let name = full
  if (match && match[1] && match[2]) name = STOCK_ENDPOINT.test(match[1].trim()) ? match[2] : match[1]
  return name
    .replace(/\((R|TM)\)|[®™]/gi, '')
    .replace(/^\d+-\s*/, '') // "2- USB Audio Device": Windows numbering duplicates
    .replace(/\s{2,}/g, ' ')
    .trim() || full
}

/** Short names for a set of devices, falling back to the full name where two would read the same. */
export function shortDeviceNames(fullNames: string[]): string[] {
  const short = fullNames.map(shortDeviceName)
  return short.map((name, i) => (short.indexOf(name) !== short.lastIndexOf(name) ? fullNames[i] : name))
}
