# Shell integration for zsh: OSC 133 prompt marks and OSC 7 working directory.
#
# Reached by pointing ZDOTDIR here. zsh reads four files from ZDOTDIR, so all four exist beside this
# one and forward to the user's — providing only .zshrc would silently drop the rest of their setup.
#
# The mechanism follows Ghostty's, which is MIT licensed.

# Put ZDOTDIR back FIRST. Anything the user's own files spawn must see their directory, not this
# one, or a nested zsh reads this instead of their configuration.
if [[ -n "$RESESH_SHELL_ZDOTDIR" ]]; then
  ZDOTDIR="$RESESH_SHELL_ZDOTDIR"
else
  ZDOTDIR="$HOME"
fi

[[ -f "$ZDOTDIR/.zshrc" ]] && builtin source "$ZDOTDIR/.zshrc"

# ---- the integration itself ------------------------------------------------------------------

autoload -Uz add-zsh-hook
__resesh_osc() {
  if [[ ${RESESH_SHELL_TMUX:-} == 1 ]]; then
    builtin printf '\033Ptmux;\033\033]%s\007\033\\' "$1"
  else
    builtin printf '\033]%s\007' "$1"
  fi
}

__resesh_report_cwd() {
  [[ $PWD == *[$'\001'-$'\037'$'\177']* ]] && return
  # Only the percent needs escaping. The consumer unescapes what arrives, so a directory whose
  # name contains a valid-looking sequence -- a%2Fb -- would come back as a/b, silently wrong.
  # Everything else survives unescaping untouched, so encoding it would be work for nothing.
  __resesh_osc "7;file://${HOST:-}${PWD//\%/%25}"
}

# Before each prompt: the status of the command that just finished, then the new prompt.
__resesh_precmd() {
  # NOT named "status": in zsh that is a special parameter aliased to $?, and declaring it local
  # makes this function fail silently — the prompt still appeared and only the A and D marks went
  # missing, which is a hard symptom to read backwards.
  local __resesh_status=$?

  if [[ -n "$__resesh_command_running" ]]; then
    __resesh_osc "133;D;$__resesh_status"
    unset __resesh_command_running
  fi

  __resesh_report_cwd
  __resesh_osc '133;A'
  return "$__resesh_status"
}

# Before a command runs: the prompt is over and output begins.
__resesh_preexec() {
  __resesh_command_running=1
  __resesh_osc '133;C'
}

# add-zsh-hook appends, so the user's own precmd and preexec keep running.
precmd_functions=(__resesh_precmd ${precmd_functions:#__resesh_precmd})
add-zsh-hook preexec __resesh_preexec

# B marks where the prompt ends and typing begins. %{ %} tells zsh the sequence occupies no columns;
# without it the prompt's measured width is wrong and line wrapping goes with it.
__resesh_prompt_end() {
  local __resesh_status=$?
  if [[ "$PS1" != *'133;B'* ]]; then
    PS1="$PS1%{$(__resesh_osc '133;B')%}"
  fi
  return "$__resesh_status"
}
add-zsh-hook precmd __resesh_prompt_end
