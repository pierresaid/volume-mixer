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
  active: boolean // an audio stream is running right now
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

/** A media player Windows knows about; "now playing" follows the current one. */
export interface MediaSessionInfo {
  id: string
  name: string
  current: boolean
}

export interface MixerSnapshot {
  machineName: string
  hostName: string // the PC's network name; phones try <hostName>.local
  master: MasterState
  micMuted: boolean | null
  micName: string | null
  micUsers: string[] // apps recording from the mic right now, e.g. a call
  devices: AudioDevice[]
  media: MediaInfo | null
  mediaSessions: MediaSessionInfo[]
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
