-- Fig for DaVinci Resolve: Workspace > Scripts > Fig brings a Fig project's timeline into the open Resolve project.
-- Fig wrote this file and points it at itself each time it starts. If Fig has moved, open it once (or use its
-- Help > Install DaVinci Resolve importer).
local FIG = "@FIG@"

local resolve = resolve or (Resolve and Resolve()) or bmd.scriptapp("Resolve")
local fu = fu or fusion or resolve:Fusion()
local windows = package.config:sub(1, 1) == "\\"
-- set when run outside the menu (tests): no dialogs, the project and timeline come from these
local quiet_project = os.getenv("FIG_RESOLVE_PROJECT")

-- Dialogs go through Resolve's UI manager: AskUser needs a Fusion composition, which the Edit page doesn't have.
local ui = fu and fu.UIManager
local dispatcher = ui and bmd.UIDispatcher(ui)

-- Shows `win` until one of its handlers calls dispatcher:ExitLoop().
local function run_window(win)
    win:Show()
    dispatcher:RunLoop()
    win:Hide()
end

local function tell(title, message)
    print("Fig: " .. message)
    if quiet_project or not dispatcher then return end
    local win = dispatcher:AddWindow({
        ID = "FigMessage", WindowTitle = title, Geometry = { 400, 300, 520, 260 },
        ui:VGroup({
            ui:TextEdit({ ID = "Text", Text = message, ReadOnly = true, Weight = 1 }),
            ui:HGroup({ Weight = 0, ui:HGap(0, 1), ui:Button({ ID = "OK", Text = "OK" }) }),
        }),
    })
    win.On.OK.Clicked = function() dispatcher:ExitLoop() end
    win.On.FigMessage.Close = function() dispatcher:ExitLoop() end
    run_window(win)
end

local function fail(message)
    tell("Fig", message)
    error(message, 0)
end

local function quote(s)
    if windows then return '"' .. s .. '"' end
    return "'" .. (s:gsub("'", "'\\''")) .. "'"
end

-- Runs Fig without a window. Resolve's own library path would load Resolve's copies of libraries Fig also uses.
local function run_fig(...)
    local parts = { quote(FIG) }
    for _, a in ipairs({ ... }) do parts[#parts + 1] = quote(a) end
    local cmd = table.concat(parts, " ")
    if windows then
        cmd = '"start "" /wait /b ' .. cmd .. '"'
    else
        cmd = "env -u LD_LIBRARY_PATH -u DYLD_LIBRARY_PATH -u PYTHONPATH -u PYTHONHOME " .. cmd
    end
    local ok = os.execute(cmd)
    return ok == true or ok == 0
end

local function temp_file(name)
    local dir = os.getenv("TMPDIR") or os.getenv("TEMP") or os.getenv("TMP") or "/tmp"
    return dir .. (windows and "\\" or "/") .. "fig-resolve-" .. name .. ".lua"
end

local function read_back(path)
    local chunk = loadfile(path)
    os.remove(path)
    return chunk and chunk()
end

-- Which project and which of its timelines (both from 1), or nil when cancelled.
local function pick_project(projects)
    local win = dispatcher:AddWindow({
        ID = "FigPick", WindowTitle = "Import from Fig", Geometry = { 400, 300, 440, 150 },
        ui:VGroup({
            ui:HGroup({ ui:Label({ Text = "Project", Weight = 0, MinimumSize = { 70, 0 } }), ui:ComboBox({ ID = "Project" }) }),
            ui:HGroup({ ui:Label({ Text = "Timeline", Weight = 0, MinimumSize = { 70, 0 } }), ui:ComboBox({ ID = "Timeline" }) }),
            ui:HGroup({
                ui:HGap(0, 1),
                ui:Button({ ID = "Cancel", Text = "Cancel" }),
                ui:Button({ ID = "Import", Text = "Import", Default = true }),
            }),
        }),
    })
    local items = win:GetItems()
    local function fill_timelines()
        local p = projects[items.Project.CurrentIndex + 1]
        items.Timeline:Clear()
        for _, name in ipairs(p and p.timelines or {}) do items.Timeline:AddItem(name) end
        items.Timeline.Enabled = p ~= nil and #p.timelines > 1
    end
    for _, p in ipairs(projects) do items.Project:AddItem(p.name) end
    fill_timelines()
    local project, timeline
    win.On.Project.CurrentIndexChanged = fill_timelines
    win.On.Import.Clicked = function()
        project, timeline = items.Project.CurrentIndex + 1, items.Timeline.CurrentIndex + 1
        dispatcher:ExitLoop()
    end
    win.On.Cancel.Clicked = function() dispatcher:ExitLoop() end
    win.On.FigPick.Close = function() dispatcher:ExitLoop() end
    run_window(win)
    if project then return projects[project], math.max(timeline, 1) end
end

-- which project, and which of its timelines
local list_path = temp_file("projects")
if not run_fig("--resolve-projects", list_path) then
    fail("Couldn't start Fig (" .. FIG .. "). Open Fig once so it can point this script at itself.")
end
local projects = read_back(list_path) or fail("Fig didn't list its projects.")
if #projects == 0 then fail("There are no Fig projects yet.") end

local pick, timeline
if quiet_project then
    for _, p in ipairs(projects) do
        if p.id == quiet_project then pick = p end
    end
    pick = pick or fail("No Fig project " .. quiet_project)
    timeline = tonumber(os.getenv("FIG_RESOLVE_TIMELINE") or "1")
else
    if not dispatcher then fail("Resolve's dialogs aren't available here.") end
    pick, timeline = pick_project(projects)
    if not pick then return end
end

local manifest_path = temp_file("export")
run_fig("--resolve-export", pick.id, tostring(timeline), manifest_path)
local m = read_back(manifest_path) or fail("Fig didn't write the timeline.")
if m.error then fail(m.error) end

local project = resolve:GetProjectManager():GetCurrentProject() or fail("Open a project in Resolve first.")
local pool = project:GetMediaPool()
-- a project with no timelines yet takes the reel's size and frame rate
if project:GetTimelineCount() == 0 then
    project:SetSetting("timelineResolutionWidth", tostring(m.width))
    project:SetSetting("timelineResolutionHeight", tostring(m.height))
    project:SetSetting("timelineFrameRate", m.fps)
end
-- the reel's media goes in a bin named after it, the same one each time
local root, folder = pool:GetRootFolder(), nil
for _, f in ipairs(root:GetSubFolderList() or {}) do
    if f:GetName() == m.name then folder = f end
end
folder = folder or pool:AddSubFolder(root, m.name)
if folder then pool:SetCurrentFolder(folder) end

-- Resolve won't import over a timeline of the same name: importing again makes "<name> 2"
local taken = {}
for i = 1, project:GetTimelineCount() do taken[project:GetTimelineByIndex(i):GetName()] = true end
local name, n = m.name .. " - " .. m.timeline, 1
while taken[name] do
    n = n + 1
    name = m.name .. " - " .. m.timeline .. " " .. n
end

local tl = pool:ImportTimelineFromFile(m.xml, { timelineName = name })
if not tl then fail("Resolve couldn't read the timeline Fig wrote at " .. m.xml) end
if tl:GetSetting("timelineResolutionWidth") ~= tostring(m.width) or tl:GetSetting("timelineResolutionHeight") ~= tostring(m.height) then
    tl:SetSetting("useCustomSettings", "1")
    tl:SetSetting("timelineResolutionWidth", tostring(m.width))
    tl:SetSetting("timelineResolutionHeight", tostring(m.height))
end
project:SetCurrentTimeline(tl)

local notes = m.notes
local rate = tonumber(tl:GetSetting("timelineFrameRate"))
if rate and math.abs(rate - tonumber(m.fps)) > 0.01 then
    table.insert(notes, 1, "This Resolve project runs at " .. rate .. " fps and the reel at " .. m.fps
        .. ": cuts land on the nearest frame. Import into a new project to keep the reel's rate.")
end
if #notes > 0 then
    tell("Imported from Fig", "\"" .. name .. "\" is in. What didn't come over:\n\n- " .. table.concat(notes, "\n- "))
else
    print("Fig: imported " .. name)
end
