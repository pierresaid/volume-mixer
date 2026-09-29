export interface MasterState {
  volume: number
  muted: boolean
}

export interface AppChannel {
  key: string
  name: string
  iconKey: string | null
  volume: number
  muted: boolean
  isSystem: boolean
}

export interface AudioDevice {
  id: string
  name: string
  isDefault: boolean
}

export interface MediaInfo {
  title: string
  artist: string
  playing: boolean
  artKey: string | null
  canSeek: boolean
  positionMs: number
  durationMs: number
  rate: number
}

export interface MixerSnapshot {
  machineName: string
  master: MasterState
  micMuted: boolean | null
  micName: string | null
  micUsers: string[] // apps recording from the mic right now, e.g. a call
  devices: AudioDevice[]
  media: MediaInfo | null
  apps: AppChannel[]
}

export interface PeakFrame {
  master: number
  mic: number | null
  apps: Record<string, number>
}

export type ConnectionStatus =
  | 'connecting' // first connection attempt, nothing shown yet
  | 'live' // connected, state is fresh
  | 'reconnecting' // was live, transport dropped, retrying forever
  | 'offline' // start() failed, waiting for a lifecycle event to retry
  | 'unpaired' // server rejected the token (401), needs a new QR scan
