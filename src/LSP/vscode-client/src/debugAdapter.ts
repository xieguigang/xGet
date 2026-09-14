import * as cp from 'child_process';
import {
    DebugSession,
    LoggingDebugSession,
    OutputEvent,
    TerminatedEvent,
    InitializedEvent
} from 'vscode-debugadapter';
import { DebugProtocol } from 'vscode-debugprotocol';

/**
 * Minimal DAP skeleton for launching VB.NET scripts through vbs.exe.
 *
 * This first version only wires up the "launch -> run -> capture output ->
 * terminate" main loop. Breakpoints / stepping / variable inspection are NOT
 * implemented yet: the base LoggingDebugSession answers those requests with a
 * graceful "not supported" error so the VS Code UI does not crash.
 */
interface LaunchRequestArguments extends DebugProtocol.LaunchRequestArguments {
    /** Absolute path of the .vb/.vbs script to execute. */
    program: string;
    /** Optional override for the vbs.exe host path. */
    vbsPath?: string;
    /** Extra command line arguments forwarded to the script. */
    args?: string[];
}

class VbNetDebugSession extends LoggingDebugSession {
    private child: cp.ChildProcess | undefined;
    private terminateRequested = false;

    constructor() {
        // The parameter enables protocol logging to ./vbnet-debug.txt.
        super('vbnet-debug.txt');
    }

    protected initializeRequest(
        response: DebugProtocol.InitializeResponse,
        _args: DebugProtocol.InitializeRequestArguments
    ): void {
        response.body = response.body || {};
        // We do not require a configurationDone handshake.
        response.body.supportsConfigurationDoneRequest = false;
        response.body.supportsTerminateRequest = true;
        this.sendResponse(response);
        this.sendEvent(new InitializedEvent());
    }

    protected launchRequest(
        response: DebugProtocol.LaunchResponse,
        args: LaunchRequestArguments
    ): void {
        const vbs =
            args.vbsPath && args.vbsPath.trim().length > 0
                ? args.vbsPath
                : 'vbs.exe';
        const program = args.program;

        if (!program) {
            this.sendErrorResponse(
                response,
                3001,
                'Missing "program" in launch configuration.'
            );
            return;
        }

        const spawnArgs = [program, ...(args.args || [])];
        this.sendEvent(
            new OutputEvent(
                `Launching: ${vbs} ${spawnArgs.join(' ')}\n`,
                'console'
            )
        );

        try {
            this.child = cp.spawn(vbs, spawnArgs, {
                stdio: ['ignore', 'pipe', 'pipe']
            });
        } catch (e) {
            this.sendErrorResponse(
                response,
                3002,
                `Failed to start vbs.exe: ${(e as Error).message}`
            );
            return;
        }

        this.child.stdout?.on('data', (d: Buffer) => {
            this.sendEvent(new OutputEvent(d.toString(), 'stdout'));
        });
        this.child.stderr?.on('data', (d: Buffer) => {
            this.sendEvent(new OutputEvent(d.toString(), 'stderr'));
        });
        this.child.on('error', (err) => {
            this.sendEvent(
                new OutputEvent(`[error] ${err.message}\n`, 'stderr')
            );
            this.sendEvent(new TerminatedEvent());
        });
        this.child.on('close', (code) => {
            this.sendEvent(
                new OutputEvent(
                    `\n[process exited with code ${code}]\n`,
                    'console'
                )
            );
            if (!this.terminateRequested) {
                this.sendEvent(new TerminatedEvent());
            }
        });

        this.sendResponse(response);
    }

    protected disconnectRequest(
        response: DebugProtocol.DisconnectResponse,
        _args: DebugProtocol.DisconnectArguments
    ): void {
        this.terminateChild();
        this.sendResponse(response);
    }

    protected terminateRequest(
        response: DebugProtocol.TerminateResponse,
        _args: DebugProtocol.TerminateArguments
    ): void {
        this.terminateChild();
        this.sendResponse(response);
    }

    private terminateChild(): void {
        this.terminateRequested = true;
        if (this.child) {
            this.child.kill();
            this.child = undefined;
        }
    }
}

DebugSession.run(VbNetDebugSession);
