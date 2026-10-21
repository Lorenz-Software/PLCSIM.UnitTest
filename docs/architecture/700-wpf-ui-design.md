# WPF UI Design

This document describes the WPF workbench's interaction model and rationale.
It focuses on user-facing workflow structure, main window
regions, menu and command placement, status presentation, and layout decisions.

It does not define final styling, colors, icons, XAML implementation details,
Siemens API behavior, or PLC communication contracts.

## Design Intent

The WPF application is the primary manual debugging and validation application.
It should make the workflow observable and controllable without exposing vendor
API details or forcing the user through low-level technical steps.

The UI should be a single-window workbench. The main workflow stays visible
while the user selects configuration, prepares the environment, starts/stops the
PLC, starts/aborts/resets UnitTest runs, reviews results, opens artifacts, and
cleans up resources.

## Main Window Regions

The workbench uses these stable regions:

| Region | Purpose |
| --- | --- |
| `MainMenu` | Standard application commands, configuration access, advanced/manual actions, log/artifact actions, and help/about entries. |
| `SessionSidebar` | VS-Code-like left workbench sidebar with step navigation and active-step inputs/actions. |
| `StatusBadgeBar` | Compact process communication area above the main content with runtime setup, PLC, communication, event-stream monitoring, run-control, and diagnostics status. |
| `WorkspaceArea` | Primary work surface, starting with run overview/result tree and selected detail content. |
| `OutputPanel` | Bottom workbench area for terminal-style output, diagnostics summary/details, and artifact/log access. |
| `SessionStatusBar` | Compact session identity line for profile, project, target context, and current transient progress. |

### Main Layout Sizing

The main window uses a compact developer-tool layout:

```text
MainMenu
------------------------------------------------
SessionSidebar | StatusBadgeBar
               | WorkspaceArea
               | OutputPanel
------------------------------------------------
SessionStatusBar
```

Resizable areas:

| Area | Sizing |
| --- | --- |
| `MainMenu` | Fixed height. Not user-resizable. |
| `SessionSidebar` | User-resizable width. |
| `StatusBadgeBar` | Content/fixed height. Not user-resizable. Must stay compact. |
| `WorkspaceArea` | Filling area. Takes the primary remaining space. |
| `OutputPanel` | User-resizable height. |
| `SessionStatusBar` | Fixed height. Not user-resizable. |

The outer workbench exposes two splitters: one vertical splitter between
`SessionSidebar` and the right workbench, and one horizontal splitter between
`WorkspaceArea` and `OutputPanel`. Run Result and Diagnostics each have their
own internal list/detail splitter as described below.

## Information Architecture

The WPF application should present one coherent workbench instead of separate
wizard pages. The user should always be able to see:

- what is selected,
- what is prepared,
- what is currently running,
- which actions are available,
- which diagnostics require attention,
- whether results or artifacts are available.

The information architecture is organized around workflow ownership:

| Information Group | Primary Region | Secondary Region | Notes |
| --- | --- | --- | --- |
| Configuration selection | Session sidebar or configuration flyout/dialog. | Status badge bar and status bar. | Selection is a guided setup flow. Once complete, selected values remain visible in status badges and the status bar. |
| Profile/environment readiness | Status badge bar. | Diagnostics/output area. | The user sees whether the selected access profile and environment are usable without seeing vendor API object details. |
| Project preparation | Step 3 `Next` progression and status badge bar. | Terminal-style output. | Preparation is an implicit composed progression step after target selection and profile configuration. Intermediate technical steps are shown as progress/output, not as mandatory separate commands. |
| Runtime/PLC state | Status badge bar and status bar. | Main content detail area. | PLC running/stopped/prepared/unknown state must remain quickly visible. |
| UnitTest communication and monitoring | Status badge bar. | Terminal-style output and diagnostics. | Communication connected/monitoring active state gates Start/Abort/Reset. |
| Run state and result summary | Main content area. | Status badge bar and terminal-style output. | The result tree is the primary inspection surface once run evidence exists. |
| Diagnostics | Diagnostics/output area. | Diagnostics status badge. | The UI shows actionable diagnostics in context and uses the log file for deeper technical detail. |
| Test result artifact view | Main content area. | Menu. | The generated test result artifact is inspectable inside the application as a secondary main-content tab. Opening the physical file is a secondary menu action. |
| Log access | Menu. | Diagnostics/output area. | Opening the application log is a secondary command. Diagnostics should be usable without opening the log file for normal cases. |

### Region Priority

The main content area should be optimized for the current user task:

1. Before a run: configuration summary, readiness, and preparation feedback.
2. During preparation/start/run: operation progress and run overview.
3. After run evidence exists: structured result tree and selected node detail.
4. During failure/recovery: diagnostics and recovery-relevant context without
   hiding the relevant step action bar.

The stepper area should remain visible across these states. It owns the compact
workflow context and should not scroll away with main content.

The diagnostics/output area is supporting information. It should be visible or
quickly expandable, but it should not dominate the first screen over workflow
state and result inspection.

## UI Standards

The WPF application should use a compact, standard desktop-tool presentation.
The workbench prefers native or near-native WPF control
behavior over custom-styled controls unless the default control behavior would
make the workflow harder to read.

These rules are visual and interaction standards for the WPF application. They
do not define shared-core workflow behavior.

### Typography

Use one UI font family consistently:

| Element | Font |
| --- | --- |
| Default UI text | `Segoe UI` |
| Fallback | System UI fallback selected by WPF/Windows |
| Terminal-style output | `Consolas` or another installed monospace font |
| XML/artifact preview | `Consolas` or another installed monospace font |

Initial text sizing:

| Element | Size Direction |
| --- | --- |
| Main UI labels, buttons, menus, tabs, tree rows, fields | Standard WPF desktop text size, approximately 12 px / 9 pt. |
| Section headings inside panels/dialogs | Slightly stronger than normal text, approximately 13-15 px. |
| Main window/application titles | Use standard window/menu presentation; no hero-sized type. |
| Terminal/artifact text | Compact monospace text, approximately standard UI text size. |

The UI should not use viewport-scaled font sizes. Text should remain readable
and stable across window sizes.

### Density And Spacing

The application is a developer workbench and should use compact spacing:

| Area | Spacing Direction |
| --- | --- |
| Main workbench outer margins | Minimal. The workbench should preserve space for result, diagnostics, and output content. |
| Session sidebar panels | Compact vertical rhythm with clear grouping. |
| Result tree and diagnostics list | Dense rows with stable height and minimal row padding. |
| Detail panes | Slightly more breathing room than lists, but still compact. |
| Dialog content | Standard Windows/WPF dialog spacing, not card-style page spacing. |

Avoid large decorative margins, card-heavy sections, and wide empty bands. If
content needs separation, use standard group labels, separators, tab structure,
or panel boundaries.

### Buttons And Action Bars

Buttons should follow standard WPF desktop sizing and placement:

| Control | Direction |
| --- | --- |
| Primary step action | Placed at the bottom of the active step panel, right-aligned within the panel action bar where possible. |
| Step 4 action groups | Grouped by function: `PLC Control`, `Test Run`, and `Session`. |
| Dialog buttons | Standard bottom button bar, right aligned. |
| View clear/open actions | Menu/context actions for secondary view maintenance, not large primary workflow buttons. |

Button labels should be short commands. Use specific command labels where they
improve clarity, for example `End Session`, `Start PLC`, `Clear Output`, and
`Clear Run Result`.

Button bars should use standard desktop spacing between buttons. They should
not use oversized button heights, pill shapes, or web-style call-to-action
layout.

### Standard Controls

Use familiar desktop controls for familiar jobs:

| Need | Preferred Control |
| --- | --- |
| Option set | Combo box or radio group depending on density and permanence. |
| Binary setting | Checkbox. |
| Numeric timeout/duration | Text field with validation or numeric up/down control if available. |
| File/folder path setting | Text field plus browse button. |
| Status summary | Compact badge/label with small status marker. |
| Tree result hierarchy | Tree/list control with sticky header behavior where implemented. |
| Diagnostics detail | List plus detail pane when the content warrants it. |

The UI should not invent custom controls for standard desktop interactions
unless implementation proves that the standard control cannot express the
workflow cleanly.

### Icon And Color Use

Icons and colors are supporting scan aids:

- icons should use a consistent WPF-compatible icon set or native system icon
  source selected during implementation,
- status color should appear as a small marker, not as large filled panels,
- text must remain the primary carrier of meaning,
- warning/error dialogs should use standard system-style icon placement,
- help icons should show secondary information only on hover/focus.

### Main Content Layout

The main content area should use a stable split:

| Area | Purpose |
| --- | --- |
| Primary pane | Run overview, structured result tree, or test result artifact view. |
| Detail pane | Selected configuration, status, diagnostic, result node, or artifact details. |

Before a result tree exists, the primary pane may show a run overview and
preparation checklist. After run evidence exists, the result tree becomes the
primary pane. The detail pane changes with selection.

The main content area should provide at least two result-oriented tabs once run
output exists:

| Main Content Tab | Purpose |
| --- | --- |
| Run Result | Structured internal result view over RunControl, suites, test cases, and assertion findings. This is the primary manual debugging view. |
| Test Result Artifact | Textual or formatted representation of the generated test result artifact, starting with JUnit XML. This allows inspecting the produced artifact without opening the file externally. |

The physical test result artifact file may still be opened through a secondary
menu command, but opening the file should not be required for normal result
inspection.

`Run Result` layout:

| Region | Purpose |
| --- | --- |
| Result tree | Hierarchical run overview over run control, suites, and test cases. |
| Detail pane | Selected node detail, timing/result data, finding details, and related evidence references where available. |

The `Run Result` tab should contain one internal vertical splitter between
`Result tree` and `Detail pane`. The result tree width is user-resizable; the
detail pane fills the remaining space. The design should not specify concrete
pixel or percentage widths.

The result tree is the primary manual debugging surface. It should use clear
state/result markers such as not evaluated, running, passed, failed, and error.
It should not expose raw event-stream entries as the normal inspection model.
Assertion results are findings, not a normal tree hierarchy level.
Because the result tree is a dense inspection surface, it should be designed as
a scalable tree/list view, not as a small illustrative tree. Real projects may
easily contain dozens of suites and many dozens or hundreds of test cases. The
view must therefore use compact row heights, restrained padding, stable columns,
and scrolling. It must not use oversized cards or spacious dashboard-style
spacing.

Tree hierarchy and collapse behavior:

| Node Type | Tree Behavior |
| --- | --- |
| Run root | Top-level node, expanded by default. |
| Suite | Collapsible/expandable through a disclosure control. |
| Test case | Leaf node in the normal tree. Failed/error test cases indicate result status only; findings are shown in the detail pane. |
| Assertion/finding | Not part of the normal tree hierarchy. Display in the selected test case detail pane. |

The detail pane should change with the selected tree node:

| Selection | Detail Content |
| --- | --- |
| Run root | Run summary, start/end timing, overall result, and artifact state. |
| Suite | Suite status, aggregate counts, contained test cases, and suite-level findings. |
| Test case | Test case status, result, timing, assertion findings, and failure/error text. |
| Assertion/finding | Not selectable as a normal tree node. Finding details are shown through the selected test case detail pane. |

The selected tree node must remain visibly marked in the result tree while its
details are shown in the detail pane. Selection state should not be communicated
only by the detail pane content. The overview result column should stay focused
on the result state (`passed`, `failed`, `error`, etc.); finding counts belong
in the detail pane.

`Test Result Artifact` layout:

| Region | Purpose |
| --- | --- |
| Artifact header | Artifact type, generation state, and compact convenience actions. The configured output path is an application setting and should not be permanently shown as header content. |
| Artifact preview | Text or formatted preview of the generated artifact, currently JUnit XML. |

The artifact tab is for inspection. Opening the physical file remains a
secondary View-menu action.
The artifact preview is also content-dense and should maximize readable artifact
content over decorative spacing.
The artifact header may include compact convenience actions such as `Open File`
when the artifact exists and `Open Folder` when an output folder is configured.
It should not expose
generation/export commands in the first design; artifact creation belongs to the
run/export workflow.

The `Test Result Artifact` tab should not introduce an internal splitter. Its
header uses fixed/content height and the artifact preview fills the remaining
workspace. The artifact text view scrolls internally when content exceeds the
available area.

The workbench uses stable regions rather than a multi-document interface or a
separate navigation shell.

### Workbench Stepper Flow

The top-level stepper should guide both configuration and operation. It is not a
wizard for the full test run, but it is also not only a passive configuration
form. The user should be able to keep PLC control, UnitTest run control, and
run-related parameters close together during debugging.

The first UI direction uses this stepper:

| Step | Purpose |
| --- | --- |
| Project / Profile | Select the engineering project or project source. The access profile is suggested from the project type where possible and can be changed when needed. Loading the access profile is part of this step. |
| PLC / Runtime / Communication | Discover and select the PLC/CPU within the project, select the matching simulation/runtime target, and capture communication-related values that are needed before operation. |
| Profile Configuration | Edit optional profile-projected semantic configuration fields and validate the complete current profile configuration without application knowledge of profile JSON paths. |
| Operation / Test Run | Expose PLC runtime controls, expose UnitTest run controls, and keep run-specific parameters such as timeout close to the controls used during debugging. |

The stepper may trigger workflow-level access-profile loading and discovery.
Discovery may open/read project context where required for selection. It must
not build, transfer/download, start the PLC, or trigger UnitTest lifecycle
actions.

The final `RunSetup` is applied when leaving `Profile Configuration`, after the
required project/PLC/runtime selections and profile configuration are valid. The horizontal status badge bar
and the status bar provide the lightweight review function; a
separate full review page is not part of the stepper.

Config file loading remains a menu/import action. Loading a config file may
pre-fill the same configuration selection flow, but the compact stepper status
area does not provide a separate file-open command.

### Stepper Navigation And Release Actions

Forward navigation is performed through the current step panel, normally by a
`Next` action. `Next` validates the current step and performs the implicit
workflow actions required to make the next step available.

Backward navigation is performed through the active step's `Back` action or by
selecting an already reached previous step header. Both entry points invoke the
same workflow release action and confirmation behavior. Returning to an earlier
step is not simple display navigation. It means the workflow releases state up
to the target level so that the target step is editable again as if its previous
successful `Next` action had not happened.

Backward navigation always requires confirmation before the step changes.

Confirmation dialog headline format:

```text
Return to step <x> - <stepName>
```

Confirmation buttons:

```text
[OK] [Cancel]
```

The dialog body should clearly state which workflow context will be released.
The exact wording can be refined during WPF implementation.

Release actions should mirror the main lifecycle levels:

| Level | Setup / Prepare Direction | Release Direction |
| --- | --- | --- |
| Access profile | Load selected access profile. | Unload access profile when switching profile, profile reload is required, or application shutdown releases the full session. |
| Project | Open/read or prepare the selected project context. | Release project context when switching project or ending the session. |
| Runtime / target | Prepare runtime and transfer the selected project/PLC to the runtime target. | Release runtime/target context when switching runtime/PLC target or ending the session. |
| UnitTest communication / monitoring | Prepare communication and start monitoring after PLC start. | Stop monitoring and release communication when stopping PLC, changing runtime context, or ending the session. |

Back navigation rules:

- Returning from `Profile Configuration` to `PLC / Runtime / Communication`
  discards the editor projection and makes target selection editable again. No
  prepared runtime or operation context exists at this point.
- Returning from `Operation / Test Run` to `Profile Configuration` releases the
  operation/test-run, communication/monitoring, and runtime levels. The current
  application-owned profile configuration remains available for editing.
- Returning to `Project / Profile` releases operation/test-run,
  communication/monitoring, runtime/target, project/discovery context, and
  unloads the access profile. Project/profile selection becomes editable again,
  with the previous configuration values kept as editable preselection.
- Returning directly from a later step to `Project / Profile` releases all
  applicable later levels before the Step 1 content becomes editable.
- The important selected information from previous steps remains visible through
  the horizontal status badge bar and the session status bar while the user
  stays in later steps.

The UI may expose contextual release commands where useful, but backward
navigation through the stepper owns the normal user path for returning to an
earlier editable level. Overall cleanup remains a composed shutdown/recovery
action, not the only release API.

### Session Sidebar And Status Badges

There is no separate full-width status summary strip. The application should
feel like a professional developer workbench, closer to VS Code than a website.
The left session sidebar owns step navigation and active-step actions. Workflow
status is shown in a compact horizontal badge bar above the main content so the
process state remains visible without consuming the main work area.

The horizontal status badge bar should show status badges for process
communication:

| Status Badge | Example States |
| --- | --- |
| Runtime Setup | Not configured, preparing, prepared, transferred, failed. |
| PLC | Unknown, stopped, starting, running, stopping, lost. |
| Communication | Not connected, connecting, connected, contract mismatch, lost. |
| Event Stream Monitor | Off, starting, active, interrupted, released. |
| Run Control | Not ready, ready, starting, running, completing, completed, resetting, aborting, error. |
| Diagnostics | None, information, warning, error. |

These badges are UI summaries over shared workflow/core state. They do not own
the workflow rules.

The badges are dense label/value status elements, not large cards and not
primary command buttons. Each badge should contain:

- a stable segment name,
- the current status value,
- a small status marker such as a dot or narrow side bar,
- optional tooltip text,
- optional navigation/focus behavior to the related detail area.

Badge clicks may focus the related detail view, but they should not execute
workflow actions. Workflow actions remain in the active stepper panel action
bar or menu.

Color should be used as a small scan aid, not as a large background treatment
and not as the only information channel:

| Meaning | Suggested Marker |
| --- | --- |
| Ready, running, connected, active, passed. | Green marker. |
| Loading, preparing, starting, stopping, pending. | Blue or neutral accent marker. |
| Warning, changed, incomplete, degraded, reset required. | Amber marker. |
| Failed, unavailable, contract mismatch, lost, error. | Red marker. |
| Unknown, not selected, not prepared, inactive, no run. | Gray marker. |

`Runtime Setup` and `PLC` are separate badges. Runtime Setup describes
preparation and transfer state. PLC describes the observed execution state of
the concrete PLC target.

### Diagnostics And Output Area

The bottom diagnostics/output area should be tabbed or otherwise segmented:

| Tab / View | Purpose |
| --- | --- |
| Output | Terminal-style progress and concise summary stream. |
| Diagnostics | Actionable diagnostics with severity, source/context, message, and selected detail. |

Detailed logs open through the system/default editor rather than an embedded
deep log viewer.

The bottom area does not contain a separate artifact tab.
Artifact inspection belongs to the main content area, while file/folder open
commands belong to the menu model.

`OutputPanel` layout:

| Region | Purpose |
| --- | --- |
| Tab header | `Output` and `Diagnostics` tabs. The `Diagnostics` tab may show a count/severity badge. |
| Active tab content | Scrollable output stream or diagnostics list/detail view. |

`OutputPanel` is user-resizable in height as part of the main shell layout. It
should not introduce additional splitters in the first design. Each tab content
area scrolls internally when content exceeds the visible space.

`Output` tab:

| Element | Behavior |
| --- | --- |
| Output stream | Compact chronological text entries for user-facing progress and summaries. |
| Clear Output | Context menu action on the output stream and View menu action. Clears the visible output stream only. Does not clear diagnostics, results, artifacts, or log files. |

`Diagnostics` tab:

| Element | Behavior |
| --- | --- |
| Diagnostics list | Compact list of current coalesced diagnostic entries with severity marker, source/context, message, and last-seen/update context where useful. It is not a chronological event log. |
| Diagnostic detail | Shows selected diagnostic detail where needed, including category, source, phase, message, technical detail, evidence reference, first/last seen metadata, and occurrence count when available. Can be inline or a right/detail region inside the tab if implementation needs it later. |
| Clear Diagnostics | Context menu action on the diagnostics list and View menu action. Clears the shared diagnostic sink and removes current diagnostic snapshot entries from the diagnostics view. Does not change workflow state, action gates, run evidence, result interpretation, output history, or application logs. |

The `Diagnostics` tab should use one internal vertical splitter between
`Diagnostics list` and `Diagnostic detail`. The list width is user-resizable and
the detail area fills the remaining space. The design should not specify
concrete pixel or percentage widths.

The diagnostics list presents the current `DiagnosticSnapshot` from the
application-owned shared diagnostic sink. Workflow-origin diagnostics and
WPF/application-origin diagnostics such as configuration-load, application-log,
and artifact-access problems are shown in the same list. Repeated equivalent
diagnostic emissions should remain one visible row with updated
last-seen/occurrence metadata. Chronological diagnostic emission history belongs
to the output stream and application log, not to duplicate rows in the
diagnostics list.

Diagnostics are diagnostic help for the user. They are not the authority for
workflow state, action availability, run result interpretation, artifact state,
or output history. The UI may use diagnostics to explain blocked or failed
states, but the command gates and result views must still come from shared
workflow, run-epoch, and output-state models.

The `Output` tab should not introduce an internal splitter. The output stream
fills the active tab content and scrolls internally.

The `Diagnostics` tab badge should summarize current visible diagnostics, for
example a count and/or highest severity marker. Diagnostics are not duplicated
in the `SessionStatusBar`.
The badge should keep the count readable. Prefer a colored ring/marker over a
filled badge background when the count would otherwise lose contrast.

## Terminal-Style Output

The terminal-style output is a human-facing progress and summary stream. It is
not the technical application log and it is not the primary result model.

Workflow, diagnostics, and artifact-export messages share one chronological
stream. A later reconnect message must not move an earlier artifact message
behind it.

It should show concise chronological entries such as:

- workflow phase started/finished,
- active operation messages,
- important diagnostics,
- command outcomes,
- produced artifact and log paths,
- concise run summary.

Normal UnitTest event-stream entries should not be dumped into this view by
default. Detailed technical logs remain in the application log file. Structured
test results remain in the result tree and artifact/export model.

The terminal-style output should live in the bottom diagnostics/output area,
most likely as an `Output` tab beside diagnostics.

The output stream is a UI-facing narrative. It should be compact and readable,
but it should not attempt to replace structured result views or the technical
log file.
Output and diagnostics are content-dense support views. They should use compact
row heights and restrained padding, especially in the scrollable list/text
areas.

## Command Model And Placement

The WPF command model should distinguish between primary manual workflow
actions, secondary view/open actions, and advanced validation actions.

Primary workflow actions should be visible in the action button bar at the
bottom of the active stepper panel. They are the commands the user needs for
the current manual validation step. This is not a global icon toolbar.

| Command | Placement | Workflow Meaning |
| --- | --- | --- |
| Next | Project/Profile, PLC/Runtime/Communication, and Profile Configuration step action bars. | Validates the active step and performs the implicit progression action: profile loading/discovery from Step 1, target acceptance from Step 2, or final setup application from Step 3. |
| Next / Apply Selection | Step action bar. | Moves forward when the current step has enough valid information. |
| Start PLC | Operation/Test Run step action bar. | Starts the prepared PLC and connects UnitTest communication/monitoring when gates allow it. |
| Start | Operation/Test Run step action bar. | Starts an application-triggered UnitTest run. |
| Abort | Operation/Test Run step action bar. | Requests abort for an active or starting UnitTest run. |
| Reset | Operation/Test Run step action bar. | Requests UnitTest lifecycle reset and returns communication to a reusable state when possible. |
| Reset EventBuffer | Operation/Test Run recovery action bar and Run menu. | Discards PLC-buffered events after confirmation; does not discard retained PC run evidence or imply lifecycle Reset. |
| Stop PLC | Operation/Test Run step action bar. | Stops the prepared PLC target. |
| End Session | Operation/Test Run step action bar and File menu. | Ends the complete current session, releases workflow/profile/service resources, discards the configuration draft, and returns to Step 1. |

The step action bar should expose command availability directly from action
gates. Disabled or blocked commands should provide the gate reason through
tooltip, status text, or detail focus. The action bar should not duplicate
advanced low-level workflow steps.

Action placement inside the bar:

| Position | Action Type |
| --- | --- |
| Left | Primary actions for the current step, such as load, next, start PLC, start, abort, reset, stop PLC. |
| Right | Secondary or release actions, such as release target context, release session, cleanup, or unload profile when applicable. |

### Menu Structure

The menu should provide standard access to configuration, view/open actions,
advanced/manual actions, and application commands. It is the correct home for
commands that are useful but not part of the normal visible workflow.

Menu structure:

| Menu | Entries | Notes |
| --- | --- | --- |
| File | Load UnitTest..., Import Configuration..., End Session, Exit. | `Load UnitTest...` loads a complete UnitTest setup directly to the operation step when possible. `Import Configuration...` pre-fills the stepper and lets the user walk through the steps manually. Exit triggers end-session/shutdown policy. |
| Run | Start PLC, Stop PLC, separator, Start UnitTest, Abort UnitTest, Reset UnitTest, Reset EventBuffer. | Mirrors primary PLC and UnitTest commands and explicit buffer recovery. PLC commands and UnitTest run commands are visually separated. Preparation and session lifecycle are not exposed here. |
| View | Clear Run Result, Clear Output, Clear Diagnostics, Open Test Result Artifact, Open Output Folder, Analyze Current Run..., Analyze PLC Lifecycle..., Analyze Communication Health.... | View/reset/open/analysis actions are secondary menu actions. Entries are ordered by expected workflow relevance within their groups. |
| Tools | Refresh Profile Registrations, Reconnect Runtime, separator, Settings... | Refresh profile registrations is available before a profile is loaded. Reconnect Runtime mirrors the Step 4 recovery action and obeys the same workflow gate. Settings opens application settings that are separate from the UnitTest run setup. |
| Help | Help Contents, Logs submenu, About. | Help Contents opens the shipped offline help. The Logs submenu opens the application log, access-profile log, or shared log folder. Log access is a diagnostic/help action, not a normal workflow operation. |

Menu entries must obey the same action gates and availability model as primary
commands. The menu must not provide a bypass around workflow state, diagnostics,
or cleanup boundaries.

The View menu contains a separate group after the file/folder open actions for
`Analyze Current Run...`, `Analyze PLC Lifecycle...`, and
`Analyze Communication Health...`. Selecting one performs that analysis and
opens the single modeless Evidence Analysis window.

### Evidence Analysis Window

Evidence analysis is a diagnostic support surface rather than another
chronological bottom-panel tab. One modeless window presents the currently
selected analysis and provides toolbar actions for all three analysis scopes.
The header names the active scope and focus and shows assessment, completeness,
generation time, and activity range. Copy, package export, and clear actions are
visually separated from the analysis-generation actions.

Each analysis action atomically captures one immutable trace snapshot and
creates one report package. The visible report, Copy action, and Export
action reuse that package; they do not recapture evidence or rerun analysis.
The footer indicates whether the report was generated from new evidence,
refreshed without changes, superseded by newer relevant evidence, or retained
from a previous session.

The ZIP handoff contains compact `analysis.md`, optional Run `hierarchy.json`,
and ordered `events.ndjson`. These files share package identity, scope,
correlation, capture time, activity range, and completeness metadata. The
Markdown report leads with actionable findings; the full ordered activity
payload remains available for automated or expert follow-up without requiring
raw worker-event logging.

### Secondary Open Actions

Opening files or folders is secondary:

| Action | Primary Placement | Reason |
| --- | --- | --- |
| Open Test Result Artifact | View menu. | The artifact content is already inspectable in the `Test Result Artifact` main-content tab. |
| Open Output Folder | View menu. | Opens the configured artifact output directory; not a workflow phase. |
| Open Application Log | Help / Logs submenu. | Opens the current application log. |
| Open Access Profile Log | Help / Logs submenu. | Opens the active access-profile log when available. |
| Open Log Folder | Help / Logs submenu. | Opens the application log directory for access to profile and worker logs. |

### Configuration File Actions

The File menu should provide two configuration-file based entry points because
they support different user intentions:

| Action | Behavior |
| --- | --- |
| Load UnitTest... | Opens a file dialog, loads and validates a complete UnitTest setup, performs the required setup/discovery progression, and moves to `Operation / Test Run` when successful. If a step fails, the UI remains at or moves to the failing step and shows diagnostics. |
| Import Configuration... | Opens a file dialog, imports values into the stepper as a draft, starts at `Project / Profile`, and lets the user manually continue through the steps with pre-filled values. |

`Load UnitTest...` is the fast path for known-good setups. `Import
Configuration...` is the manual/debuggable path for verifying where setup or
discovery fails.

`Import Configuration...` imports all common and profile values that are usable
for the draft and lets the user complete missing or incomplete profile details in
the stepper. `Load UnitTest...` requires a complete profile-valid configuration
because the setup progression is automated up to the operation/test-run surface.

Both WPF entry points ignore `output.directory` and `output.testResult` from the
UnitTest configuration file. WPF always produces the test result artifact when a
result can be produced and uses the output folder from application settings,
not imported UnitTest configuration values.

The UI should not require the user to open external files for normal result
inspection.

### Clear And Reset View Actions

Clearing visible output is a presentation action, not workflow cleanup and not a
resource release action.

The presentation actions are:

| Action | Placement | Effect |
| --- | --- | --- |
| Clear Run Result | Main content header and View menu. | Clears both `Run Result` and `Test Result Artifact` views. Output and diagnostics remain unchanged. |
| Clear Output | Output tab context menu and View menu. | Clears the visible terminal-style output stream. |
| Clear Diagnostics | Diagnostics tab context menu and View menu. | Clears the shared diagnostic sink and removes current diagnostic snapshot entries from the diagnostics view. Output and application logs remain unchanged. |

`Output` and `Diagnostics` intentionally use context menus for the local clear
actions. This preserves vertical panel space and
avoids a visible toolbar that would be empty except for one command. The same
actions are mirrored in the View menu for discoverability.

`Clear Run Result` affects both main-content result tabs because `Run Result`
and `Test Result Artifact` represent the same current/previous run result
context. A new test run automatically replaces or reinitializes these result
views. Ending a session does not automatically clear them; it leaves the last
result visible until a new run/session replaces it or the user resets the result
view.

### Advanced Commands

Advanced/manual commands are secondary and should not dominate the first screen.
The Tools menu is limited to access-profile refresh,
runtime reconnect recovery, and application settings.

Tools commands:

| Command | Purpose |
| --- | --- |
| Refresh Profile Registrations | Run explicit access-profile capability registration while Step 1 is active and no profile has been loaded, then reload the profile selection from the registry. |
| Reconnect Runtime | Replace technical runtime and UnitTest communication resources for the current prepared target without discovery, download, reprovisioning, target release, or PLC control. |
| Settings... | Open WPF application settings. Settings are persisted in an application-internal config file and are separate from the UnitTest run setup. |

Other low-level operations such as open project, build project, create/attach
runtime, download project, or connect communication are coordinated by the
stepper, not exposed as independent top-level commands.

`Reconnect Runtime` is the runtime recovery command in the menu because it has a
concrete Step 4 debugging workflow and a Shared Core
action gate. Reloading an access profile and lower-level project, runtime, or
communication commands remain outside the menu model.

### Step Action Bar Grouping

The `SessionSidebar` separates domain actions from step/session boundary
actions. Step/session boundary actions are placed at the bottom of the sidebar:
`Next` in Steps 1-3 and `End Session` in Step 4. This keeps structural workflow
actions visually distinct from PLC and UnitTest function buttons.

The active stepper panel action area should group commands by the current step:

```text
Project / Profile
  Step Boundary:
    [Next] at the bottom of the sidebar

PLC / Runtime / Communication
  Step Boundary:
    [Next] at the bottom of the sidebar

Profile Configuration
  Step Boundary:
    [Next] at the bottom of the sidebar

Operation / Test Run
  PLC Control:
    [Start PLC] [Stop PLC]
  Test Run:
    [Start] [Abort] [Reset]
  Session:
    [End Session] at the bottom of the sidebar
```

Visual grouping should be modest. The action bar should stay compact and usable
during manual testing.

`End Session` ends the complete current workbench session, releases
UnitTest communication/monitoring, runtime/target context, project/discovery
context, unloads the access profile, discards the current run setup and
configuration draft, and returns to `Project / Profile`. It requires
confirmation before executing.

Returning to `Project / Profile` through the stepper is different from `End
Session`: it releases workflow resources up to Step 1, but keeps the previous
configuration values as editable preselection. `End Session` discards those
draft values.

## Status Bar

The status bar is a compact session status line. It should identify the current
session rather than duplicate every process badge.

It should show only always-useful short session identity on the left:

- selected access profile,
- selected project or project file name,
- selected PLC/runtime target when available.

The right side may show the current activity/progress state for async workflow
operations. This is not stepper-specific; it represents the currently running
operation regardless of whether it was triggered by `Next`, `Start PLC`,
`Start`, `Abort`, `Reset`, `Stop PLC`, artifact export, or session ending.

- indeterminate activity indicator for running operations with unknown progress,
- optional determinate progress bar only when real progress is known,
- short progress status text such as `Preparing project...`, `Starting PLC...`,
  `Running test...`, `Exporting result...`, or `Ready`.

The UI must not imply exact progress when the underlying operation cannot report
it. In that case, the activity area should use a running/indeterminate
presentation.

It should not show run state, diagnostics count, full diagnostics, result tree
details, event-stream data, artifact lists, or configuration details.

Diagnostics state belongs to the `Diagnostics` tab in the `OutputPanel`, for
example as a count and/or severity marker on the tab header. Run state belongs
to the `StatusBadgeBar` and the `WorkspaceArea`.

The active-operation field owns one icon-only cancel command. It is visible only
while a cancellable registration, discovery, setup, or automated-load operation
is active. The tooltip is `Cancel current operation`. After activation, the
button disables immediately and the activity field shows
`Cancelling operation...` until the lower layers reach a defined terminal or
recovery boundary.

The cancel command is not shown for PLC control, UnitTest Start/Abort/Reset,
test-run observation, release, End Session, or shutdown cleanup. Cancellation
means stop waiting for the requested setup operation; it does not promise
rollback of work already dispatched to a profile or vendor API.

The footer remains a session-identity/activity surface. Diagnostic severity is
shown in the Diagnostics badge and diagnostics/output area, not as a duplicate
footer count or severity marker. Process-state markers belong to the status
badge bar.

## Workflow State And Action Availability

The WPF UI should not calculate workflow validity from control state. It should
present state and action availability from the shared workflow status surface,
shared workflow action gates, and WPF stepper placement context.

Workflow status badges are projections, not authoritative state machines. The
WPF layer may aggregate lower-level workflow status into compact badge state, but
the aggregation must be deterministic and traceable to workflow status and action
gate data. Badge text, colors, animations, diagnostics, or WPF-only derived
strings must not be used to decide whether a workflow command is functionally
safe or available.

The horizontal process badges use WPF-local structured status view states. Each
badge projection has a stable status kind, display text, marker styling, and
optional activity animation flag. Command logic and behavior tests use the
stable status kind or the shared action gates; display text is presentation only.
The structured process-badge model covers only Runtime Setup, PLC,
Communication, Monitoring, and Run Control. Footer/session fields,
Runtime-Setup detail/substatus fields, diagnostics, artifact state, run result,
run tree, run status, and step messages remain separate projections unless a
later design step identifies a concrete behavioral dependency that requires a
structured model.

The UI state model has three visible layers:

| Layer | Purpose |
| --- | --- |
| Stepper state | Shows the current user workflow level and controls forward/backward progression. |
| Horizontal status badge bar | Shows compact process state above the main content area. |
| Step action bar | Shows commands available for the active step. |

### Stepper State

The stepper has four steps:

| Step | Editable When | Locked When | Completed When |
| --- | --- | --- | --- |
| Project / Profile | No profile is loaded, or the user returned to Step 1 through confirmed release. | Access profile is loaded and later step state depends on it. | Access profile was loaded successfully for the selected project/profile input. |
| PLC / Runtime / Communication | Step 1 completed and target/communication selections are not locked by operation state. | Profile configuration or operation/runtime state depends on these selections. | Required PLC/runtime/communication selections are accepted for profile configuration. |
| Profile Configuration | Step 2 completed and no prepared operation/runtime context depends on the current values. | Final setup was applied or operation/runtime state depends on the values. | Generic field checks and authoritative profile validation both succeed. |
| Operation / Test Run | Step 3 completed and final run setup has been applied and prepared. | Not applicable; this is the active workbench operation step. | The step stays active while the session is open. |

Selecting an earlier step releases workflow state to that level after
confirmation. Selecting a later step is only possible when all previous steps are
completed.

### Horizontal Status Badge Presentation

Status badges should use short labels and small markers. They should
avoid long explanations and rely on tooltip/detail focus for details.

| Field | Values To Present | Detail Focus |
| --- | --- | --- |
| Runtime Setup | Not configured, preparing, transferred, failed. | Shows project preparation, runtime preparation, and project transfer context. |
| PLC | Unknown, stopped, starting, running, stopping, lost. | Shows PLC execution state when available. |
| Communication | Not connected, connecting, connected, contract mismatch, lost, released. | Shows communication readiness and relevant diagnostics. |
| Event Stream Monitor | Off, starting, active, interrupted, released. | Shows whether event-stream evidence monitoring is active for the current UnitTest communication session. |
| Run Control | Not ready, ready, starting, running, completed, resetting, aborting, error. | Shows UnitTest RunControl lifecycle/readiness state. |
| Diagnostics | None, information, warning, error. | Focuses diagnostics list/details. The highest retained severity determines the marker color; no diagnostics uses a neutral marker. |

The Run Control badge is a WPF projection over structured workflow evidence. It
uses coarse execution status and independent execution signals for its existing
presentation states; detailed PLC RunControl state and producer result remain
available as supplemental evidence. The badge does not reconstruct missing
facts, decide command success, or determine the test result.

The status strip is read-only summary. Segment focus may move the detail pane,
but segment clicks do not execute workflow actions.

Colors and movement belong to badge presentation. Starting, running, resetting,
and aborting style states may use a subtle animated marker; ready and completed
states should remain static. These visual choices must not become the source for
command availability.

Run result state is not represented as a separate global status badge. Completed
or failed UnitTest result details belong in the run tree and test-result artifact
view. Lower-level causes and detailed conflicts belong in diagnostics and output,
not in additional first-level badges unless a concrete user workflow requires
one later.

### Action Availability Rules

The WPF action bars and menu entries should use the shared-core action gate
availability categories directly. The UI may map them to concrete control
properties, but it should not collapse the workflow semantics into a plain
enabled/disabled model.

Command `CanExecute` rules should combine action gates with explicit UI placement
context only. UI placement context covers where a command belongs, such as a Step
4 operation button or menu entry. Functional conditions such as PLC running,
communication connected, monitoring active, RunControl ready/abortable, or
operation conflicts must come from action gates rather than status badge text.

| Category | UI Presentation |
| --- | --- |
| Available | Enabled. |
| Pending | Disabled or progress-marked while an operation is active. |
| Blocked | Disabled with tooltip/status reason from the action gate or step state. |
| Unsafe | Disabled unless the UI presents an explicit confirmation/release path. |
| Not applicable | Hidden or disabled depending on whether the command is useful context. |

The workbench uses disabled visible buttons for primary workflow
actions in the active step, so the user can see which action is expected next.
Menu entries may be disabled when unavailable.

UI mapping:

| Core Availability | `IsEnabled` | `IsVisible` | UI Notes |
| --- | --- | --- | --- |
| `Available` | `true` | `true` | Execute directly. |
| `Pending` | `false` | `true` | Show active operation/progress context. |
| `Blocked` | `false` | `true` | Show reason and missing prerequisite. |
| `Unsafe` | `false` by default | `true` | Offer only explicit confirmation/release path where designed. |
| `NotApplicable` | `false` | Context-dependent | Hide for low-value menu entries; keep visible for primary step actions only if it helps orientation. |

This mapping is presentation behavior. The action gate availability itself is a
shared workflow concept and should come from the workflow/core layer.

### Step Action Availability

| Step | Command | Available When | Blocked / Confirmation Behavior |
| --- | --- | --- | --- |
| Project / Profile | Next | Required project/profile input is present and no operation is pending. | Loads the access profile. The UI may already show Step 2 as the target-discovery surface while loading. On failure, keep recovery navigation available and show diagnostics. |
| PLC / Runtime / Communication | Next | Access profile is loaded and required PLC/runtime/communication selections are present. | Opens Step 3 with the profile configuration projection. On failure, stay in Step 2 and show diagnostics. |
| Profile Configuration | Next | Generic field validation and authoritative profile validation succeed. | Applies the current payload to final setup and runs preparation/provision/transfer. On failure, stay in Step 3 and show diagnostics. |
| Operation / Test Run | Start PLC | Project/runtime transfer is complete and PLC is not running. | Disabled before prepare/transfer or while operation is pending. |
| Operation / Test Run | Start | PLC is running, UnitTest communication is connected, monitoring is active, and no run is active. | Disabled until Start PLC/communication/monitoring readiness is reached. |
| Operation / Test Run | Abort | Run is starting or running and lifecycle communication is trusted. | Disabled when no run is active. |
| Operation / Test Run | Reset | PLC is running and lifecycle communication is reachable enough to request reset. | Disabled when runtime/communication context is not available. |
| Operation / Test Run | Reset EventBuffer | Shared event-buffer reset gate permits the request. | Separate destructive recovery command; confirmation precedes dispatch. PLC-framework valid-use restrictions remain the caller's responsibility. |
| Operation / Test Run | Stop PLC | Runtime target is prepared and addressable. | Confirmation may be required when a run or lifecycle command is active. |
| Operation / Test Run | End Session | Session resources or configuration draft exist. | Always confirmation-gated; releases full session and discards draft. |

### User Action Gate Matrix

The following matrix is the WPF-facing interpretation of action gates. It is a
UI design reference; the actual gate state should come from the shared workflow
where possible.

| User Action | `Available` | `Pending` | `Blocked` | `Unsafe` | `NotApplicable` |
| --- | --- | --- | --- | --- | --- |
| Step 1 `Next` | Project/profile input is valid enough to load the access profile. | Profile load or validation is running. | Required project/profile input is missing or invalid. | Current active session would be replaced without confirmed release. | User is not in Step 1. |
| Step 2 `Next` | Access profile is loaded and required PLC/runtime/communication selections exist. | Target validation is running. | Profile not loaded, discovery failed, or required target values are missing. | Changing target would invalidate later configuration/operation context without confirmed release. | User is not in Step 2. |
| Step 3 `Next` | Projected values pass generic checks and profile validation accepts the complete context. | Preparation, provisioning, or transfer is running. | Projection failed, a field is invalid, or profile validation rejects the context. | Applying changed values would replace an active prepared context without release. | User is not in Step 3. |
| Stepper return to Step 1 | A later step is active and confirmation can be shown. | Cancellation recovery or release is running. | Release cannot start until a non-cancellable command reaches its operation boundary. | Always confirmation-gated; first cancels and awaits a cancellable setup operation, then releases all later state and unloads the profile. | Already in Step 1. |
| Stepper return to Step 2 | Step 1 is completed and Step 3 or Step 4 is active. | Release operation is running. | Step 1 has not completed. | From Step 4, releases operation/runtime state before target editing. | Already in Step 2 or Step 2 has never been reached. |
| Stepper return to Step 3 | Step 3 configuration exists and Step 4 is active. | Release operation is running. | Profile configuration cannot be restored or release failed. | Releases operation/runtime state and reprojects current profile values. | User is not in Step 4. |
| Load UnitTest... | No modal workflow operation is pending. | Workflow operation or file load is running. | Selected file is invalid or incomplete for direct load. | Existing active session/draft would be replaced; confirmation required. | Never. |
| Import Configuration... | No modal workflow operation is pending. | Workflow operation or file import is running. | Selected file is invalid or unreadable. | Existing draft/session would be replaced; confirmation required. | Never. |
| Start PLC | Transfer completed and PLC target is addressable but not running. | Preparation/transfer/start/stop operation is running. | Project/runtime/transfer target is not ready. | Runtime context is unknown/stale or release is pending. | User is not in Step 4. |
| Start | PLC is running, UnitTest communication is connected, monitoring is active, and no run is active. | Start/abort/reset or completion stabilization is running. | PLC not running, communication not connected, monitoring inactive, or RunControl not ready. | Previous run completion is still settling or run context is ambiguous. | User is not in Step 4. |
| Abort | Run is starting/running and lifecycle communication is trusted. | Start/abort/reset operation is running. | Lifecycle communication is unavailable. | Target context is unknown or release/cleanup owns the context. | No active/starting run. |
| Reset | PLC is running and lifecycle communication is reachable. | Start/abort/reset operation is running. | PLC not running or lifecycle communication unavailable. | Active/completing run may be interrupted; confirmation may be required by policy. | User is not in Step 4. |
| Reset EventBuffer | Shared reset-buffer gate permits the request. | Another conflicting workflow operation is running. | Event-stream/session prerequisites are unavailable. | Confirmation required; retains PC evidence and marks interrupted observation incomplete. | User is not in Step 4. |
| Stop PLC | Runtime target is prepared and addressable. | Start/stop/prepare/release operation is running. | Runtime target is not prepared/addressable. | Active run or lifecycle command is active; confirmation or abort/reset path required. | User is not in Step 4. |
| Reconnect Runtime | Setup, profile session, prepared runtime context, and effective runtime target exist. Invalid lifecycle/event-stream health and stopped/unknown PLC states are accepted recovery sources. | Another workflow operation or reconnect is running. | Required prepared target context is missing or profile health is unusable. | Active/finalizing run requires confirmation and is retained as incomplete; reconnect then replaces technical resources without releasing the target. | User is not in Step 4 or the profile returns not implemented. |
| End Session | Always available as global setup/session recovery. | Cancellation recovery or end/release operation is running. | Normally none. | Confirmation-gated when session resources or a draft exist; first cancels and awaits a cancellable setup operation, then releases the full session and discards the draft. | No-op when no session, draft, or resources exist. |
| Clear Run Result | Result view has content. | Normally never. | None. | None; presentation-only. | No result/artifact view content exists. |
| Clear Diagnostics | Diagnostics view has entries. | Normally never. | None. | Clears diagnostic sink entries only; does not affect workflow state, action gates, run evidence, result interpretation, output history, or logs. | No visible diagnostics. |
| Clear Output | Output stream has entries. | Normally never. | None. | None; presentation-only. | No visible output entries. |
| Open Test Result Artifact | Artifact path/content exists. | Artifact writing is running. | Artifact not generated or path missing. | None. | Artifact export not requested/supported. |
| Open Output Folder | Output folder is configured. | Output folder creation/write is running. | Output folder unknown or cannot be opened. | None. | No output policy/folder exists. |
| Open Application Log | Log path exists. | Logging startup/path discovery is not ready. | Log path unknown or file missing. | None. | File logging is not configured. |
| Refresh Profile Registrations | Step 1 is active and no access profile has been loaded. | Capability registration is running. | Registration or discovery diagnostics are shown in output/diagnostics; the current session is unchanged. | None. | After Step 1 loaded an access profile. |

## Step UX

The stepper is the primary interaction model for setup and operation. Each step
should have a focused purpose, a small number of required inputs, clear status
feedback, and a `Next` action only where forward progression is possible.

### Step 1 - Project / Profile

Purpose:

- select the project or project source,
- derive or select the access profile,
- load and validate the access profile enough to enable project/target
  discovery.

Primary content:

| Field / Element | Behavior |
| --- | --- |
| Project source | Optional when `Build and download project` is disabled. A selected source still filters compatible profiles without being opened. The read-only field is populated through Browse and can be cleared explicitly. |
| Access profile | Suggested from project/source when possible. Without a project source, all registered profiles remain available for explicit selection. |
| Build and download project | Selects `ProjectOwnershipMode.ApplicationManaged` when enabled and `ExternallyManaged` when disabled. It is WPF session input and is not persisted in the UnitTest configuration file. New manual/import sessions initialize it from the persisted WPF application default; the user may override it for the current session. |
| Profile/environment status | Shows not loaded/loading/ready/unavailable and related diagnostics. |
| Imported values indicator | Shows when the current values came from `Import Configuration...`. |

Step action:

| Action | Behavior |
| --- | --- |
| Next | Requires an access profile and, only for application-managed ownership, a project source. It switches to Step 2 as the target-discovery surface, loads the selected access profile, discovers project/PLC targets only for application-managed ownership, always discovers runtime targets, reports diagnostics, and enables the applicable Step 2 inputs on success. |

Failure behavior:

- show Step 2 as the target-discovery surface,
- keep Step 2 inputs disabled until discovery succeeds,
- keep Back to Step 1 and End Session available as recovery actions even when
  profile loading fails before a session context exists,
- show diagnostics in the diagnostics area,
- show profile/project/runtime discovery state in status/output areas,
- keep entered/imported values recoverable by returning to Step 1.

Step 1 does not prepare, build, transfer, start PLC, or validate UnitTest
communication.

### Step 2 - PLC / Runtime / Communication

Purpose:

- discover project-side PLC/CPU candidates,
- select the PLC/CPU to test,
- select or define the runtime/simulation target,
- capture communication-related values that are required before operation.

Primary content:

| Field / Element | Behavior |
| --- | --- |
| PLC selection | Shows discovered PLC/CPU candidates for application-managed ownership. It is not required for externally managed project handling. In fully manual mode, no candidate is preselected. Imported configuration values may preselect a matching candidate. |
| Runtime target | User selects or defines the simulation/runtime target associated with the selected PLC. In fully manual mode, no runtime target is preselected. Imported configuration values may preselect a matching target. |
| Communication values | Shows only values that cannot be safely derived before operation. Contract validation itself happens later when the PLC/runtime is running. |
| Discovery status | Shows discovery progress, empty result, failed discovery, and diagnostics. |
| Active project access | A neutral information/lock icon is visible while the application owns an open project session. Its tooltip states that the project is released after transfer, when returning to Step 1, or when ending the session. Release failures are reported through Diagnostics. |

Step action:

| Action | Behavior |
| --- | --- |
| Next | Validates the current project/PLC/runtime selections and opens Step 3 with the profile configuration projection. It does not yet prepare, build, transfer, start a PLC, or connect UnitTest communication. |

Failure behavior:

- stay in Step 2,
- keep discovered values and user selections where safe,
- show diagnostics and missing selections,
- do not build, transfer, start PLC, or connect UnitTest communication.

Step 2 is the target selection step and the boundary where the user confirms
the selected target. If validation fails, the user stays in Step 2 with
diagnostics and recovery guidance.

### Step 3 - Profile Configuration

Purpose:

- expose optional profile-owned configuration without application knowledge of
  profile JSON paths,
- edit the session-owned profile configuration through its neutral service,
- validate the complete profile configuration before setup side effects begin.

Every loaded session exposes `IAccessProfileConfigurationService`. Profiles
without configurable fields return an empty projection, so Step 3 remains a
stable part of the workflow without requiring nullable service handling. WPF
renders projected controls from field semantics:

| Field Semantics | WPF Control |
| --- | --- |
| Allowed values present | Combo box using profile-provided values and display names. |
| Boolean | Checkbox. |
| String, Integer, Float | Text field with immediate invariant generic validation. |

Field order follows the profile projection. The profile does not select WPF
controls or layout. Each successful typed edit updates the configuration
service snapshot and causes reprojection and revalidation; failed edits retain
the previous snapshot. Unknown, inactive, and currently non-projected values
remain preserved by the profile. WPF copies the resulting immutable
`AccessProfileConfig` into final run setup and does not pass the service itself
to shared workflow.

`Next` is available only when immediate generic checks succeed and the profile's
context-aware `ValidateConfig` accepts the complete current snapshot. It then
performs the applicable project/runtime preparation and transfer. On failure,
Step 3 remains active with diagnostics; on success, the application enters Step
4. Back returns to Step 2 without preparing resources.

### Step 4 - Operation / Test Run

Purpose:

- keep runtime/PLC control and UnitTest run control close together,
- expose run-specific parameters used during manual debugging,
- present the operational state for repeated local test runs.

Primary content:

| Field / Element | Behavior |
| --- | --- |
| Run setup summary | Compact summary of selected and prepared project/profile/PLC/runtime/communication values. Detailed review remains in status/detail regions. |
| Run timeout | Editable run-specific timeout used for operation/test-run behavior. |
| Operation state | Shows runtime/PLC/communication/event-stream/run-control state relevant to the action bar. |
| Result entry points | Shows access to `Run Result` and `Test Result Artifact` main-content tabs once data exists. |

Step action groups:

```text
PLC Control
  [Start PLC] [Stop PLC]

Test Run
  [Start] [Abort] [Reset]

Recovery
  [Reconnect Runtime] [Reset EventBuffer]

Session
  [End Session] at the bottom of the sidebar
```

Action behavior:

| Action | Behavior |
| --- | --- |
| Start PLC | Requests PLC start when gates allow it. UnitTest communication/monitoring setup follows independently from the profile-published PLC readiness observation. |
| Start | Starts an application-triggered UnitTest run. |
| Abort | Requests controlled interruption for an active/starting run. |
| Reset | Requests UnitTest lifecycle reset for recovery/reuse. |
| Reset EventBuffer | Requests discard of PLC-buffered events separately from lifecycle Reset. PC-side evidence is retained; interrupted run observation becomes incomplete. Acceptance is not proof of an observed clean buffer. The PLC framework's valid-use restrictions still apply. |
| Stop PLC | Stops the prepared PLC target and updates communication/runtime trust state. |
| Reconnect Runtime | Rebuilds technical runtime and UnitTest communication for the retained prepared target. It performs no project action, download, reprovisioning, target release, or PLC command. An active/finalizing run requires confirmation and is retained as incomplete evidence. |
| End Session | Releases the full session, discards the configuration draft, and returns to Step 1 after confirmation. |

A profile-published PLC state with `ReadyForExecution` starts UnitTest
communication and monitoring preparation whether the PLC was started by the WPF
command, was already running when runtime setup completed, or was started
externally. Command results never substitute for the observed PLC state.

Step 4 is not a final wizard page. It is the main operation surface for repeated
debugging runs after setup has been accepted.

### Configuration File Entry Points

Configuration file actions integrate with the stepper:

| Entry Point | Step Behavior |
| --- | --- |
| Load UnitTest... | Always uses application-managed project ownership. It loads a complete setup, validates projected/profile configuration, runs the required progression, and lands in Step 4 when successful. On failure, it moves to or remains at the failing step with diagnostics. |
| Import Configuration... | Imports usable values as a draft and starts at Step 1. The file does not select project ownership; `Build and download project` is initialized from the persisted WPF default and remains editable before the user advances with `Next`. |

Neither action should silently discard an active session. If a session or draft
exists, replacement requires confirmation.

## Layout Baseline

The workbench uses a compact developer-tool layout with minimal outer margins,
a left session sidebar, a horizontal status badge bar, and most vertical space
reserved for main content and diagnostics/output.

### Operation State Variants

The same layout should remain stable while the operation state changes. Buttons
change availability; regions should not jump or move.

| State | Status Summary | Expected Enabled Actions |
| --- | --- | --- |
| Prepared / transferred | Runtime ready, PLC stopped, communication not connected. | `Start PLC`, `End Session`. |
| PLC running / communication connected | PLC running, communication connected, monitoring active, no run active. | `Start`, `Reset`, `Stop PLC`, `End Session`. |
| Run active | Run running, monitoring active. | `Abort`, `Reset`, `Stop PLC` where policy allows, `End Session` confirmation-gated. |
| Run completed | Run completed and result views available. | `Start`, `Reset`, `Stop PLC`, `End Session`; artifact/result actions become available where output exists. |
| Failure / blocked | Relevant segment is warning/error and diagnostics tab has entries. | `Reconnect Runtime` remains available when the prepared target is retained and the reconnect gate accepts recovery; other actions depend on their gates. Blocked actions remain visible with reasons. |

### Operation Action Bar Rules

The Step 4 action area belongs to the left session sidebar and contains
runtime/test-run commands only. It does not contain step navigation.

Button grouping:

```text
PLC Control
  [Start PLC] [Stop PLC]

Test Run
  [Start] [Abort] [Reset]

Recovery
  [Reconnect Runtime]

Session
  [End Session] at the bottom of the sidebar
```

Rules:

- `Start PLC` starts the runtime PLC and connects UnitTest communication and
  monitoring according to workflow gates.
- `Start`, `Abort`, and `Reset` are UnitTest lifecycle actions.
- `Stop PLC` is a manual runtime control and may invalidate active communication
  or run state according to workflow policy.
- `Reconnect Runtime` is a full-width recovery action above step/session
  navigation. It uses the same command from the Tools menu and does not appear
  in the Run menu.
- `End Session` is visually separated from the functional command groups,
  placed at the bottom of the sidebar, confirmation-gated, and returns to Step 1
  with draft values discarded.
- Buttons should remain visible in Step 4, even when disabled, because they
  communicate the expected manual workflow.

### Confirmation Dialogs

Stepper backward navigation, `End Session`, and application exit from Step 2,
Step 3, or Step 4 require confirmation. Application exit from Step 1 closes
directly.

Dialog rules:

| Dialog Type | Required Headline | Required Buttons |
| --- | --- | --- |
| Return to previous step | `Return to step <x> - <stepName>` | `OK`, `Cancel` |
| End session | `End Session?` | `OK`, `Cancel` |
| Exit with active setup/session | `End Session and Exit?` | `OK`, `Cancel` |

Dialog texts:

| Dialog | Headline | Body | Help Tooltip | Buttons |
| --- | --- | --- | --- | --- |
| Return to Step 1 | `Return to step 1 - Project / Profile?` | `The prepared runtime, UnitTest communication, project context, and access profile will be released.` | `Current configuration values remain available as editable preselection.` | `OK`, `Cancel` |
| Return to Step 2 | `Return to step 2 - PLC / Runtime / Communication?` | `The current operation context, UnitTest communication, and runtime target will be released when present.` | `Project and profile values remain available. PLC, runtime, and communication values remain editable.` | `OK`, `Cancel` |
| Return to Step 3 | `Return to step 3 - Profile configuration?` | `The current operation context, UnitTest communication, and runtime target will be released.` | `The current profile configuration remains available for editing.` | `OK`, `Cancel` |
| End Session | `End Session?` | `The current session, prepared runtime, UnitTest communication, project context, access profile, and configuration draft will be released.` | None. | `OK`, `Cancel` |
| Application exit | `End Session and Exit?` | `The current session, prepared runtime, UnitTest communication, project context, access profile, and configuration draft will be released before the application exits.` | None. | `OK`, `Cancel` |

Confirmation dialogs should follow a standard Windows/WPF task-dialog style:
normal window chrome, icon plus main instruction/body text, and a standard
button bar. They should not use custom card-like content panels or decorative
application styling.

The body text should follow a standard confirmation-dialog structure: the
headline asks for the decision, and the body states the direct consequence of
confirming it. It should not explain the next workflow step or add instructional
copy. Secondary information, for example whether current configuration values
are kept as editable preselection or discarded, may be presented as contextual
help through a standard help icon. The tooltip text is not permanently visible
in the dialog. The exact body text can be refined during implementation. The
headline format and `OK`/`Cancel` buttons are fixed by this design.

### Menu Availability

Menu entries mirror the same rules:

- `Load UnitTest...` is available when no operation is pending. If a session is
  active, it requires confirmation and release before loading.
- `Import Configuration...` is available when no operation is pending. If a
  session is active, it requires confirmation before replacing draft values.
- `End Session` is available when a session or draft exists and is
  confirmation-gated.
- `Refresh Profile Registrations` is available only while Step 1 is active and
  no access profile has been loaded. It runs explicit capability registration,
  reloads the registry-backed profile list, preserves the current id/version and
  source selection when still available, and never releases or changes an active
  workflow context.
- `Clear Run Result`, `Clear Output`, and `Clear Diagnostics` are view actions
  and do not depend on workflow readiness.
- Open actions are enabled only when the corresponding path/content exists.
- `Open Output Folder` uses the configured artifact output directory and is
  available after that directory exists.

### Application Settings

The WPF application exposes application settings through:

```text
Tools | Settings...
```

Settings are application-level defaults and paths. They are not a second hidden
run configuration model and they should not contain convenience actions that do
not change a setting.

Settings dialogs use `Save` and `Cancel` as their standard buttons.

The settings dialog uses three vertically arranged tabs:

| Tab | Purpose |
| --- | --- |
| General | Default behavior for new manual sessions, including `Build and download project by default`. |
| Timeouts | Persisted operation and observation defaults for new sessions. The content is grouped as Setup, PLC Control, UnitTest Execution, and Recovery and scrolls vertically within the tab. |
| Files And Logging | Application-level file locations and policies for output files and logging. |

`Build and download project by default` is `false` for a fresh installation.
It initializes new manual sessions, imported drafts, and the Step 1 state after
`End Session`; changing the Step 1 checkbox remains session-local. `Load
UnitTest...` always selects application-managed project handling regardless of
this WPF default.

`General` and `Timeouts` must not contain project, profile, PLC, or runtime target
history. Those values belong to the current configuration/session flow or to a
UnitTest configuration file, not to application settings.

The timeout tab exposes project prepare, combined build/transfer, runtime
prepare, communication prepare, PLC command and separate start/stop reaction,
UnitTest command and reaction, test-run, final-event, and communication
revalidation values. These are application defaults; imported run configuration
and current stepper overrides take precedence where applicable. A zero
communication revalidation timeout disables automatic terminal timeout
handling. Binding-internal worker/IPC containment is not exposed here.

`Files And Logging` configures file and logging behavior. It exposes
`Application log level` and `Access profile log level`; both default to
`Information`. It also exposes opt-in raw access-profile event logging. That
option is enabled only while the access-profile level is `Debug` and is reset
to false when another level is selected.

The application does not expose worker or binding log levels. Profiles and
their internal components inherit the access-profile policy. Logging-path and
policy changes take effect on the next application start; commands that open
the current application log continue to target the active startup log until
then.

The section should not expose log-opening or folder-opening actions; those
remain menu/view commands.

The default output directory is the absolute per-user path
`%LOCALAPPDATA%\PLCSIM.UnitTest\artifacts`. A persisted legacy exact default of
`artifacts` is migrated to that path so an installed application does not write
below its installation directory. Explicit custom absolute and relative paths
remain valid; relative paths are resolved to absolute paths internally before
file access. The UI may show the configured value in the field and expose the
resolved absolute path through detail text or tooltip where useful.

### Result And Output Clearing

Starting a new test run reinitializes `Run Result` and `Test Result Artifact`.
It does not clear `Output` or `Diagnostics`.

Ending a session does not automatically clear output panes. It releases
resources and discards the configuration draft, while existing result/output
views may remain visible as previous-session information until reset or
replaced.

`Clear Output` is a presentation action. `Clear Diagnostics` clears the shared
diagnostic sink and removes current diagnostic snapshot entries from the
diagnostics view. Neither action changes workflow state, action gates, run
evidence, result interpretation, release state, log files, artifacts, output
history, or the current/previous run model.

## Implementation And Verification

[`MainWindow.xaml`](../../src/PLCSIM.UnitTest.App.Wpf/MainWindow.xaml) owns the
workbench layout and command bindings. Presentation state and coordinators live
under the application's `ViewModels/` directory; they consume shared workflow
publications instead of deriving availability from display text. User operation
is documented by the [shipped Help](../../src/PLCSIM.UnitTest.App.Wpf/Help/index.html),
not by a separate set of implementation-stage instructions.

The [WPF tests](../../tests/PLCSIM.UnitTest.Tests.Wpf) cover view contracts,
command gates, projections and variable-content structure rather than brittle
pixel comparisons. Profile-owned qualification documents describe exercised
real-runtime paths and their limits; this UI model is not additional evidence
that every feature or vendor combination has been tested.
