--[[
PadNook for Lightroom Classic.
Lets PadNook (phone/tablet control deck on this PC) adjust Develop sliders, rate and flag photos.
The plugin listens on 127.0.0.1 only: port 58873 for commands, 58874 for the state sent back.
]]

return {
  LrSdkVersion = 13.0,
  LrSdkMinimumVersion = 10.0,
  LrToolkitIdentifier = 'it.padnook.lightroom',
  LrPluginName = 'PadNook',
  LrPluginInfoUrl = 'https://github.com/AgaresFX/PadNook',
  LrInitPlugin = 'Init.lua',
  LrForceInitPlugin = true,
  LrShutdownPlugin = 'Shutdown.lua',
  LrPluginInfoProvider = 'InfoProvider.lua',
  LrExportMenuItems = {
    { title = 'PadNook: stato e collegamento', file = 'Status.lua' },
    { title = 'PadNook: esporta impostazioni foto', file = 'Dump.lua' },
  },
  LrLibraryMenuItems = {
    { title = 'PadNook: stato e collegamento', file = 'Status.lua' },
    { title = 'PadNook: esporta impostazioni foto', file = 'Dump.lua' },
  },
  VERSION = { major = 1, minor = 12, revision = 2, build = 0 },
}
