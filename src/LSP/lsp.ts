import * as vscode from 'vscode';
import * as net from 'net';
import { LanguageClient, LanguageClientOptions, StreamInfo } from 'vscode-languageclient/node';

export function activate(context: vscode.Context) {
    const config = vscode.workspace.getConfiguration('myRemoteLsp');
    const host = config.get<string>('host') || '127.0.0.1';
    const port = config.get<number>('port') || 8080;

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

    // 2. 配置客户端选项，指定对 VB.NET 文件生效
    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'vbnet' }] // 注意这里的语言 ID
    };

    // 3. 创建并启动客户端
    const client = new LanguageClient(
        'remoteVbnetLsp',
        'Remote VB.NET LSP',
        serverOptions,
        clientOptions
    );

    context.subscriptions.push(client.start());
}
