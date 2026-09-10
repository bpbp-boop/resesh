# Shell integration for PowerShell: OSC 133 prompt marks and OSC 7 working directory.
#
# Loaded by `pwsh -NoExit -Command ". <this file>"`. That runs AFTER the profiles, so
# $function:prompt is whatever the user -- or oh-my-posh, or starship -- installed, and it is
# wrapped rather than replaced. Nothing of the user's is displaced, and -NoProfile still works,
# because this arrives by -Command rather than by profile.
#
# Kept 5.1-compatible on purpose. `e, the ternary and ?? are 7-only, and Windows PowerShell is
# still the default shell on a great many machines that Confetty will land on first.

if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') { return }

# A shell that dot-sources this twice would emit every mark twice.
if (Test-Path variable:Global:__ReseshIntegration) { return }

$Global:__ReseshIntegration = @{
    OriginalPrompt   = $function:prompt
    OriginalReadLine = $null
    CommandRunning   = $false
    CommandIsNative  = $false
    LastHistoryId    = -1    # -1 until the first prompt has seen where history stands
}

function Global:__Resesh-Osc([string] $Body) {
    $osc = "$([char]27)]$Body$([char]7)"
    if ($env:RESESH_SHELL_TMUX -eq '1') { return "$([char]27)Ptmux;$($osc.Replace([string][char]27, ([string][char]27 + [char]27)))$([char]27)\" }
    return $osc
}

function Global:__Resesh-ReportCwd {
    if ($ExecutionContext.SessionState.Path.CurrentLocation.Provider.Name -ne 'FileSystem') { return '' }
    $location = $ExecutionContext.SessionState.Path.CurrentLocation.ProviderPath
    if ($null -eq $location -or $location -match '[\x00-\x1f\x7f]') { return '' }

    # $IsWindows is 7-only; on 5.1 it is simply absent, and $env:OS is the older tell.
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        # OSC 9;9 with a bare quoted path -- the form Microsoft's own prompts emit and the
        # emulator already reads. A file:// URI for C:\Users would decode to /C:/Users, a leading
        # slash that no Windows API accepts.
        return (__Resesh-Osc "9;9;`"$location`"")
    }

    # Not $host, which is PowerShell's own automatic variable and must not be assigned.
    $machine = [Environment]::MachineName
    return (__Resesh-Osc "7;file://$machine$($location.Replace('%', '%25'))")
}

# The C mark -- "output begins here" -- has to go out after Enter and before the command runs,
# and the only hook that sits there is PSReadLine's line reader. It is wrapped, not replaced, so
# every key handler the user configured keeps working; this only looks at the line it returns.
function Global:__Resesh-InstallReadLineHook {
    $state = $Global:__ReseshIntegration
    if ($null -ne $state.OriginalReadLine) { return }

    $existing = Get-Command -Name PSConsoleHostReadLine -CommandType Function -ErrorAction SilentlyContinue
    if ($null -eq $existing) { return }

    $state.OriginalReadLine = $existing.ScriptBlock

    function Global:PSConsoleHostReadLine {
        $line = & $Global:__ReseshIntegration.OriginalReadLine
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $Global:__ReseshIntegration.CommandRunning = $true
            $Global:__ReseshIntegration.CommandIsNative = $false
            # Only a single, direct native invocation proves LASTEXITCODE belongs
            # to this command. Compound commands and cmdlets use conservative 0/1.
            $parseTokens = $null
            $parseErrors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($line, [ref]$parseTokens, [ref]$parseErrors)
            if ($parseErrors.Count -eq 0 -and $null -ne $ast.EndBlock -and $ast.EndBlock.Statements.Count -eq 1) {
                $pipeline = $ast.EndBlock.Statements[0]
                if ($pipeline -is [System.Management.Automation.Language.PipelineAst] -and $pipeline.PipelineElements.Count -eq 1) {
                    $command = $pipeline.PipelineElements[0]
                    if ($command -is [System.Management.Automation.Language.CommandAst]) {
                        $name = $command.GetCommandName()
                        if ($null -ne $name) {
                            $resolved = @(Get-Command -Name $name -ErrorAction SilentlyContinue)
                            $Global:__ReseshIntegration.CommandIsNative = $resolved.Count -eq 1 -and $resolved[0].CommandType -eq 'Application'
                        }
                    }
                }
            }
            [Console]::Write((__Resesh-Osc '133;C'))
        }
        return $line
    }
}

function Global:prompt {
    # FIRST, before anything in here can overwrite them. $? is the success of the last
    # statement the user ran; $LASTEXITCODE is the code of the last NATIVE command, which a
    # failing cmdlet leaves untouched -- so $? decides whether $LASTEXITCODE is even relevant.
    $succeeded = $?
    $lastExit = $global:LASTEXITCODE

    $state = $Global:__ReseshIntegration
    $out = ''

    # PSReadLine loads AFTER -Command finishes and interactive mode begins, so it was not there
    # when this file ran. Install the hook the first time the prompt draws, when it is.
    __Resesh-InstallReadLineHook

    # Without PSReadLine there is no C mark, but history still says whether a command ran.
    #
    # Measured from the first prompt rather than from zero: pwsh records the -Command that
    # loaded this file as history entry 1, so "history advanced" was true before the user had
    # typed anything, and the very first prompt carried a D for a command nobody ran.
    $last = Get-History -Count 1 -ErrorAction SilentlyContinue
    $historyAdvanced = $false
    if ($state.LastHistoryId -lt 0) {
        $state.LastHistoryId = 0
        if ($null -ne $last) { $state.LastHistoryId = $last.Id }
    } elseif ($null -ne $last) {
        $historyAdvanced = $last.Id -gt $state.LastHistoryId
    }

    if ($state.CommandRunning -or $historyAdvanced) {
        $code = 0
        if (-not $succeeded) {
            $code = 1
            if ($state.CommandIsNative -and $lastExit -is [int] -and $lastExit -ne 0) { $code = $lastExit }
        }
        $out += __Resesh-Osc "133;D;$code"
        $state.CommandRunning = $false
        $state.CommandIsNative = $false
    }

    if ($null -ne $last) { $state.LastHistoryId = $last.Id }

    $out += __Resesh-ReportCwd
    $out += __Resesh-Osc '133;A'

    # The user's own prompt, whatever installed it. A ScriptBlock invoked returns a collection,
    # hence the join. If there was no prompt function at all, draw PowerShell's default.
    $original = $state.OriginalPrompt
    if ($null -ne $original) {
        $global:LASTEXITCODE = $lastExit
        # Restore the success signal immediately before invoking the user's prompt.
        # Ignore suppresses both display and insertion into the user's $Error list.
        if (-not $succeeded) { Write-Error 'Resesh prompt status' -ErrorAction Ignore }
        $out += ((& $original) -join '')
    } else {
        $out += "PS $($ExecutionContext.SessionState.Path.CurrentLocation)$('>' * ($NestedPromptLevel + 1)) "
    }

    $out += __Resesh-Osc '133;B'
    $global:LASTEXITCODE = $lastExit
    return $out
}
