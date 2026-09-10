# Preserve user startup files, including a ZDOTDIR selected inside .zshenv.
__resesh_injected_zdotdir=$ZDOTDIR
if (( ${+RESESH_SHELL_ZDOTDIR} )); then
  ZDOTDIR=$RESESH_SHELL_ZDOTDIR
else
  unset ZDOTDIR
fi
[[ -f ${ZDOTDIR:-$HOME}/.zshenv ]] && builtin source "${ZDOTDIR:-$HOME}/.zshenv"
RESESH_SHELL_ZDOTDIR=${ZDOTDIR:-$HOME}
ZDOTDIR=$__resesh_injected_zdotdir
unset __resesh_injected_zdotdir
