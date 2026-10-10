-- Can the C++ argument types of the scripting write methods be built from Lua?
--
-- Run it with Dorico open, from a shell:
--   dorico-use command send --name Script.RunScript \
--     --param "ScriptPath=<repo>/tools/probe-scripting-writes.lua" --param ShowConsole=false
--   (adjust the path to wherever this file is), then read .spinney/scripting/probe4.out.
--
-- Why it is written this way: a RunScript without a console has no output channel, so a probe that dies
-- half-way leaves no trace at all - that happened once while writing this. The findings file is therefore
-- opened first and written as the probe goes, and the whole body runs inside pcall so a failure is recorded
-- rather than swallowed.
--
-- Introspection only: nothing here writes to the score. The write calls are made with the WRONG number of
-- arguments on purpose, which luabind rejects before it invokes anything, and its error text is the C++
-- signature of the method - that is how createNote's arguments became known.

local path = '<repo>/.spinney/scripting/probe4.out'
local file = io.open(path, 'w')
local function w(...)
  local parts = {}
  for index = 1, select('#', ...) do parts[#parts + 1] = tostring((select(index, ...))) end
  file:write(table.concat(parts, ' ') .. '\n')
  file:flush()
end

local function attempt(label, body)
  local ok, result = pcall(body)
  w(label, '->', tostring(ok), tostring(result))
  return result
end

w('=== probe4: can the write arguments be constructed? ===')

local ok = pcall(function()
  local app = DoApp.DoApp()
  w('versionString', app.versionString)
  local score = app.currentScore
  w('score', tostring(score))
  local flow = score.flows[1]
  local stream = flow.voiceStreams[1]
  w('stream', tostring(stream))

  -- 1. Which of the registered helper classes are reachable as class-reps, and what does each constructor
  --    want? A class-rep answers a wrong call with its signature.
  local helpers = {
    'MIDIPitchWithSpelling', 'MIDIPitch', 'NotatedLength', 'LogDuration', 'RhythmicLength',
    'TupletDefinition', 'CaretLocation', 'Interval', 'AccidentalType', 'NoteName', 'DynamicIntensity', 'Variant',
  }

  w('--- constructors on the DoCore class-reps ---')
  for _, name in ipairs(helpers) do
    local klass = attempt('DoCore.' .. name, function() return DoCore[name] end)
    if klass then
      attempt('   ' .. name .. '()', function() return klass() end)
      attempt('   ' .. name .. '(4)', function() return klass(4) end)
      attempt('   ' .. name .. '(4, 4)', function() return klass(4, 4) end)
      attempt('   ' .. name .. '("C", 4)', function() return klass('C', 4) end)
    end
  end

  w('--- the length helper, whose signature was seen earlier ---')
  attempt('LengthUtils.getRhythmicDurationForRational()', function() return LengthUtils.getRhythmicDurationForRational() end)
  attempt('DoCore.LengthUtils.getRhythmicDurationForRational()', function() return DoCore.LengthUtils.getRhythmicDurationForRational() end)

  -- 2. If a pitch and a length can be built, an actual write can be attempted next run - with the arguments
  --    printed here. This probe stops short of writing on purpose.
  w('--- what a write would need, restated ---')
  attempt('stream.createNote()', function() return stream.createNote() end)
end)

if not ok then
  w('the probe body failed; see the lines above')
end

w('=== end probe4 ===')
file:close()
