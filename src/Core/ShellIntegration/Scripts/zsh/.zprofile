# Source the user's login profile with their ZDOTDIR, then keep routing startup
# through the bundled .zshrc even when that profile selects another directory.
__resesh_injected_zdotdir=$ZDOTDIR
ZDOTDIR=${RESESH_SHELL_ZDOTDIR:-$HOME}
[[ -f "$ZDOTDIR/.zprofile" ]] && builtin source "$ZDOTDIR/.zprofile"
RESESH_SHELL_ZDOTDIR=${ZDOTDIR:-$HOME}
ZDOTDIR=$__resesh_injected_zdotdir
unset __resesh_injected_zdotdir
