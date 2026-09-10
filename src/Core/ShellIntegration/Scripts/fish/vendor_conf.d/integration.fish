# Shell integration for fish: OSC 133 prompt marks and OSC 7 working directory.
#
# Sourced by --init-command after fish has loaded the user's configuration.
# Existing startup files and the user's prompt remain in place.
#
# The mechanism follows Ghostty's, which is MIT licensed.

status is-interactive; or return
if functions -q __resesh_osc
    return
end
function __resesh_osc
    if test "$RESESH_SHELL_TMUX" = 1
        printf '\033Ptmux;\033\033]%s\007\033\\' "$argv[1]"
    else
        printf '\033]%s\007' "$argv[1]"
    end
end

# fish gives these as real events, so there is no prompt string to splice and no width to get wrong.

function __resesh_report_cwd --on-variable PWD --description 'report the working directory'
    string match -qr '[\x00-\x1f\x7f]' -- "$PWD"; and return
    # $hostname, not (hostname): the variable is built in, while the command forks a process —
    # and this runs on every directory change, in a shell whose whole appeal is being quick.
    # Only the percent needs escaping — see the note in the bash script. string replace is a
    # builtin, so this still forks nothing.
    __resesh_osc "7;file://$hostname"(string replace --all '%' '%25' -- "$PWD")
end

function __resesh_prompt_start --on-event fish_prompt --description 'mark the prompt'
    # D carries the status of the command that just finished. Nothing has run before the first
    # prompt, so there is nothing to report then.
    if set -q __resesh_command_running
        __resesh_osc "133;D;$__resesh_last_status"
        set -e __resesh_command_running
    end

    __resesh_report_cwd
    __resesh_osc '133;A'
end

function __resesh_preexec --on-event fish_preexec --description 'mark where output begins'
    set -g __resesh_command_running 1
    __resesh_osc '133;C'
end

function __resesh_postexec --on-event fish_postexec --description 'remember the exit status'
    set -g __resesh_last_status $status
end

# B marks the end of the prompt and the start of typing. Wrapping the user's fish_prompt rather than
# appending to a string, because fish builds its prompt from a function.
if not functions -q __resesh_original_fish_prompt
    functions -c fish_prompt __resesh_original_fish_prompt

    function fish_prompt --description 'fish_prompt, with the end of it marked'
        __resesh_original_fish_prompt
        __resesh_osc '133;B'
    end
end

# The first prompt has no preceding command, and the report above needs somewhere to read from.
set -g __resesh_last_status 0
