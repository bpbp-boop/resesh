# Shell integration for bash: OSC 133 prompt marks and OSC 7 working directory.
#
# Sourced through $ENV with bash started in POSIX mode, because --init-file is silently ignored for
# a login shell and POSIX mode sources $ENV whatever kind of shell it is. The price is here: this
# has to leave POSIX mode and then replay bash's own startup sequence, since bash skipped it.
#
# The mechanism follows Ghostty's, which is MIT licensed.

# Only when we put it there. Anything else sourcing this is not what it was written for.
if [ -z "${RESESH_SHELL_BASH_INJECT+x}" ]; then
  return 0 2>/dev/null || exit 0
fi

__resesh_flags="$RESESH_SHELL_BASH_INJECT"
unset RESESH_SHELL_BASH_INJECT ENV

# Back to being ordinary bash. inherit_errexit comes with POSIX mode and is not what the user asked
# for either.
builtin set +o posix
builtin shopt -u inherit_errexit 2>/dev/null

# Give back the user's own ENV, which POSIX mode displaced.
if [ -n "${RESESH_SHELL_BASH_ENV+x}" ]; then
  ENV="$RESESH_SHELL_BASH_ENV"
  builtin export ENV
  unset RESESH_SHELL_BASH_ENV
fi

# ---- replay what bash would have read -------------------------------------------------------
#
# bash starts differently depending on whether it is a login shell, and POSIX mode meant it read
# none of it. Getting this wrong loses the user's configuration silently, which is worse than
# having no integration at all.

case " $__resesh_flags " in
  *" login "*) __resesh_login=1 ;;
  *) __resesh_login=0 ;;
esac

case " $__resesh_flags " in
  *" noprofile "*) __resesh_noprofile=1 ;;
  *) __resesh_noprofile=0 ;;
esac

case " $__resesh_flags " in
  *" norc "*) __resesh_norc=1 ;;
  *) __resesh_norc=0 ;;
esac

# A leading dash on the shell's own name is the other way a login shell is spelled.
case "$0" in
  -*) __resesh_login=1 ;;
esac

if [ "$__resesh_login" = 1 ]; then
  if [ "$__resesh_noprofile" != 1 ]; then
    [ -r /etc/profile ] && builtin source /etc/profile

    # The first of these that exists, and only the first — which is bash's own rule.
    for __resesh_rc in "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile"; do
      if [ -r "$__resesh_rc" ]; then
        builtin source "$__resesh_rc"
        break
      fi
    done
  fi
else
  if [ "$__resesh_norc" != 1 ]; then
    # Distributions disagree about where the system one lives.
    for __resesh_rc in /etc/bash.bashrc /etc/bash/bashrc /etc/bashrc; do
      if [ -r "$__resesh_rc" ]; then
        builtin source "$__resesh_rc"
        break
      fi
    done

    [ -r "$HOME/.bashrc" ] && builtin source "$HOME/.bashrc"
  fi
fi

unset __resesh_flags __resesh_login __resesh_noprofile __resesh_norc __resesh_rc

# ---- the integration itself ------------------------------------------------------------------

__resesh_osc() {
  if [[ ${RESESH_SHELL_TMUX:-} == 1 ]]; then
    builtin printf '\033Ptmux;\033\033]%s\007\033\\' "$1"
  else
    builtin printf '\033]%s\007' "$1"
  fi
}

# Working directory, so a new tab can open where this one is.
__resesh_report_cwd() {
  local cwd=$PWD windows_cwd
  # Control characters cannot be embedded unescaped in terminal control strings.
  [[ $cwd == *[$'\001'-$'\037'$'\177']* ]] && return
  # Git Bash/MSYS and Cygwin use virtual mount paths such as /c and /usr.
  # Report the actual Windows directory so local file actions resolve correctly.
  case ${OSTYPE:-} in
    msys*|cygwin*)
      if windows_cwd=$(command cygpath -am -- "$PWD" 2>/dev/null); then
        case $windows_cwd in
          [a-zA-Z]:/*) cwd=/$windows_cwd ;;
          //*) cwd=$windows_cwd ;;
        esac
      fi
      ;;
  esac
  [[ $cwd == *[$'\001'-$'\037'$'\177']* ]] && return
  # Only the percent needs escaping. The consumer unescapes what arrives, so a directory whose
  # name contains a valid-looking sequence -- a%2Fb -- would come back as a/b, silently wrong.
  # Everything else survives unescaping untouched, so encoding it would be work for nothing.
  __resesh_osc "7;file://${HOSTNAME:-}${cwd//%/%25}"
}

# Runs just before each prompt: report the last command's exit status, then mark the new prompt.
__resesh_precmd() {
  local status=$?

  # PS0 assigns the running flag in the parent shell only for a parsed command.
  # The first prompt and blank Enter have no command to complete.
  if [ -n "${__resesh_running:-}" ]; then
    __resesh_osc "133;D;$status"
  fi

  unset __resesh_running

  __resesh_report_cwd
  __resesh_osc "133;A"
  return "$status"
}

# Prompt frameworks commonly replace PS1 from PROMPT_COMMAND. Add B afterwards
# on every prompt, preserving readline's zero-width delimiters and avoiding duplicates.
__resesh_prompt_end() {
  local status=$?
  if [[ "${PS1:-}" != *'133;B'* ]]; then
    local marker=$(__resesh_osc '133;B')
    # Bash decodes backslashes in PS1 before expanding the prompt. The tmux DCS
    # terminator contains a literal backslash; quote it so it cannot consume
    # readline's closing \] delimiter and leave the prompt width unbalanced.
    marker=${marker//\\/\\\\}
    PS1="${PS1:-}"'\['"$marker"'\]'
  fi
  return "$status"
}

# Run before the user's hooks to capture the command status, then after them to
# decorate their final PS1. Newlines preserve scalar hooks ending in a comment.
if [[ "${PROMPT_COMMAND:-}" != *__resesh_precmd* ]]; then
  if [[ "$(builtin declare -p PROMPT_COMMAND 2>/dev/null)" == "declare -a"* ]]; then
    PROMPT_COMMAND=(__resesh_precmd "${PROMPT_COMMAND[@]}" __resesh_prompt_end)
  else
    PROMPT_COMMAND=$'__resesh_precmd\n'"${PROMPT_COMMAND:-}"$'\n__resesh_prompt_end'
  fi
fi

# Parameter assignment happens in the parent shell; PS0 is expanded only for a
# parsed command. Blank Enter therefore never creates a synthetic completion.
PS0='${__resesh_running:=$(__resesh_osc "133;C")}'"${PS0:-}"
