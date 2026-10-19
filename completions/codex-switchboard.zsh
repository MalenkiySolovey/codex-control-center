#compdef codex-switchboard codex-switchboard.exe

_codex_switchboard() {
    local -a commands
    commands=(
        'list:List saved accounts and API providers'
        'current:Show the current target and model'
        'status:Show target and local profile counts'
        'export:Write a redacted JSON snapshot'
        'fleet:Show cached quota status for saved ChatGPT accounts'
        'schema:Print a versioned JSON Schema'
        'completion:Print a shell completion script'
        'help:Show command usage'
    )

    if (( CURRENT == 2 )); then
        _describe 'command' commands
        return
    fi

    case "$words[2]" in
        schema)
            _values 'schema' export fleet
            ;;
        completion)
            _values 'shell' powershell bash zsh
            ;;
        list|current|status|fleet)
            _values 'option' --json
            ;;
    esac
}

_codex_switchboard "$@"
