-- Lightroom is closing or the plugin is being reloaded: stop the loop and the sockets.
local ok, state = pcall(function() return rawget(_G, 'PADNOOK') end)
if ok and state then
  state.running = false
end
