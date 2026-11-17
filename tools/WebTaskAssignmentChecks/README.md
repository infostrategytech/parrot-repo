# Task assignment contract checks

These checks use controlled frontend state (an in-memory task store and
questions service). They do not load demo data, sign in or contact an API.

Build `Parrot.Web/Parrot.Web/Parrot.Web.csproj` with the repository's normal
frontend build first, then run from the repository root:

```powershell
dotnet run --project tools/WebTaskAssignmentChecks/WebTaskAssignmentChecks.csproj
```

If the web build uses a custom output directory, pass its absolute path as
`-p:ParrotWebBin=C:/path/to/web-output/` (keep the trailing slash).

Coverage: the Tasks table's Assignee menu saves at once with Undo and opens no
window; Take it in the task details; Pass on starts with nobody chosen and
passing the task; one permission-aware, de-duplicated list of people (no
fallback to the whole roster when the service returns nobody); the handover
note saved once and an honest message when the note saved but the move failed;
a changed assignee or a teammate who lost access is checked again before
saving; a stale Undo never overwrites newer ownership; asking a question never
moves the task.
