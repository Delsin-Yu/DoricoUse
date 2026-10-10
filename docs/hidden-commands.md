# Commands that exist in the application but are not in the published catalogue

Offline, read-only analysis of `C:\Program Files\Steinberg\Dorico6\Dorico6.exe`
(154,771,992 bytes, Dorico Pro 6.2.30.1012, build date 2025-05-29) against the **live**
catalogue the API published on this machine (`getcommands`, 1090 names, snapshot in
`.spinney/vocabulary/catalogue.json` and `command-names.txt`).

Nobody sent anything to Dorico to produce this: the application was never started, no
command was tried, and no candidate below has been executed.

## 1. Answer in one line

The 1090 names the Remote Control API publishes are a **subset**: the executable also
contains at least **162 further command names** — among them `File.New` — in the same
literal pool, in the same family blocks, interleaved with the published ones. So
`File.New` is **absent from the API's published list, not absent from the application**:
the app has the command, gives it the default key binding Ctrl+N, and simply never
publishes it. Whether the API would *run* an unpublished name is **not established here**
and cannot be established offline — it is untested, and the honest statement is
"not published", not "callable".

The README's claim ("no way to create a project — `File.New` is not even in the
catalogue") is therefore true of the *catalogue* and false of the *application*.

## 2. Method (and how substring false positives were avoided)

Three independent extractions; every one works on exact, NUL-delimited whole tokens.

### 2.1 Extract the application's command-name literals

The exe stores names as plain NUL-terminated ASCII in `.rdata`. A first pass took every
NUL-delimited dotted token in the whole file:

```bash
# A: exact whole tokens (the one used for the finding)
tr '\0' '\n' < Dorico6.exe | sort -u > tokens.txt      # 10253 dotted tokens, 16918 token-like
# B: same thing from Python, with byte offsets, non-overlapping-safe
#    (?<=\x00)([A-Za-z_]\w*(?:\.[A-Za-z_0-9]\w*)+)(?=\x00)
grep -a -o -P '\x00File\.New\x00' Dorico6.exe          # -> 1 hit, offset 129685976
```

10253 distinct dotted tokens; 1283 of them have command shape
(`^[A-Z]\w*(\.[A-Z]\w*)+$` plus the two catalogue oddities, see §2.3).

### 2.2 The false-positive trap, measured

`File.New` is a **prefix** of `File.NewFromTemplate`, so a plain substring grep
over-reports:

```bash
$ grep -a -o -F "File.New" Dorico6.exe | sort | uniq -c
      2 File.New                # <-- one is File.New, one is the prefix of File.NewFromTemplate
$ tr '\0' '\n' < Dorico6.exe | grep -x -c -F "File.New"
1                              # <-- exact whole token: the real number
```

Every name in the lists below was matched as a **complete NUL-delimited token**, never as
a substring, so `File.NewFromTemplate` can neither hide `File.New` nor invent it.

### 2.3 Diff, then classify

The pool of command-name literals sits at byte offsets **129,656,700 – 129,700,000** of the exe (the region begins
right after the `ScoreDocument notifyEditOperationFinished` strings and ends in pointer tables). It holds
**1191 unique names**: **1029 of the 1090 published names** are in it, plus **162 names
that the catalogue does not have**. The block is laid out family by family, in the same
alphabetical order the family is published in — e.g. around `File.New`:

```
129685904 Page.ExportGraphicSlices          <- published
129685936 Page.SetGraphicSliceDataInternal  <- NOT published
129685976 File.New                          <- NOT published   <-- Ctrl+N
129685992 File.NewFromTemplate              <- NOT published
129686016 File.Open                         <- published
129686032 File.Save                         <- published
129686048 File.AutoSave                     <- NOT published
```

Excluded, with the reason, so the numbers mean something:

| Excluded | Count | Why |
|---|---|---|
| `<published command>.<its own parameter>` | 10 | UI action strings, e.g. `Play.SetPlayheadTo.PlayFromLocation` — the stem `Play.SetPlayheadTo` is published and `PlayFromLocation` is one of its declared parameters (`window/noteInput/play action strings` pool at 129,771,0xx, a different region). Checked mechanically against `requiredParameters`/`optionalParameters`. |
| `Bar.Beat.Tick`, `Bar.Beat.Ticks`, `Dorico.AU`, `Dorico.ScoreView`, `Internal.Command`, `ScoreInfo.Get*` | 13 | not commands: transport display format strings, Qt/AU module names, a short-name-pool entry, and Lua `ScoreInfo.*` accessors |
| `B.U`, `D.K`, `N.F`, `X.Y`, `J.X`, `P.N`, `V.N` … | 12 | two-segment layout symbols from `.pdata`/mangling, not names |
| `Debug.kDebugRemoveDynamicAboveBelowStaveProperties` | — | **kept**: it is in the command pool (129672592) and is exempt from the strict shape by its lowercase `k` prefix (the catalogue has the same exemption: `Edit.kForceCurrentAppearanceOfFiguredBass`) |

The 162 pool members that remain are the finding. One of them,
`Debug.SlurRouter.Options`, is ambiguous and is listed as such (§4).

### 2.4 Independent corroboration, from a file the app ships

`C:\Program Files\Steinberg\Dorico6\keycommands_en.json` (default key commands, 324 bound
names in the 9 language files) binds four of the 162:

```
line  41:  "File.New": [ "Ctrl+N" ]
line 942:  "Play.NavigateForwards": [ "Ctrl+]" ]
line 945:  "Play.NavigateBackwards": [ "Ctrl+[" ]
line 1041: "View.HideInvisibles": [ "`" ]      (and again as "\\" )
```

That is an app-authored file, so for these four the "it is a command" claim no longer
rests on the binary at all.

## 3. The list (162 names, grouped)

Families marked **new** have no published member at all.

**`Debug.*` (48) — new family, all unpublished**
`Debug.DumpScoreAsHTML`, `Debug.DumpScoreLibraryAsHTML`,
`Debug.DumpScoreLibraryUserEntitiesAsHTML`, `Debug.DebugDumpCondensedVoiceNotationGraphsAsHTML`,
`Debug.DumpRenderChainsAsHTML`, `Debug.DumpPlaybackGraphAsHTML`, `Debug.DumpSequenceDiagram`,
`Debug.DumpWidgetHierarchy`, `Debug.OpenActionLog`, `Debug.ResetInstrumentation`,
`Debug.RemoveExplicitRests`, `Debug.RemoveExplicitBeaming`, `Debug.RemoveExplicitAccidentals`,
`Debug.RemoveForcedDuration`, `Debug.RemoveArticulationsProperties`, `Debug.RemoveTupletsProperties`,
`Debug.RemoveTieProperties`, `Debug.RemoveGlissandoLineProperties`,
`Debug.kDebugRemoveDynamicAboveBelowStaveProperties`, `Debug.RemoveStemDirectionProperties`,
`Debug.RemoveVoiceStartAndStopProperties`, `Debug.RemoveRestPositions`,
`Debug.ClearPersistedWindowPositions`, `Debug.ClearInstalledContentCheck`, `Debug.ShowOptions`,
`Debug.ShowProperties`, `Debug.DefaultLibraryMaintenance`, `Debug.Trim`, `Debug.BasicLayoutOptions`,
`Debug.BasicNotationOptions`, `Debug.BasicEngravingOptions`, `Debug.BasicPlaybackOptions`,
`Debug.BasicNoteInputOptions`, `Debug.CompositeExplorer`, `Debug.ReinitializeFontServices`,
`Debug.ShowKeyboardWindow`, `Debug.SVGScreenShot`, `Debug.RemoveAllBreaks`,
`Debug.ClearSuppressedAssertions`, `Debug.ExportWebViewerMetaData`, `Debug.ExportWebOMRMetaData`,
`Debug.RemoveNoteheadOverrides`, `Debug.QMLScoreSnippetDrawerTest`, `Debug.QMLScoreViewTest`,
`Debug.ClearSavedWindowGeometry`, `Debug.ClearJumpBarHistory`, `Debug.ShowDiagnosticCrasher`,
`Debug.DumpHALionPresets` — plus the ambiguous `Debug.SlurRouter.Options` (§4).

**`Custom.*` (7) — new family**
`Custom.CreateStartOfUserOperationMarker`, `Custom.CreateEndOfUserOperationMarkerAndEnsureRecording`,
`Custom.StartRecordingUserOperationHistory`, `Custom.StopRecordingUserOperationHistory`,
`Custom.StartMultiCommandUndoStep`, `Custom.StopMultiCommandUndoStep`,
`Custom.NavigateToSuggestedCueLocation`.

**`Play.*` (27)**
`Play.SetProjectAudioEngineState`, `Play.SetTool`-sibling names by pool position —
`Play.NavigateForwards`, `Play.NavigateBackwards`, `Play.NavigateToStart`, `Play.PianoRollShiftEvent`,
`Play.PianoRollShiftPitch`, `Play.PianoRollEditPitchAndPosition`, `Play.PianoRollDrawNote`,
`Play.PianoRollNoteVelocityChange`, `Play.AutomationPointChange`, `Play.AutomationPointCreate`,
`Play.AutomationPointDelete`, `Play.AutomationPointEditEnvelope`, `Play.AutomationPointHide`,
`Play.AutomationEdit`, `Play.AutomationSetValues`, `Play.SyncAutomation`, `Play.SetPlayheadToPoint`,
`Play.SetPluginShownInUIState`, `Play.PostLoadFixup`, `Play.ApplyLiveSpace`, `Play.NewStageTemplate`,
`Play.NewSpaceTemplate`, `Play.StartScrubPlayback`, `Play.StartScrubSoloPlayback`, `Play.StopScrubPlayback`,
`Play.ChangeMixerControls`.
(Not hidden, so not listed: `Play.SetTool`, `Play.PianoRollEventsSelected`, `Play.PianoRollEventsDeleted` and
`Play.PianoRollNoteOffsetChange` are all published, and `Play.SetTool.ToolName`,
`Play.StartOrStop.PlayFromLocation`, `Play.SetPlayheadTo.PlayFromLocation` are the action-string class of §2.3.)

**`Edit.*` (11)**
`Edit.PasteSpecial`, `Edit.PasteCubaseChordsAtPosition`, `Edit.SelectComment`,
`Edit.RemoveFromSharedElement`, `Edit.ScalePlayerTo60Percent`, `Edit.ScalePlayerTo75Percent`,
`Edit.ScalePlayerTo100Percent`, `Edit.ChangeVoiceSlashType`, `Edit.MakeSingleNoteTremolo`,
`Edit.ApplyPercussionKitEditorChanges`,
`Edit.ApplyPercussionPlayingTechniquePresentationEditorChanges`.

**`EventEdit.*` (10)**
`EventEdit.Trim`, `EventEdit.Split`, `EventEdit.Glue`, `EventEdit.AddAndRemovePropertiesAtLocation`,
`EventEdit.NavigateStaveUp`, `EventEdit.NavigateStaveDown`, `EventEdit.SetCapoChordDiagram`,
`EventEdit.SetSpecificChordDiagramForTuning`, `EventEdit.ApplyInstrumentStringsAndTuningChange`,
`EventEdit.EditExistingChordSymbolVisibilityEvent`.

**`NoteInput.*` (10)**
`NoteInput.FretDecaIndex`, `NoteInput.MoveToString`, `NoteInput.OttavaLineStop`, `NoteInput.Glue`,
`NoteInput.DrumPadHits`, `NoteInput.DrumPadHitsAudition`, `NoteInput.FretboardStringPlucked`,
`NoteInput.UpdateMIDIPitchesInMIDITriggerPopover`, `NoteInput.CreateComment`,
`NoteInput.CreateMIDITriggerRegion`.

**`File.*` (8)**
`File.New`, **`File.NewFromTemplate`**, `File.AutoSave`, `File.Print`, `File.SaveAsTemplate`,
`File.WasModifiedOnDisk`, `File.WasRenamedOnDisk`, `File.WasDeletedOnDisk`
(the last three read like file-watch notifications rather than user commands — a name alone
does not settle what they do).

**`View.*` (9)** `View.Expand`, `View.Collapse`, `View.ExpandAll`, `View.CollapseAll`,
`View.HideInvisibles`, `View.ShowInvisibles`, `View.MarqueeZoom`, `View.ResetOptionsToFactory`,
`View.ResetOptionsToSavedDefaults`.

**`Project.*` (7)** `Project.AddCustomEnsemble`, `Project.Instrument.UpdateNames`,
`Project.SetScoreInfoInternal`, `Project.AddEntitiesToScoreLibrary`,
`Project.AddChordDiagramToScoreLibrary`, `Project.SuppressProofingResult`,
`Project.Layouts.ClefAndTranspositionOverridesSetOverrides`.

**`Application.*` (6)** `Application.About`, `Application.Nudge`, `Application.CheckForUpdates`,
`Application.OpenUrlInDefaultBrowser`, `Application.ShowAllowRemoteClientDialog`,
`Application.UploadCrashes`.

**Smaller groups** — `Print.*` (4): `Print.SetHideViewOptionsInternal`,
`Print.AddPrintLayoutRendererInternal`, `Print.RemovePrintLayoutRendererInternal`,
`Print.PrintWithCurrentSettingsInternal`; `UI.*` (3): `UI.AddToSelectionTool`,
`UI.ExtendSelectionTool`, `UI.TriggerLongPressAnimation`; `TextEditor.*` (2):
`TextEditor.InsertToken`, `TextEditor.InsertMusicText`; `NoteEdit.*` (2): `NoteEdit.PitchDrag`,
`NoteEdit.RespellToggle`; `Window.*` (2): `Window.CreateView`, `Window.TabsReordered`;
`Help.*` (2): `Help.ConnectionsEditor`, `Help.WhatsNewGuidedTour`; single names:
`Engrave.EditStaffSpacingMeasurement`, `Filter.Voice`, `Page.SetGraphicSliceDataInternal`.

The published families of the pool, for scale: `Edit` 228, `NoteInput` 129, `Project` 114,
`Play` 107, `Filter` 95, `EventEdit` 79, `Page` 70, `View` 56, `Window` 43, `Engrave` 38,
`File` 20, `NoteEdit` 17, `UI` 13, `Print` 13, `MusicalManipulation` 12, `TextEditor` 11,
`Setup` 10, `Application` 8, `Script` 6, `Help` 5, `Video` 5, `OptionsDialog` 5,
`ScrubPlayback` 2, `JumpBar` 2, `SetChordDiagramTuning` 1, `MouseEdit` 1.

## 4. What is *not* claimed, and the ambiguity

- **Not claimed: that the API will execute any of these.** The catalogue is the API's own
  list of accepted names; a name outside it is, by that list, not offered. The remote
  handler's filter lives in code, not in a blacklist string table — the whole
  protocol-string region (130,327,0xx: `commandlist`, `getcommands`, `getstatus`, …) holds
  no excluded-name table to read, so the rule that drops these 162 was not recovered.
  Testing one by sending it would answer this, and was deliberately not done.
- **Not claimed: that each string is a command.** Membership of the name pool is strong (it
  is the same table, same family blocks, 1029/1090 published names in it), but it is
  *evidence about the application's command table*, not a behavioural test.
- **Ambiguous names, stated as such:** `Debug.SlurRouter.Options` could be the command
  `Debug.SlurRouter` with parameter `Options`, or a command `Debug.SlurRouter.Options`
  (stem `Debug.SlurRouter` is not published, so the parameter rule of §2.3 cannot decide);
  `Project.Layouts.ClefAndTranspositionOverridesSetOverrides` reads like the published
  `Project.Layouts.ClefAndTranspositionOverrides` plus a `SetOverrides` selector;
  `Project.Instrument.UpdateNames` likewise sits next to `Project.Instrument.InitiateEditNames`.
- **Boundary of the pool is empirical.** 60 published names live in *other* literal pools
  (e.g. `NoteInput.Enter` only at 102,490,160, next to `kNoteInputStartNoteInput`), so
  "not in the pool" is not "not a command" — this method under-reports hidden names exactly
  as much as it might over-report. `Script.*` (6), `Setup.*` (10), `MusicalManipulation.*`
  (12), `JumpBar.*` (2) are wholly inside the pool and show no hidden siblings, which is
  itself a useful negative.

## 5. Reproduce

```bash
EXE="/c/Program Files/Steinberg/Dorico6/Dorico6.exe"
tr '\0' '\n' < "$EXE" | sort -u > /tmp/tokens.txt                  # every NUL-delimited token
grep -x -F "File.New" /tmp/tokens.txt                               # 1
grep -a -o -F "File.New" "$EXE" | wc -l                             # 2  (the substring trap)
grep -a -o -b -P '\x00File\.New\x00' "$EXE"                         # 129685976:File.New
python .spinney/hidden.py                                           # diff vs the 1090 catalogue
```

`/tmp/tokens.txt` holds the published list too: `comm -13 <(sort command-names.txt) tokens.txt`
yields the same 162 once the false-positive classes of §2.3 are removed.
