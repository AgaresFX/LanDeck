-- Runs when Lightroom starts (LrForceInitPlugin) or when the plugin is reloaded: opens the PadNook sockets.
local ok, e = pcall(function()
  require('Server').start('init')
end)
if not ok then
  -- Last resort: say why in the plugin folder.
  pcall(function()
    local LrPathUtils = import 'LrPathUtils'
    local f = io.open(LrPathUtils.child(_PLUGIN.path, 'PadNook.log'), 'a')
    if f then
      f:write('Init failed: ' .. tostring(e) .. '\n')
      f:close()
    end
  end)
end
