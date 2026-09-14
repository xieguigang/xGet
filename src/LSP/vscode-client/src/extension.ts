import * as vscode from 'vscode';
import * as net from 'net';
import { LanguageClient, LanguageClientOptions, StreamInfo } from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

export function activate(context: vscode.ExtensionContext) {
    const config = vscode.workspace.getConfiguration('myRemoteLsp');
    const host = config.get<string>('host') || 'vscode_lsp.scibasic.net';
    const port = config.get<number>('port') || 8088;

    // 1. 定义如何连接到 TCP 服务器
    const serverOptions = () => {
        return new Promise<StreamInfo>((resolve, reject) => {
            const socket = new net.Socket();
            socket.connect(port, host, () => {
                // 连接成功，将 socket 的读写流交给 LanguageClient
                resolve({
                    reader: socket,
                    writer: socket
                });
            });
            socket.on('error', (err) => reject(err));
        });
    };

    // 2. 配置客户端选项，指定对 VB.NET 文件（扩展注册的 vbnet 语言）生效
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'vbnet' }]
    };

    // 3. 创建并启动客户端
    client = new LanguageClient(
        'remoteVbnetLsp',
        'Remote VB.NET LSP',
        serverOptions,
        clientOptions
    );

    context.subscriptions.push(
        new vscode.Disposable(() => {
            if (client) {
                void client.stop();
            }
        })
    );

    client.start().then(
        () => console.log('Remote VB.NET LSP started.'),
        (err) => console.error('Remote VB.NET LSP failed to start:', err)
    );
}

export function deactivate() {
    if (client) {
        void client.stop();
        client = undefined;
    }
}
