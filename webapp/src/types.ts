// Canonical configuration schema (the server stores profiles as raw JSON).

export type Orientation = 'portrait' | 'landscape';
export type Align = 'start' | 'center' | 'end';

export interface GridSize {
  rows: number;
  cols: number;
}

export interface Insets {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

export interface Layout {
  portrait: GridSize;
  landscape: GridSize;
  gapX: number;
  gapY: number;
  margin: Insets;
  keyShape: 'square' | 'fill';
  cornerRadius: number;
  align: { h: Align; v: Align };
  minKeySize: number;
}

export interface Background {
  color?: string;
  imageId?: string | null;
  fit?: 'cover' | 'contain' | 'tile';
}

export interface LabelStyle {
  text: string;
  size: number;
  color: string;
  position: 'top' | 'center' | 'bottom' | 'hidden';
  align?: 'left' | 'center' | 'right';
  bold?: boolean;
}

export interface IconStyle {
  kind: 'none' | 'emoji' | 'lucide' | 'image';
  value: string;
  color?: string;
  scale?: number;
}

export interface KeyStyle {
  label: LabelStyle;
  icon: IconStyle;
  bg: { color: string; imageId?: string | null };
  cornerRadius?: number | null;
}

export interface Cell {
  r: number;
  c: number;
}

export type MediaCommand =
  | 'playPause'
  | 'next'
  | 'prev'
  | 'stop'
  | 'volUp'
  | 'volDown'
  | 'volSet'
  | 'mute'
  | 'appVolUp'
  | 'appVolDown'
  | 'appVolSet'
  | 'appMute'
  | 'micMute'
  | 'output';

export interface HotkeyAction {
  type: 'hotkey';
  keys: string[];
}
export interface LaunchAction {
  type: 'launch';
  target: string;
  args?: string;
  cwd?: string;
  /** What to do when the app already has a window open (default 'new'). */
  ifOpen?: 'new' | 'focus' | 'close';
  /** Process to check for ifOpen; defaults to the launched .exe. */
  app?: string;
}
export interface MediaAction {
  type: 'media';
  command: MediaCommand;
  step?: number;
  /** volSet / appVolSet: 0..100 */
  level?: number;
  /** Per-app commands: process name, e.g. "spotify". */
  app?: string;
  /** micMute: toggle (default), on = mute, off = unmute. */
  mode?: 'toggle' | 'on' | 'off';
  /** output: one device = select it, more = cycle. */
  devices?: { id: string; name: string }[];
}
export interface SoundAction {
  type: 'sound';
  command?: 'play' | 'stopAll';
  file: string;
  volume?: number;
  mode?: 'restart' | 'toggle' | 'overlap';
}
export type SystemCommand =
  | 'lock'
  | 'sleep'
  | 'hibernate'
  | 'shutdown'
  | 'restart'
  | 'cancelShutdown'
  | 'logoff'
  | 'screenshot'
  | 'snip'
  | 'desktopNext'
  | 'desktopPrev'
  | 'desktopNew'
  | 'desktopClose'
  | 'taskView'
  | 'showDesktop'
  | 'brightnessUp'
  | 'brightnessDown'
  | 'brightnessSet';
export interface SystemAction {
  type: 'system';
  command: SystemCommand;
  /** Ask on the device before running (shutdown, restart…). */
  confirm?: boolean;
  delaySec?: number;
  step?: number;
  level?: number;
  area?: 'all' | 'primary' | 'window';
  target?: 'file' | 'clipboard' | 'both';
  folder?: string;
}
export type WindowCommand = 'maximize' | 'minimize' | 'restore' | 'toggleMax' | 'close' | 'focus' | 'topmost' | 'nextMonitor' | 'snap';
export interface WindowAction {
  type: 'window';
  command: WindowCommand;
  /** Empty = the active window on the PC. */
  app?: string;
  /** focus: program to open if the app is not running. */
  launch?: string;
  region?: string;
}
export interface CounterAction {
  type: 'counter';
  name: string;
  op: 'inc' | 'dec' | 'reset' | 'set';
  step?: number;
  value?: number;
}
export interface TimerAction {
  type: 'timer';
  name: string;
  op: 'toggle' | 'start' | 'pause' | 'reset';
  mode?: 'countdown' | 'stopwatch';
  durationSec?: number;
  /** Countdown end: Windows notification on the PC (default on). */
  alertPc?: boolean;
  /** Countdown end: sound + vibration on the devices (default on). */
  alertPhone?: boolean;
}
export type ObsCommand =
  | 'streamToggle'
  | 'streamStart'
  | 'streamStop'
  | 'recordToggle'
  | 'recordPause'
  | 'replaySave'
  | 'replayToggle'
  | 'virtualCam'
  | 'scene'
  | 'muteToggle';
export interface ObsAction {
  type: 'obs';
  /** Which program: OBS Studio (default) or Streamlabs Desktop. */
  app?: 'obs' | 'slobs';
  command: ObsCommand;
  scene?: string;
  input?: string;
}
/** An HTTP request. Text fields may contain {ora} {data} {appunti} {contatore:N} {timer:N} {segreto:N}. */
export interface HttpRequestSpec {
  method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE' | 'HEAD';
  url: string;
  headers?: { name: string; value: string }[];
  bodyType?: 'none' | 'json' | 'text' | 'form';
  body?: string;
  timeoutSec?: number;
}
export interface HttpAction extends HttpRequestSpec {
  type: 'http';
}
/** HTTP dynamic keys (source "http:KEYID"): polled every intervalSec, value picked by a JSON path. */
export interface LiveHttpConfig extends HttpRequestSpec {
  intervalSec: number;
  /** e.g. current.temperature, items[0].name; empty = whole response. */
  path?: string;
}
export type TwitchCommand =
  | 'marker'
  | 'clip'
  | 'channel'
  | 'commercial'
  | 'chat'
  | 'announce'
  | 'shoutout'
  | 'raid'
  | 'raidCancel'
  | 'chatMode'
  | 'poll'
  | 'pollEnd'
  | 'prediction'
  | 'predictionLock'
  | 'predictionResolve'
  | 'predictionCancel';
export interface TwitchAction {
  type: 'twitch';
  command: TwitchCommand;
  description?: string;
  /** channel: title / category (gameId + gameName for the editor) / tags. */
  title?: string;
  gameId?: string;
  gameName?: string;
  tags?: string[];
  length?: number;
  message?: string;
  color?: 'primary' | 'blue' | 'green' | 'orange' | 'purple';
  /** shoutout / raid: channel name. */
  channel?: string;
  mode?: 'slow' | 'follower' | 'sub' | 'emote';
  set?: 'toggle' | 'on' | 'off';
  seconds?: number;
  minutes?: number;
  choices?: string[];
  durationSec?: number;
  archive?: boolean;
  outcomes?: string[];
  windowSec?: number;
  /** predictionResolve: winning outcome, 1-based. */
  outcome?: number;
}
export type StreamEventKind =
  'follow' | 'sub' | 'gift' | 'resub' | 'raid' | 'cheer' | 'redeem' | 'online' | 'offline' | 'donation' | 'superchat';
/** Streamlabs account: custom alert on the alert box, reset of the session donation total. */
export interface StreamlabsAction {
  type: 'streamlabs';
  command: 'alert' | 'resetTotal';
  alertType?: 'follow' | 'subscription' | 'donation' | 'host';
  message?: string;
  userMessage?: string;
  imageHref?: string;
  soundHref?: string;
  color?: string;
  durationMs?: number;
}
/** When an event happens: flash a key and/or run an action. */
export interface EventRule {
  id: string;
  event: StreamEventKind;
  /** redeem: reward title contains; cheer/raid/gift/resub: minimum amount. */
  filter?: string;
  flashKeyId?: string;
  flashColor?: string;
  flashSec?: number;
  action?: Action | null;
}
export type LrParam =
  | 'Exposure'
  | 'Contrast'
  | 'Highlights'
  | 'Shadows'
  | 'Whites'
  | 'Blacks'
  | 'Temperature'
  | 'Tint'
  | 'Vibrance'
  | 'Saturation'
  | 'Texture'
  | 'Clarity'
  | 'Dehaze';
export type LightroomCommand =
  | 'inc'
  | 'dec'
  | 'reset'
  | 'set'
  | 'resetAll'
  | 'autoTone'
  | 'copy'
  | 'paste'
  | 'sync'
  | 'preset'
  | 'beforeAfter'
  | 'undo'
  | 'redo'
  | 'rating'
  | 'flag'
  | 'label'
  | 'next'
  | 'prev'
  | 'module';
/** Lightroom Classic through the PadNook plugin. */
export interface LightroomAction {
  type: 'lightroom';
  command: LightroomCommand;
  param?: LrParam;
  step?: number;
  value?: number;
  preset?: string;
  rating?: number;
  flag?: 'pick' | 'reject' | 'none';
  label?: 'red' | 'yellow' | 'green' | 'blue' | 'purple' | 'none';
  module?: 'library' | 'develop' | 'map' | 'book' | 'slideshow' | 'print' | 'web';
}
export interface PluginAction {
  type: 'plugin';
  plugin: string;
  action: string;
  fields: Record<string, string | number | boolean>;
}
export interface NavigateAction {
  type: 'navigate';
  target: 'page' | 'profile' | 'back' | 'home';
  id?: string;
}

export interface TextAction {
  type: 'text';
  text: string;
}
export interface CommandAction {
  type: 'command';
  shell: 'cmd' | 'powershell';
  command: string;
  hidden: boolean;
  cwd?: string;
}
export interface MacroStep {
  action: Action | null;
  delayMs: number;
}
export interface MouseAction {
  type: 'mouse';
  op: 'click' | 'down' | 'up' | 'move' | 'moveBy' | 'scroll';
  button?: 'left' | 'right' | 'middle';
  /** click: 1 = single, 2 = double. */
  count?: number;
  /** Screen point in physical pixels; optional for click/down/up/scroll (= where the cursor is). */
  x?: number;
  y?: number;
  /** moveBy: offset in pixels. */
  dx?: number;
  dy?: number;
  /** scroll: notches, positive = up / right. */
  amount?: number;
  horizontal?: boolean;
}
export type DiscordCommand =
  'toggleMute' | 'mute' | 'unmute' | 'toggleDeaf' | 'deaf' | 'undeaf' | 'togglePtt' | 'inputVolume' | 'outputVolume' | 'join' | 'leave';
export interface DiscordAction {
  type: 'discord';
  command: DiscordCommand;
  /** inputVolume 0..100, outputVolume 0..200 */
  level?: number;
  /** join: voice channel id (Developer Mode → Copy Channel ID). */
  channelId?: string;
}
export interface DiscordInfo {
  enabled: boolean;
  clientId: string;
  hasSecret: boolean;
  hasToken: boolean;
  connected: boolean;
  authorized: boolean;
  loginPending: boolean;
  user?: string | null;
  error?: string | null;
  redirectUri: string;
}
export interface MacroAction {
  type: 'macro';
  steps: MacroStep[];
}

export type Action =
  | HotkeyAction
  | LaunchAction
  | MediaAction
  | NavigateAction
  | TextAction
  | CommandAction
  | MacroAction
  | MouseAction
  | DiscordAction
  | SoundAction
  | SystemAction
  | WindowAction
  | CounterAction
  | TimerAction
  | ObsAction
  | HttpAction
  | TwitchAction
  | StreamlabsAction
  | LightroomAction
  | PluginAction;

/** Value of a live source, as sent by the server. */
export interface LiveValue {
  text?: string;
  value?: number;
  state?: boolean;
  sub?: string;
  icon?: string;
  /** startedAtMs: absolute start (Twitch uptime), used instead of elapsedMs + receive time. */
  timer?: { mode: 'countdown' | 'stopwatch'; durationMs: number; elapsedMs: number; running: boolean; startedAtMs?: number };
  [k: string]: unknown;
}

/** Style applied while a rule matches. Text may contain {text} {value} {sub}. */
export interface LiveStyle {
  labelText?: string;
  labelColor?: string;
  icon?: IconStyle;
  bgColor?: string;
}

export interface LiveRule {
  /** status: HTTP status code matches value (200, 2xx, 4xx…; 0 = no response). */
  when: 'true' | 'false' | 'gt' | 'lt' | 'eq' | 'contains' | 'status';
  value?: string;
  style: LiveStyle;
}

/** Dynamic keys: the label is a template filled from a live source; rules change the look. */
export interface LiveConfig {
  /** clock, cpu, ram, vol, mic, audio.out, app.fg, appvol:NAME, weather:LAT,LON, counter:NAME, timer:NAME, obs.*, plugin:ID/SOURCE */
  source: string;
  /** clock: time | timeSec | date | dateTime | weekday | dayMonth */
  format?: string;
  /** weather: place name shown in the editor. */
  place?: string;
  /** Use the icon sent by the source (weather, plugins) instead of the key icon. */
  sourceIcon?: boolean;
  /** source http:KEYID */
  http?: LiveHttpConfig;
  rules: LiveRule[];
}

/** Toggle keys: the base style is the OFF look; onStyle overrides it while ON. */
export interface ToggleConfig {
  onAction: Action | null;
  offAction: Action | null;
  /** true = turning OFF runs onAction too (offAction is ignored). */
  sameAction: boolean;
  onStyle: { labelText?: string; icon?: IconStyle; bgColor?: string };
}

/** Slider keys: drag to set a level on the PC. Vertical when the key is taller than wide. */
export interface SliderConfig {
  target: 'vol' | 'mic' | 'app' | 'brightness' | 'lightroom';
  /** target 'lightroom': Develop parameter (a tap resets it). */
  param?: LrParam;
  /** target 'app': process name, e.g. "spotify". */
  app?: string;
  /** target 'vol' / 'mic': device id; empty = the Windows default device. */
  device?: string;
  /** Device name, shown in the editor when the device is not connected. */
  deviceName?: string;
  /** auto (default) = vertical when the key is taller than wide. */
  orientation?: 'auto' | 'vertical' | 'horizontal';
  /** Audio targets: a tap (without dragging) toggles mute. Default true. */
  tapMute?: boolean;
  /** Fill color of the bar. */
  color?: string;
}

export interface Key {
  id: string;
  span: { w: number; h: number };
  pos: { portrait?: Cell; landscape?: Cell };
  style: KeyStyle;
  kind: 'action' | 'toggle' | 'folder' | 'dynamic' | 'slider';
  onPress: Action | null;
  onLongPress: Action | null;
  longPressMs?: number;
  /** Action for a quick double tap; when set, a single tap waits doubleTapMs before running onPress. */
  onDoubleTap?: Action | null;
  doubleTapMs?: number;
  folderPageId?: string;
  toggle?: ToggleConfig;
  live?: LiveConfig;
  slider?: SliderConfig;
}

export interface Page {
  id: string;
  name: string;
  /** soundpad = a page with only sound keys. */
  kind: 'page' | 'folder' | 'soundpad';
  layout?: Partial<Layout>;
  background?: Background | null;
  keys: Key[];
}

export interface Profile {
  id: string;
  name: string;
  /** Automatic switch: this profile is shown while one of these apps is in front on the PC. */
  auto?: { apps: string[] };
  layout: Layout;
  background: Background;
  pages: Page[];
}

export interface DeviceInfo {
  id: string;
  name: string;
  defaultProfileId: string | null;
  pairedAt: string;
  lastSeen: string;
  online: boolean;
}

export interface PluginField {
  id: string;
  label?: string;
  type?: 'text' | 'textarea' | 'number' | 'checkbox' | 'select' | 'file';
  default?: string | number | boolean;
  options?: [string, string][];
  min?: number;
  max?: number;
  placeholder?: string;
}

export interface PluginInfo {
  id: string;
  name: string;
  version?: string;
  author?: string;
  description?: string;
  actions: { id: string; name: string; description?: string; fields: PluginField[] }[];
  sources: { id: string; name: string; description?: string }[];
}

/** Lists from the PC for the editor (edit.info). */
export interface PcInfo {
  outputs: { id: string; name: string; isDefault: boolean }[];
  inputs?: { id: string; name: string; isDefault: boolean }[];
  sessions: string[];
  /** Apps with an audio session, with volume (0..100) and icon (data URL). */
  audioApps?: { name: string; volume: number | null; muted: boolean; icon: string | null }[];
  apps: string[];
  screenshotFolder: string;
  obs: {
    enabled: boolean;
    port: number;
    hasPassword: boolean;
    connected: boolean;
    error?: string | null;
    scenes?: string[];
    inputs?: string[];
  } | null;
  twitch?: TwitchInfo | null;
  slobs?: {
    enabled: boolean;
    mode: 'pipe' | 'ws';
    port: number;
    hasToken: boolean;
    connected: boolean;
    error?: string | null;
    scenes?: string[];
    inputs?: string[];
  } | null;
  streamlabs?: StreamlabsInfo | null;
  lightroom?: { connected: boolean; error?: string | null; installed: boolean; folder: string } | null;
  discord?: DiscordInfo | null;
  /** Names of the credentials stored (encrypted) on the PC; the values never leave it. */
  secrets?: string[];
}

export interface TwitchInfo {
  clientId: string;
  hasDefaultClientId: boolean;
  connected: boolean;
  login?: string | null;
  name?: string | null;
  error?: string | null;
  pending?: { userCode: string; uri: string; expiresAt: number } | null;
  missingScopes: string[];
  eventsub: { connected: boolean; error?: string | null; failed: string[] };
  rules: EventRule[];
}

export interface StreamlabsInfo {
  enabled: boolean;
  hasSocketToken: boolean;
  hasAccessToken: boolean;
  connected: boolean;
  error?: string | null;
  rules: EventRule[];
  /** Result of edit.streamlabs.test. */
  test?: string;
}
