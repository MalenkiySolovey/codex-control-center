Register-ArgumentCompleter -Native -CommandName codex-switchboard,codex-switchboard.exe -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)

    $elements = @($commandAst.CommandElements)
    $parts = @($elements | ForEach-Object { $_.Extent.Text })
    $arguments = @($parts | Select-Object -Skip 1)
    $currentExtent = if ($elements.Count -gt 1) { $elements[-1].Extent } else { $null }
    $currentIsArgument = $null -ne $currentExtent -and
        $currentExtent.StartOffset -le $cursorPosition -and
        $currentExtent.EndOffset -ge $cursorPosition
    if ($currentIsArgument) {
        if ($arguments.Count -le 1) {
            $arguments = @()
        }
        else {
            $arguments = @($arguments[0..($arguments.Count - 2)])
        }
    }

    $candidates = @()
    if ($arguments.Count -eq 0) {
        $candidates = @('list', 'current', 'status', 'export', 'fleet', 'schema', 'completion', 'help')
    }
    elseif ($arguments.Count -eq 1) {
        switch ($arguments[0]) {
            'schema' { $candidates = @('export', 'fleet') }
            'completion' { $candidates = @('powershell', 'bash', 'zsh') }
            { $_ -in @('list', 'current', 'status', 'fleet') } { $candidates = @('--json') }
        }
    }
    elseif ($arguments.Count -eq 2 -and $arguments[0] -in @('list', 'current', 'status', 'fleet')) {
        $candidates = @('--json')
    }

    $candidates |
        Where-Object { $_ -like "$wordToComplete*" } |
        ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
}
