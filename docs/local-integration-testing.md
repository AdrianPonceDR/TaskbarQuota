# Local integration testing

The `codex/integration-all-features` branch combines:

- `feature/quota-replenishment-notifications`
- `feature/widget-visibility-policy`
- `fix/startallback-taskbar-overlap`

It is intended for testing those changes together. Keep feature work on its source branch and merge new commits into the integration branch; do not amend or force-push commits that have already been integrated.

## Update the integration branch

Commit and test changes on each source branch first. Then, from the integration worktree:

```powershell
git status --short --branch
git fetch origin
git merge --no-ff feature/quota-replenishment-notifications
git merge --no-ff feature/widget-visibility-policy
git merge --no-ff fix/startallback-taskbar-overlap
dotnet test tests\TaskbarQuota.Tests\TaskbarQuota.Tests.csproj -c Debug -p:Platform=x64
git push origin codex/integration-all-features
```

Stop and resolve any conflict before continuing. Preserve the behavior from both branches in shared settings, taskbar, interop, and test files.

## Publish the combined build

No installer or Inno Setup is required. The helper produces a self-contained x64 Release build and deploys it to:

```text
%LOCALAPPDATA%\TaskbarQuota-Integration\current
```

Quit every running TaskbarQuota instance first. Preview and then publish:

```powershell
.\scripts\Publish-LocalIntegration.ps1 -WhatIf
.\scripts\Publish-LocalIntegration.ps1
```

The helper validates the executable, PRI, and loose XBF resources before replacing the current build. It keeps the prior build at:

```text
%LOCALAPPDATA%\TaskbarQuota-Integration\previous
```

Use `-NoLaunch` when the updated build should remain stopped. Use `-InstallRoot` only for another dedicated installation folder; broad paths such as the drive root, user profile, or `%LOCALAPPDATA%` itself are rejected.

## Start with Windows

Launch the executable from the stable `current` folder, open **Settings**, and enable **Open at startup**. TaskbarQuota writes this per-user value:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TaskbarQuota
```

It must point to the stable executable followed by `--startup-widget`. Because later publications replace the contents of `current` without changing that path, the startup entry remains valid.

The app's built-in **Install update** action follows official `zioder/TaskbarQuota` releases and does not update this integration branch. Update this build by merging the source branches and running `Publish-LocalIntegration.ps1` again.

The integration build shares `%LOCALAPPDATA%\TaskbarQuota` settings and credentials with other unpackaged TaskbarQuota builds.
