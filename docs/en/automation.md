# Automation

[简体中文](../zh-CN/automation.md) · [Getting started](getting-started.md) · [Plugin contract](plugin-development.md) · [Home](../../README.md)

The Windows service runs saved rules while the desktop UI is closed. Rules contain device commands, not scripts or application launch commands. Start with one device and check its result before combining actions.

## Create a rule

1. Save a lighting scene from the Lighting save menu if the rule should change lighting.
2. Open **Automation → New rule**. Give the rule a name and choose Schedule or Process.
3. For a schedule, select local `HH:mm` time and repeat days. For a process, enter an exact executable filename such as `game.exe`, without a path or wildcard.
4. Select a saved lighting scene and, optionally, a supported cooling preset, Windows power scheme, or process priority/affinity. Process policy is available only for a process trigger.
5. For a process rule, optionally capture known lighting, cooling, and power settings as exit actions. The snapshot is taken when saving the rule. It is not a live stack of all subsequent changes.
6. Enable the rule and save. New rules start disabled in the editor. Check its state after the trigger.

Scenes are copied into rules. Editing a scene later does not update an existing rule. Disabling or deleting a rule stops future triggers; it does not undo settings already applied. Closing or exiting the UI leaves rules running in the service.

## Schedule semantics

The service checks approximately once a second. A daily rule runs only during its exact scheduled minute in the Windows machine's local time zone, on a selected day. It does not catch up missed minutes after sleep, downtime, or a delayed control loop.

A durable date high-water mark reserves the occurrence before the first device command. A rule ID runs at most once per local date, including a repeated daylight-saving hour or clock rollback. Keeping the same ID retains this high-water mark across edits and restarts. A forward clock jump can suppress that rule until local dates move beyond its recorded date.

This is at-most-once dispatch, not guaranteed successful execution. A crash after reservation can leave no commands or only part of the list applied. A rule left `running` becomes `paused` on restart. The service does not replay uncertain writes.

## Process identity and exit actions

Matching is case-insensitive on the exact `.exe` filename, for interactive processes owned by the installation owner's Windows SID. It does not monitor every user's processes. Identity is **PID plus UTC creation time**, so a reused PID is a different process. The snapshot source does not return or log command lines, usernames, or full executable paths.

The first successful process snapshot after service startup or any explicit rule save establishes the baseline for enabled process rules. Instances present in that snapshot do not trigger start or exit catch-up, including instances opened between the save and that first poll. Later newly observed instances can trigger actions. Saving any rule resets process tracking for all rules; previously active instances no longer receive exit actions from that tracking. A process that starts and exits between polls can be missed.

Each newly observed instance receives start actions. The service supplies `pid` and `startTimeUtc` for `process.policy`; rules cannot supply those values. Exit actions run only after an active instance disappears and no matching instance remains. If an instance exits while another remains, its exit actions are suppressed. Exit commands cannot write `process.policy` or invoke global `device.release`. The Windows policy adapter handles dead process identities separately.

## Failures and bounds

Commands execute in order. There is no transaction across devices or automatic rollback of earlier commands. A command failure, uncertain result, or unsafe process snapshot pauses the affected rule with a diagnostic. A journal-write failure pauses automation globally. An unreadable journal requires trusted recovery; it is not silently replaced. A missing journal for saved rules blocks execution until an explicit save.

After resolving the cause, explicitly save the rules to reset paused states. Saving does not replay old process events or missed schedule minutes; a future matching minute can run if that date has not been reserved. Independent daily rules can continue after a process-snapshot failure.

Limits: schema version 1, 128 KiB rule document, 64 rules, 1–16 start commands per rule, at most 16 exit commands, 4 MiB journal, 4,096 processes per snapshot, 256 matching identities per rule, and 16 new matching instances per tick. Oversized bursts pause the rule rather than creating a backlog. The journal retains deleted rule IDs and their date high-water marks, with a total budget of 1,024 rule IDs. Repeatedly creating new IDs can exhaust that budget; deleting rules does not reclaim it.

## Service API and storage

- `app.automation.read {}` returns `{schemaVersion,rules,status}`; states are `disabled`, `ready`, `running`, `completed`, or `paused`.
- `app.automation.write` accepts `{schemaVersion:1,rules:[...]}`. Device IDs must be discovered public IDs (`pluginId::localId`) and capabilities must route to those devices.
- Rules use `{id,name,enabled,trigger,commands,exitCommands?}`. Daily triggers use `{kind:"daily",time:"20:00",days:[0,1,2,3,4,5,6]}`; 0 is Sunday. Process triggers use `{kind:"process",processName:"game.exe"}`.
- Each command uses `{deviceId,capability,parameters}` from the [standard contract](plugin-development.md). Use the currently discovered IDs, not sample or enumeration indices.

Protected files are `%ProgramData%\LightOrder\automation.json` and `automation-journal.json`. Writes flush a temporary file and replace the destination. Do not delete the execution journal as a normal reset; it carries the duplicate-suppression evidence.

Sources: [validation](../../src/LightOrder.Service/AutomationContracts.cs), [engine](../../src/LightOrder.Service/AutomationEngine.cs), [persistence](../../src/LightOrder.Service/AutomationPersistence.cs), [Windows owner-scoped snapshots](../../src/LightOrder.Service/AutomationProcesses.cs), and [editor](../../src/LightOrder.App/Assets/automation.js). New code and tests need the [release verification](build-and-release.md) for the exact package; source inspection alone does not establish real hardware behavior.
