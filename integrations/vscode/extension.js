'use strict';

const vscode = require('vscode');
const { spawn } = require('node:child_process');

const MAX_OUTPUT_BYTES = 512 * 1024;

function activate(context) {
    context.subscriptions.push(
        vscode.commands.registerCommand('codexSwitchboard.showStatus', showStatus),
        vscode.commands.registerCommand('codexSwitchboard.showFleet', showFleet));
}

function deactivate() {}

async function showStatus() {
    try {
        const status = await readJson(['status', '--json']);
        const target = status.target || {};
        const model = target.model ? ` · ${target.model}` : '';
        const counts = `${status.savedChatGptAccounts ?? 0} ChatGPT accounts · ${status.apiProviders ?? 0} API providers`;
        await vscode.window.showInformationMessage(`Codex target: ${target.name || 'Unknown'}${model} · ${counts}`);
    } catch {
        await showCliError();
    }
}

async function showFleet() {
    try {
        const fleet = await readJson(['fleet', '--json']);
        if (fleet.schemaVersion !== 1 || fleet.source !== 'local-cache' || !Array.isArray(fleet.accounts)) {
            throw new Error('Unsupported fleet response.');
        }
        const accounts = fleet.accounts;
        if (accounts.length === 0) {
            await vscode.window.showInformationMessage('No saved ChatGPT accounts were found in Switchboard.');
            return;
        }

        const items = accounts.map((account) => {
            const rate = account.rateLimited === true
                ? 'rate limited'
                : account.rateLimited === false
                    ? account.status || 'status unknown'
                    : account.status || 'quota unknown';
            const freshness = account.isStale === true
                ? 'cached · stale'
                : account.isStale === false
                    ? 'cached'
                    : 'no cached quota';
            const windows = (Array.isArray(account.windows) ? account.windows : []).map((window) => {
                const remaining = Number.isFinite(window.remainingPercent)
                    ? `${window.remainingPercent}% remaining`
                    : 'remaining unknown';
                const resetDate = window.resetsAt ? new Date(window.resetsAt) : null;
                const reset = resetDate && Number.isFinite(resetDate.getTime())
                    ? ` · resets ${resetDate.toLocaleString()}`
                    : '';
                return `${window.label || 'Quota'}: ${remaining}${reset}`;
            });

            return {
                label: account.name || 'ChatGPT account',
                description: `${rate} · ${freshness}`,
                detail: windows.length > 0 ? windows.join('  |  ') : 'No cached quota windows',
            };
        });

        const summary = fleet.summary || {};
        await vscode.window.showQuickPick(items, {
            title: 'Codex Switchboard · Cached account fleet',
            placeHolder: `${summary.savedAccountCount ?? items.length} saved accounts · ${summary.cachedSnapshotCount ?? 0} cached snapshots · local cache only`,
            matchOnDescription: true,
            matchOnDetail: true,
        });
    } catch {
        await showCliError();
    }
}

async function readJson(args) {
    if (process.platform !== 'win32') {
        throw new Error('The Switchboard CLI currently targets Windows.');
    }

    const configuredPath = vscode.workspace.getConfiguration('codexSwitchboard').get('cliPath', 'codex-switchboard.exe');
    const executable = typeof configuredPath === 'string' ? configuredPath.trim() : '';
    if (!executable) {
        throw new Error('CLI path is empty.');
    }

    const output = await runCli(executable, args);
    return JSON.parse(output);
}

function runCli(executable, args) {
    return new Promise((resolve, reject) => {
        let child;
        let settled = false;
        let totalBytes = 0;
        const chunks = [];
        const finish = (error, value) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            if (error) reject(error);
            else resolve(value);
        };

        const timer = setTimeout(() => {
            child?.kill();
            finish(new Error('Switchboard CLI timed out.'));
        }, 15000);

        try {
            child = spawn(executable, args, {
                shell: false,
                windowsHide: true,
                stdio: ['ignore', 'pipe', 'ignore'],
            });
        } catch {
            finish(new Error('Switchboard CLI could not be started.'));
            return;
        }

        child.stdout.on('data', (chunk) => {
            totalBytes += chunk.length;
            if (totalBytes > MAX_OUTPUT_BYTES) {
                child.kill();
                finish(new Error('Switchboard CLI output was too large.'));
                return;
            }
            chunks.push(chunk);
        });
        child.on('error', () => finish(new Error('Switchboard CLI could not be started.')));
        child.on('close', (code) => {
            if (code !== 0) {
                finish(new Error('Switchboard CLI returned an error.'));
                return;
            }
            finish(null, Buffer.concat(chunks).toString('utf8'));
        });
    });
}

async function showCliError() {
    await vscode.window.showErrorMessage(
        'Could not read local Switchboard data. Check that codexSwitchboard.cliPath points to a built codex-switchboard.exe.'
    );
}

module.exports = { activate, deactivate };
