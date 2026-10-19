_codex_switchboard_completions() {
    local current previous
    current="${COMP_WORDS[COMP_CWORD]}"
    previous="${COMP_WORDS[COMP_CWORD-1]}"

    if (( COMP_CWORD == 1 )); then
        COMPREPLY=( $(compgen -W 'list current status export fleet schema completion help' -- "$current") )
        return
    fi

    case "$previous" in
        schema) COMPREPLY=( $(compgen -W 'export fleet' -- "$current") ) ;;
        completion) COMPREPLY=( $(compgen -W 'powershell bash zsh' -- "$current") ) ;;
        list|current|status|fleet) COMPREPLY=( $(compgen -W '--json' -- "$current") ) ;;
    esac
}

complete -F _codex_switchboard_completions codex-switchboard codex-switchboard.exe
